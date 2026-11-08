using erp_inteligente.Models;
using Nexus.Tests.Fakes;

namespace Nexus.Tests;

public class DemoPlanServiceTests
{
    private static Plan Move(DemoWorld w, int qty, string from = "DOCA-01", string to = "A-02-01", string by = "op.joao") =>
        new(w.Data.NextPlanId(), "armazenagem", by, "João", DateTimeOffset.UtcNow,
            [new PlanStep(PlanOperation.MoveStock, qty, "cx", "ABC-123", from, to)]);

    private static Plan Adjust(DemoWorld w, int qty, string by = "op.joao", string? note = "Avaria") =>
        new(w.Data.NextPlanId(), "inventario", by, "João", DateTimeOffset.UtcNow,
            [new PlanStep(PlanOperation.AdjustStock, qty, "cx", "ALI-300", "C-01-01", null, note)]);

    [Fact]
    public async Task Reversible_plan_executes_and_moves_stock()
    {
        var w = new DemoWorld();
        var plan = Move(w, 15);

        await w.Plans.SubmitPlanAsync(plan);

        Assert.Equal(PlanStatus.Executed, plan.Status);
        Assert.Equal(25, w.Data.QuantityAt("ABC-123", "DOCA-01"));
        Assert.Equal(15, w.Data.QuantityAt("ABC-123", "A-02-01"));
        var flow = Assert.Single(plan.Summary!.Flows);
        Assert.Equal(("DOCA-01", "A-02-01", 15), (flow.From, flow.To, flow.Quantity));
    }

    [Fact]
    public async Task Critical_plan_waits_for_approval_without_changing_stock()
    {
        var w = new DemoWorld();
        var plan = Adjust(w, -2);

        await w.Plans.SubmitPlanAsync(plan);

        Assert.Equal(PlanStatus.PendingApproval, plan.Status);
        Assert.Equal(36, w.Data.QuantityAt("ALI-300", "C-01-01"));
    }

    [Fact]
    public async Task Requester_cannot_approve_own_plan()
    {
        var w = new DemoWorld();
        var plan = Adjust(w, -2, by: "sup.maria");
        await w.Plans.SubmitPlanAsync(plan);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => w.Plans.ApproveAsync(plan, DemoSessionService.Supervisor));
        Assert.Equal(PlanStatus.PendingApproval, plan.Status);
    }

    [Fact]
    public async Task Operator_cannot_approve()
    {
        var w = new DemoWorld();
        var plan = Adjust(w, -2, by: "sup.maria");
        await w.Plans.SubmitPlanAsync(plan);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => w.Plans.ApproveAsync(plan, DemoSessionService.Operator));
    }

    [Fact]
    public async Task Approval_by_another_supervisor_executes_with_balance_summary()
    {
        var w = new DemoWorld();
        var plan = Adjust(w, -2);
        await w.Plans.SubmitPlanAsync(plan);

        await w.Plans.ApproveAsync(plan, DemoSessionService.Supervisor);

        Assert.Equal(PlanStatus.Executed, plan.Status);
        Assert.Equal("Maria", plan.DecidedByName);
        var balance = Assert.Single(plan.Summary!.Balances);
        Assert.Equal((36, 34), (balance.Before, balance.After));
        // FEFO: the lot expiring first loses the units.
        Assert.Equal(14, w.Data.Balances.Single(b => b.Lot == "L300-2611A").Quantity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejection_requires_a_reason(string reason)
    {
        var w = new DemoWorld();
        var plan = Adjust(w, -2);
        await w.Plans.SubmitPlanAsync(plan);

        await Assert.ThrowsAsync<ArgumentException>(() => w.Plans.RejectAsync(plan, DemoSessionService.Supervisor, reason));
        Assert.Equal(PlanStatus.PendingApproval, plan.Status);
    }

    [Fact]
    public async Task Rejection_with_reason_is_recorded()
    {
        var w = new DemoWorld();
        var plan = Adjust(w, -2);
        await w.Plans.SubmitPlanAsync(plan);

        await w.Plans.RejectAsync(plan, DemoSessionService.Supervisor, "  Contagem errada ");

        Assert.Equal(PlanStatus.Rejected, plan.Status);
        Assert.Equal("Contagem errada", plan.RejectionReason);
        Assert.Contains(w.Data.Audit, a => a.Action == "Plano rejeitado");
    }

    [Fact]
    public async Task Invalid_plan_fails_and_writes_nothing()
    {
        var w = new DemoWorld();
        var plan = Move(w, 41);

        await w.Plans.SubmitPlanAsync(plan);

        Assert.Equal(PlanStatus.Failed, plan.Status);
        Assert.Contains("DOCA-01 tem só 40", plan.FailureMessage);
        Assert.Equal(40, w.Data.QuantityAt("ABC-123", "DOCA-01"));
    }

    [Fact]
    public async Task Adjustment_without_reason_fails()
    {
        var w = new DemoWorld();
        var plan = Adjust(w, -1, note: null);

        await w.Plans.SubmitPlanAsync(plan);

        Assert.Equal(PlanStatus.Failed, plan.Status);
    }

    [Fact]
    public async Task Same_plan_cannot_be_submitted_twice()
    {
        var w = new DemoWorld();
        var plan = Move(w, 1);
        await w.Plans.SubmitPlanAsync(plan);

        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Plans.SubmitPlanAsync(plan));
    }
}
