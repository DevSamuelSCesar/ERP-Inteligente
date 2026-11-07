using erp_inteligente.Models;

namespace erp_inteligente.Services;

/// <summary>
/// Segregation of duties (references/ARQUITETURA.md, section 12.5): only a pending plan can be decided,
/// only by someone with <c>plan.approve</c>, and never by whoever requested it.
/// </summary>
public static class ApprovalPolicy
{
    public const int MaxReasonLength = 200;

    public static bool CanDecide(Plan plan, UserSession user) =>
        plan.Status == PlanStatus.PendingApproval
        && user.Has(Permissions.PlanApprove)
        && !string.Equals(plan.RequestedBy, user.Username, System.StringComparison.Ordinal);
}
