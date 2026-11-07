using erp_inteligente.Models;
using erp_inteligente.Services.Sqlite;
using Nexus.Tests.Core.Policy;

namespace Nexus.Tests.Services;

/// <summary>Read-only plan list over the seeded database (plans 1 and 2 belong to warehouse CD-01).</summary>
public class SqlitePlanServiceTests : IDisposable
{
    private const string EngineMessage = "O motor de planos chega na feature 2 do roadmap.";

    private readonly PolicyTestFixture _f = new();
    private readonly FakeSessionService _session;
    private readonly SqlitePlanService _plans;

    public SqlitePlanServiceTests()
    {
        _session = new FakeSessionService(_f.SessionFor("sup.maria"));
        _plans = new SqlitePlanService(_f.App, _session);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public void Plans_are_empty_before_the_first_refresh()
    {
        Assert.Empty(_plans.Plans);
    }

    [Fact]
    public async Task Refresh_lists_the_seed_plans_with_their_steps_and_status()
    {
        await _plans.RefreshAsync();

        Assert.Equal(2, _plans.Plans.Count);

        var executed = _plans.Plans[0];
        Assert.Equal((1, "armazenagem", "op.joao", "João (operador)"), (executed.Id, executed.Skill, executed.RequestedBy, executed.RequestedByName));
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 10, 1, 0, TimeSpan.Zero), executed.CreatedAt);
        Assert.Equal(PlanStatus.Executed, executed.Status);
        Assert.Equal(PlanRisk.Reversible, executed.Risk);
        Assert.Equal("Execução automática", executed.DecidedByName);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 10, 2, 0, TimeSpan.Zero), executed.DecidedAt);
        var move = Assert.Single(executed.Steps);
        Assert.Equal((PlanOperation.MoveStock, 40, "cx", "ABC-123", "DOCA-01", "A-01-01"),
            (move.Operation, move.Quantity, move.Unit, move.Sku, move.From, move.To));
    }

    [Fact]
    public async Task Refresh_lists_the_pending_adjustment_as_critical_with_its_reason()
    {
        await _plans.RefreshAsync();

        var pending = _plans.Plans[1];
        Assert.Equal((2, "inventario", PlanStatus.PendingApproval, PlanRisk.Critical), (pending.Id, pending.Skill, pending.Status, pending.Risk));
        Assert.Null(pending.DecidedByName);
        Assert.Null(pending.DecidedAt);
        var adjust = Assert.Single(pending.Steps);
        Assert.Equal((PlanOperation.AdjustStock, -1, "cx", "ALI-300", "C-01-01", (string?)null, "Avaria: embalagem violada"),
            (adjust.Operation, adjust.Quantity, adjust.Unit, adjust.Sku, adjust.From, adjust.To, adjust.Note));
    }

    [Fact]
    public async Task Approver_name_is_shown_when_the_plan_has_an_approver()
    {
        _f.Exec("UPDATE plans SET approved_by = 2, status = 'executed', executed_at = '2026-10-26T09:00:00Z' WHERE id = 2");

        await _plans.RefreshAsync();

        var plan = _plans.Plans[1];
        Assert.Equal(("Maria (supervisora)", PlanStatus.Executed), (plan.DecidedByName, plan.Status));
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero), plan.DecidedAt);
    }

    [Fact]
    public async Task Auditor_sees_the_plans_through_audit_read()
    {
        _session.Current = _f.SessionFor("aud.lucia");

        await _plans.RefreshAsync();

        Assert.Equal(2, _plans.Plans.Count);
    }

    [Fact]
    public async Task User_without_any_plan_permission_sees_no_plans()
    {
        // The database would return the seed plans to an operator; an empty permission set must not query at all.
        _session.Current = new UserSession(1, "op.joao", "João (operador)", 1, "operador", 1, "CD-01", new HashSet<string>());

        await _plans.RefreshAsync();

        Assert.Empty(_plans.Plans);
    }

    [Fact]
    public async Task Plans_of_another_warehouse_are_not_listed()
    {
        SqliteTestData.AddSecondWarehouse(_f);
        InsertPlanOfWarehouseTwo();

        await _plans.RefreshAsync();
        Assert.Equal(2, _plans.Plans.Count);
        Assert.DoesNotContain(_plans.Plans, p => p.Id == 3);

        _session.Current = _f.SessionFor("op.ana");
        await _plans.RefreshAsync();
        var plan = Assert.Single(_plans.Plans);
        Assert.Equal((3, "Ana (operadora)", "Execução automática"), (plan.Id, plan.RequestedByName, plan.DecidedByName));
    }

    [Fact]
    public async Task Plan_with_only_unknown_steps_is_skipped()
    {
        _f.Exec("INSERT INTO plans (id, requested_by, planned_by, skill, intent, plan_json, dsl_version, risk, status, created_at) " +
                "VALUES (3, 1, 'ai', 'armazenagem', 'putaway', " +
                "'{\"intent\":\"putaway\",\"steps\":[{\"op\":\"teleport_stock\",\"args\":{\"sku\":\"ABC-123\",\"qty\":1}}]}', " +
                "'1.0', 'reversible', 'pending_approval', '2026-10-27T10:00:00Z')");

        await _plans.RefreshAsync();

        Assert.Equal(2, _plans.Plans.Count);
        Assert.DoesNotContain(_plans.Plans, p => p.Id == 3);
    }

    [Fact]
    public async Task Plan_with_a_known_and_an_unknown_step_is_skipped_entirely()
    {
        _f.Exec("INSERT INTO plans (id, requested_by, planned_by, skill, intent, plan_json, dsl_version, risk, status, created_at) " +
                "VALUES (3, 1, 'ai', 'armazenagem', 'putaway', " +
                "'{\"intent\":\"putaway\",\"steps\":[" +
                "{\"op\":\"move_stock\",\"args\":{\"sku\":\"ABC-123\",\"qty\":1,\"from\":\"DOCA-01\",\"to\":\"A-01-01\"}}," +
                "{\"op\":\"teleport_stock\",\"args\":{\"sku\":\"ABC-123\",\"qty\":1}}]}', " +
                "'1.0', 'reversible', 'pending_approval', '2026-10-27T10:00:00Z')");

        await _plans.RefreshAsync();

        Assert.DoesNotContain(_plans.Plans, p => p.Id == 3);
    }

    [Fact]
    public async Task Unit_of_an_unknown_sku_falls_back_to_un()
    {
        _f.Exec("INSERT INTO plans (id, requested_by, planned_by, skill, intent, plan_json, dsl_version, risk, status, created_at) " +
                "VALUES (3, 1, 'ai', 'armazenagem', 'putaway', " +
                "'{\"intent\":\"putaway\",\"steps\":[{\"op\":\"move_stock\",\"args\":{\"sku\":\"XYZ-999\",\"qty\":2,\"from\":\"DOCA-01\",\"to\":\"A-01-01\"}}]}', " +
                "'1.0', 'reversible', 'pending_approval', '2026-10-27T10:00:00Z')");

        await _plans.RefreshAsync();

        var plan = _plans.Plans.Single(p => p.Id == 3);
        Assert.Equal("un", plan.Steps[0].Unit);
    }

    [Fact]
    public async Task Engine_is_not_available_and_nothing_can_be_changed()
    {
        var plan = new Plan(9, "armazenagem", "op.joao", "João (operador)", DateTimeOffset.UtcNow,
            [new PlanStep(PlanOperation.MoveStock, 1, "cx", "ABC-123", "DOCA-01", "A-01-01")]);

        Assert.False(_plans.IsEngineAvailable);
        Assert.Equal(EngineMessage, (await Assert.ThrowsAsync<InvalidOperationException>(() => _plans.SubmitAsync(plan))).Message);
        Assert.Equal(EngineMessage, (await Assert.ThrowsAsync<InvalidOperationException>(() => _plans.ApproveAsync(plan, _session.Current))).Message);
        Assert.Equal(EngineMessage, (await Assert.ThrowsAsync<InvalidOperationException>(() => _plans.RejectAsync(plan, _session.Current, "motivo"))).Message);
    }

    /// <summary>Plan 3 requested by Ana (warehouse CD-02), already executed by the reversible rule.</summary>
    private void InsertPlanOfWarehouseTwo() =>
        _f.Exec("INSERT INTO plans (id, requested_by, planned_by, skill, intent, plan_json, dsl_version, risk, status, created_at, executed_at) VALUES " +
                "(3, 6, 'user', 'armazenagem', 'putaway', " +
                "'{\"intent\":\"putaway\",\"steps\":[{\"op\":\"move_stock\",\"args\":{\"sku\":\"ABC-123\",\"qty\":1,\"from\":\"DOCA-99\",\"to\":\"DOCA-99\"}}]}', " +
                "'1.0', 'reversible', 'executed', '2026-10-27T10:00:00Z', '2026-10-27T10:01:00Z')");
}
