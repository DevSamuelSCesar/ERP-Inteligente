using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace erp_inteligente.Models;

/// <summary>
/// Who wrote a draft: the operator in "Novo plano", the Planner, or a promoted recipe (feature 7). A draft of the AI runs
/// with AI ∩ user rights; a recipe draft runs with the requester's rights, like the operator's own.
/// </summary>
public enum PlanOrigin
{
    User,
    Ai,
    Recipe,
}

/// <summary>One step of a draft: a catalog operation, its arguments and an optional result name for <c>$references</c>.</summary>
public sealed record PlanDraftStep(string Op, IReadOnlyDictionary<string, object?> Args, string? As = null);

/// <summary>
/// A plan not yet submitted, as a DSL v1 JSON document (references/ARQUITETURA.md, section 5). Drafts come
/// from the "Novo plano" screen and, from feature 3 on, from the Planner; the engine validates everything.
/// </summary>
public sealed class PlanDraft
{
    public const string DslVersion = "1.0";

    private PlanDraft(string json, PlanOrigin origin, string? skill, long? recipeId = null, IReadOnlyList<long>? memoryIds = null)
    {
        MemoryIds = origin == PlanOrigin.Ai ? memoryIds ?? [] : [];
        Json = json;
        Origin = origin;
        Skill = skill;
        RecipeId = origin == PlanOrigin.Recipe ? recipeId : null;
    }

    /// <summary>The recipe the draft was built from (only for <see cref="PlanOrigin.Recipe"/>); stored in <c>plans.recipe_id</c>.</summary>
    public long? RecipeId { get; }

    /// <summary>The memories the Planner had in front of it (only for <see cref="PlanOrigin.Ai"/>): their confidence follows the plan's outcome.</summary>
    public IReadOnlyList<long> MemoryIds { get; }

    /// <summary>The DSL document exactly as it will be validated and stored in <c>plans.plan_json</c>.</summary>
    public string Json { get; }

    public PlanOrigin Origin { get; }

    /// <summary>The <c>skill</c> field when it is a string, for display only; the engine validates it.</summary>
    public string? Skill { get; }

    /// <summary>Wraps a DSL document received as text (e.g. from the Planner); only JSON syntax is checked here.</summary>
    public static PlanDraft FromJson(string json, PlanOrigin origin = PlanOrigin.User, long? recipeId = null, IReadOnlyList<long>? memoryIds = null)
    {
        if (origin == PlanOrigin.Recipe && recipeId is null)
            throw new ArgumentException("A recipe draft needs the recipe id.", nameof(recipeId));
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        var skill = document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("skill", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
        return new PlanDraft(json, origin, skill, recipeId, memoryIds);
    }

    /// <summary>Builds a DSL v1 document from typed steps.</summary>
    public static PlanDraft Create(string intent, string? skill, IReadOnlyList<PlanDraftStep> steps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intent);
        var array = new JsonArray();
        foreach (var step in steps)
        {
            var args = new JsonObject();
            foreach (var (name, value) in step.Args)
                args[name] = value is null ? null : JsonSerializer.SerializeToNode(value);
            var node = new JsonObject { ["op"] = step.Op, ["args"] = args };
            if (step.As is not null)
                node["as"] = step.As;
            array.Add(node);
        }
        var document = new JsonObject
        {
            ["dsl_version"] = DslVersion,
            ["intent"] = intent,
            ["skill"] = skill,
            ["steps"] = array,
        };
        return new PlanDraft(document.ToJsonString(), PlanOrigin.User, skill);
    }
}
