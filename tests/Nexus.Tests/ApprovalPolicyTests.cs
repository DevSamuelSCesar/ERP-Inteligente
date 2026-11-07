using erp_inteligente.Models;
using erp_inteligente.Services;
using Nexus.Tests.Fakes;

namespace Nexus.Tests;

public class ApprovalPolicyTests
{
    private static Plan PendingPlanBy(string username) =>
        new(10, "inventario", username, "Alguém", DateTimeOffset.UtcNow,
            [new PlanStep(PlanOperation.AdjustStock, -1, "cx", "ALI-300", "C-01-01", null, "Avaria")])
        {
            Status = PlanStatus.PendingApproval,
        };

    [Fact]
    public void Requester_cannot_decide_own_plan() =>
        Assert.False(ApprovalPolicy.CanDecide(PendingPlanBy("sup.maria"), DemoSessionService.Supervisor));

    [Fact]
    public void User_without_plan_approve_cannot_decide() =>
        Assert.False(ApprovalPolicy.CanDecide(PendingPlanBy("sup.maria"), DemoSessionService.Operator));

    [Fact]
    public void Other_supervisor_can_decide() =>
        Assert.True(ApprovalPolicy.CanDecide(PendingPlanBy("op.joao"), DemoSessionService.Supervisor));

    [Fact]
    public void Plan_that_is_not_pending_cannot_be_decided()
    {
        var plan = PendingPlanBy("op.joao");
        plan.Status = PlanStatus.Executed;
        Assert.False(ApprovalPolicy.CanDecide(plan, DemoSessionService.Supervisor));
    }
}
