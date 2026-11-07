using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using erp_inteligente.Models;

namespace erp_inteligente.Services;

/// <summary>
/// Front door of the deterministic plan engine: validation, simulation, approval and execution.
/// The UI never changes a plan directly.
/// </summary>
public interface IPlanService
{
    IReadOnlyList<Plan> Plans { get; }

    /// <summary>False until the deterministic engine exists (ROADMAP.md, feature 2): plans are read-only.</summary>
    bool IsEngineAvailable { get; }

    /// <summary>Reloads <see cref="Plans"/> from the source of truth.</summary>
    Task RefreshAsync();

    /// <summary>Raised on the UI thread whenever a plan is added or changes status.</summary>
    event EventHandler<Plan>? PlanChanged;

    /// <summary>Reversible plans execute right away; critical plans wait for approval.</summary>
    Task SubmitAsync(Plan plan);

    /// <exception cref="UnauthorizedAccessException">The approver is the requester or lacks <c>plan.approve</c>.</exception>
    Task ApproveAsync(Plan plan, UserSession approver);

    /// <exception cref="UnauthorizedAccessException">The approver is the requester or lacks <c>plan.approve</c>.</exception>
    /// <exception cref="ArgumentException">The reason is empty or too long.</exception>
    Task RejectAsync(Plan plan, UserSession approver, string reason);
}
