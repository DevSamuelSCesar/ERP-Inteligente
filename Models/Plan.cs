using System;
using System.Collections.Generic;
using System.Linq;

namespace erp_inteligente.Models;

public enum PlanRisk
{
    Reversible,
    Critical,
}

/// <summary>Plan lifecycle, mirroring <c>plans.status</c> (references/ARQUITETURA.md, section 11).</summary>
public enum PlanStatus
{
    Validating,
    PendingApproval,
    Rejected,
    Executed,
    Failed,
}

/// <summary>Write operations of the DSL v1 catalog (references/ARQUITETURA.md, section 5).</summary>
public enum PlanOperation
{
    ReceiveGoods,
    MoveStock,
    AdjustStock,
    CreateProduct,
    CreateLocation,
}

/// <summary>One step of a plan, already expressed in business terms for display.</summary>
public sealed record PlanStep(
    PlanOperation Operation,
    int Quantity,
    string Unit,
    string Sku,
    string? From,
    string? To,
    string? Note = null);

/// <summary>A plan submitted through the deterministic engine. Status changes are made by the plan service only.</summary>
public sealed class Plan
{
    public Plan(int id, string skill, string requestedBy, string requestedByName, DateTimeOffset createdAt, IReadOnlyList<PlanStep> steps)
    {
        if (steps.Count == 0)
            throw new ArgumentException("A plan needs at least one step.", nameof(steps));

        Id = id;
        Skill = skill;
        RequestedBy = requestedBy;
        RequestedByName = requestedByName;
        CreatedAt = createdAt;
        Steps = steps;
    }

    public int Id { get; }
    public string Skill { get; }
    public string RequestedBy { get; }
    public string RequestedByName { get; }
    public DateTimeOffset CreatedAt { get; }
    public IReadOnlyList<PlanStep> Steps { get; }

    /// <summary>The plan risk is the highest risk among its steps.</summary>
    public PlanRisk Risk => Steps.Any(s => s.Operation == PlanOperation.AdjustStock) ? PlanRisk.Critical : PlanRisk.Reversible;

    public PlanStatus Status { get; internal set; } = PlanStatus.Validating;
    public string? DecidedByName { get; internal set; }
    public DateTimeOffset? DecidedAt { get; internal set; }
    public string? RejectionReason { get; internal set; }
    public string? FailureMessage { get; internal set; }
    public ExecutionSummary? Summary { get; internal set; }
}
