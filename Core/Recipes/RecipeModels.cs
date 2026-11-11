using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace erp_inteligente.Core.Recipes;

/// <summary>Life cycle of a recipe (references/ARQUITETURA.md, section 7). Stored as lowercase text in <c>recipes.status</c>.</summary>
public enum RecipeStatus
{
    /// <summary>Detected from repeated plans; waits for a supervisor to promote it.</summary>
    Candidate,

    /// <summary>Usable from the Receitas screen and by the Recipe Matcher.</summary>
    Active,

    /// <summary>The DSL or the skill changed (or a supervisor paused it): must be revalidated.</summary>
    Suspended,

    /// <summary>Failed repeatedly or was retired by a supervisor; kept for history, never deleted.</summary>
    Demoted,
}

public static class RecipeStatuses
{
    public static string ToDbValue(this RecipeStatus status) => status switch
    {
        RecipeStatus.Candidate => "candidate",
        RecipeStatus.Active => "active",
        RecipeStatus.Suspended => "suspended",
        RecipeStatus.Demoted => "demoted",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static RecipeStatus FromDbValue(string value) => value switch
    {
        "candidate" => RecipeStatus.Candidate,
        "active" => RecipeStatus.Active,
        "suspended" => RecipeStatus.Suspended,
        "demoted" => RecipeStatus.Demoted,
        _ => throw new ArgumentException($"Unknown recipe status '{value}'.", nameof(value)),
    };
}

/// <summary>One value the operator (or the Matcher) fills in: name, Portuguese label, JSON type and an example.</summary>
public sealed record RecipeParameter(string Name, string Label, string Type, bool Required, string? Example);

/// <summary>A row of <c>recipes</c> with its parameters already described for the UI.</summary>
public sealed record RecipeRecord(
    long Id,
    string Name,
    string Description,
    string Intent,
    string? Skill,
    string SkillVersion,
    string DslVersion,
    RecipeStatus Status,
    long SuccessCount,
    long FailureCount,
    long FailureStreak,
    string TemplateJson,
    string ParamsSchemaJson,
    string? ExampleParamsJson,
    string? Signature,
    long? SourcePlanId,
    string? PromotedByName,
    string? CreatedAt,
    string? PromotedAt,
    string? LastUsedAt,
    string? StatusReason)
{
    /// <summary>The parameters in template order, derived from <see cref="ParamsSchemaJson"/>.</summary>
    public IReadOnlyList<RecipeParameter> Parameters { get; init; } = [];

    /// <summary>The operations of the template, in step order.</summary>
    public IReadOnlyList<string> Operations { get; init; } = [];
}

/// <summary>A plan built from a recipe: the DSL document, or the reasons the parameters were refused.</summary>
public sealed record RecipeInstance(string? PlanJson, IReadOnlyList<string> Errors)
{
    public bool IsValid => PlanJson is not null && Errors.Count == 0;
}

/// <summary>What <see cref="RecipeTemplate.Extract"/> derives from an executed plan.</summary>
public sealed record ExtractedTemplate(string TemplateJson, string ParamsSchemaJson, JsonObject ExampleParams, string Signature);

/// <summary>A business error of the recipe store (pt-BR message, safe to show).</summary>
public sealed class RecipeStoreException(string message) : Exception(message);
