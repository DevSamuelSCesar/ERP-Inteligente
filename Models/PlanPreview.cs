using System.Collections.Generic;

namespace erp_inteligente.Models;

/// <summary>A validation problem in business language; <see cref="StepNumber"/> is 1-based, null for the whole plan.</summary>
public sealed record PlanIssue(int? StepNumber, string Message);

/// <summary>Result of a dry run: the issues, or the steps, risk and simulated effect. Nothing was written.</summary>
public sealed record PlanPreview(
    IReadOnlyList<PlanIssue> Issues,
    IReadOnlyList<PlanStep> Steps,
    PlanRisk Risk,
    ExecutionSummary? Effect)
{
    public bool IsValid => Issues.Count == 0;
}
