using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace erp_inteligente.Models;

/// <summary>One step of a draft: a catalog operation, its arguments and an optional result name for <c>$references</c>.</summary>
public sealed record PlanDraftStep(string Op, IReadOnlyDictionary<string, object?> Args, string? As = null);

/// <summary>
/// A plan not yet submitted, as a DSL v1 JSON document (references/ARQUITETURA.md, section 5). Drafts come
/// from the "Novo plano" screen and, from feature 3 on, from the Planner; the engine validates everything.
/// </summary>
public sealed class PlanDraft
{
    public const string DslVersion = "1.0";

    private PlanDraft(string json) => Json = json;

    /// <summary>The DSL document exactly as it will be validated and stored in <c>plans.plan_json</c>.</summary>
    public string Json { get; }

    /// <summary>Wraps a DSL document received as text (e.g. from the Planner); only JSON syntax is checked here.</summary>
    public static PlanDraft FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var _ = JsonDocument.Parse(json);
        return new PlanDraft(json);
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
                args[name] = value is null ? null : JsonValue.Create(value);
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
        return new PlanDraft(document.ToJsonString());
    }
}
