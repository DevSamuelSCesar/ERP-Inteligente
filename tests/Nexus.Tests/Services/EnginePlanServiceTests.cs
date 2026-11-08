using erp_inteligente.Core.Engine;
using erp_inteligente.Models;
using erp_inteligente.Services.Sqlite;
using Nexus.Tests.Core.Policy;

namespace Nexus.Tests.Services;

/// <summary>Engine-backed plan service over the seeded database (plans 1 and 2 belong to warehouse CD-01).</summary>
public class EnginePlanServiceTests : IDisposable
{
    private readonly PolicyTestFixture _f = new();
    private readonly FakeSessionService _session;
    private readonly EnginePlanService _plans;

    public EnginePlanServiceTests()
    {
        _session = new FakeSessionService(_f.SessionFor("sup.maria"));
        var engine = new PlanEngine(_f.Db.Database, _f.Policy, _f.Audit, _f.Time);
        _plans = new EnginePlanService(engine, _f.App, _session);
    }

    public void Dispose() => _f.Dispose();

    private static PlanDraft Draft(string op, Dictionary<string, object?> args, string? skill = "armazenagem") =>
        PlanDraft.Create("teste", skill, [new PlanDraftStep(op, args)]);

    private static PlanDraft MoveDraft(int qty) => Draft("move_stock",
        new() { ["sku"] = "ABC-123", ["qty"] = qty, ["from"] = "DOCA-01", ["to"] = "A-02-01" });

    private static PlanDraft AdjustDraft(int qty) => Draft("adjust_stock",
        new() { ["sku"] = "ABC-123", ["qty"] = qty, ["location"] = "DOCA-01", ["reason"] = "Inventário" });

    private void AddBia() =>
        _f.Exec("INSERT INTO users (id, username, display_name, password_hash, role_id, warehouse_id) " +
                "VALUES (7, 'sup.bia', 'Bia (supervisora)', 'no-login-in-tests', 2, 1)");

    // ---- read side (migrated from SqlitePlanServiceTests) ---------------------------------------------------

    [Fact]
    public void Engine_is_available_and_plans_are_empty_before_the_first_refresh()
    {
        Assert.True(_plans.IsEngineAvailable);
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
        Assert.Null(executed.Summary); // the seed stores the legacy diff format
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
        _session.Current = new UserSession(1, "op.joao", "João (operador)", 1, "operador", 1, "CD-01", new HashSet<string>());
        await _plans.RefreshAsync();
        Assert.Empty(_plans.Plans);
    }

    [Fact]
    public async Task Plans_of_another_warehouse_are_not_listed()
    {
        SqliteTestData.AddSecondWarehouse(_f);
        InsertPlan(3, 6, "'executed'", "'reversible'",
            "{\"intent\":\"putaway\",\"steps\":[{\"op\":\"move_stock\",\"args\":{\"sku\":\"ABC-123\",\"qty\":1,\"from\":\"DOCA-99\",\"to\":\"DOCA-99\"}}]}");

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
        InsertPlan(3, 1, "'pending_approval'", "'reversible'",
            "{\"intent\":\"putaway\",\"steps\":[{\"op\":\"teleport_stock\",\"args\":{\"sku\":\"ABC-123\",\"qty\":1}}]}");
        await _plans.RefreshAsync();
        Assert.DoesNotContain(_plans.Plans, p => p.Id == 3);
    }

    [Fact]
    public async Task Plan_with_a_known_and_an_unknown_step_is_skipped_entirely()
    {
        InsertPlan(3, 1, "'pending_approval'", "'reversible'",
            "{\"intent\":\"putaway\",\"steps\":[" +
            "{\"op\":\"move_stock\",\"args\":{\"sku\":\"ABC-123\",\"qty\":1,\"from\":\"DOCA-01\",\"to\":\"A-01-01\"}}," +
            "{\"op\":\"teleport_stock\",\"args\":{\"sku\":\"ABC-123\",\"qty\":1}}]}");
        await _plans.RefreshAsync();
        Assert.DoesNotContain(_plans.Plans, p => p.Id == 3);
    }

    [Fact]
    public async Task Unit_of_an_unknown_sku_falls_back_to_un()
    {
        InsertPlan(3, 1, "'pending_approval'", "'reversible'",
            "{\"intent\":\"putaway\",\"steps\":[{\"op\":\"move_stock\",\"args\":{\"sku\":\"XYZ-999\",\"qty\":2,\"from\":\"DOCA-01\",\"to\":\"A-01-01\"}}]}");
        await _plans.RefreshAsync();
        Assert.Equal("un", _plans.Plans.Single(p => p.Id == 3).Steps[0].Unit);
    }

    [Fact]
    public async Task All_five_operations_are_mapped_to_steps()
    {
        InsertPlan(3, 1, "'pending_approval'", "'critical'",
            "{\"intent\":\"x\",\"steps\":[" +
            "{\"op\":\"receive_goods\",\"args\":{\"sku\":\"ABC-123\",\"qty\":5,\"location\":\"DOCA-01\",\"lot\":\"L1\"}}," +
            "{\"op\":\"move_stock\",\"args\":{\"sku\":\"ABC-123\",\"qty\":2,\"from\":\"DOCA-01\",\"to\":\"A-01-01\",\"lot\":\"L1\"}}," +
            "{\"op\":\"adjust_stock\",\"args\":{\"sku\":\"ABC-123\",\"qty\":-1,\"location\":\"DOCA-01\",\"reason\":\"Quebra\"}}," +
            "{\"op\":\"create_product\",\"args\":{\"sku\":\"NEW-001\",\"name\":\"Novo\",\"unit\":\"kg\"}}," +
            "{\"op\":\"create_location\",\"args\":{\"code\":\"A-03-03\",\"zone\":\"A\",\"capacity\":10}}]}");

        await _plans.RefreshAsync();

        var steps = _plans.Plans.Single(p => p.Id == 3).Steps;
        Assert.Equal(new PlanStep(PlanOperation.ReceiveGoods, 5, "cx", "ABC-123", null, "DOCA-01", "L1"), steps[0]);
        Assert.Equal(new PlanStep(PlanOperation.MoveStock, 2, "cx", "ABC-123", "DOCA-01", "A-01-01", "L1"), steps[1]);
        Assert.Equal(new PlanStep(PlanOperation.AdjustStock, -1, "cx", "ABC-123", "DOCA-01", null, "Quebra"), steps[2]);
        Assert.Equal(new PlanStep(PlanOperation.CreateProduct, 0, "kg", "NEW-001", null, null, "Novo"), steps[3]);
        Assert.Equal(new PlanStep(PlanOperation.CreateLocation, 10, "", "", "A", "A-03-03"), steps[4]);
    }

    // ---- engine side ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Plan_instances_are_preserved_across_refreshes()
    {
        await _plans.RefreshAsync();
        var first = _plans.Plans.ToList();

        await _plans.RefreshAsync();

        Assert.Equal(first.Count, _plans.Plans.Count);
        for (var i = 0; i < first.Count; i++)
            Assert.Same(first[i], _plans.Plans[i]);
    }

    [Fact]
    public async Task Preview_maps_issues_one_based_and_effect_totals()
    {
        var ok = await _plans.PreviewAsync(MoveDraft(10));
        Assert.True(ok.IsValid);
        Assert.Equal(PlanRisk.Reversible, ok.Risk);
        Assert.Equal(PlanOperation.MoveStock, Assert.Single(ok.Steps).Operation);
        Assert.Equal("cx", ok.Steps[0].Unit);
        Assert.Contains(ok.Effect!.Totals, t => t.Label == "Quantidade movimentada" && t.Value == "10 cx");
        Assert.Single(ok.Effect.Flows);

        var bad = await _plans.PreviewAsync(MoveDraft(999));
        Assert.False(bad.IsValid);
        Assert.Null(bad.Effect);
        Assert.Equal(1, bad.Issues[0].StepNumber);

        var critical = await _plans.PreviewAsync(AdjustDraft(-5));
        Assert.Equal(PlanRisk.Critical, critical.Risk);
        Assert.Equal(40, _f.Scalar("SELECT quantity FROM stock_balances WHERE location_id = 1 AND product_id = 1"));
    }

    [Fact]
    public async Task Preview_shows_references_as_they_are()
    {
        var draft = PlanDraft.FromJson(
            "{\"dsl_version\":\"1.0\",\"intent\":\"x\",\"steps\":[" +
            "{\"op\":\"create_location\",\"as\":\"loc\",\"args\":{\"code\":\"A-03-03\",\"zone\":\"A\",\"capacity\":10}}," +
            "{\"op\":\"move_stock\",\"args\":{\"sku\":\"ABC-123\",\"qty\":1,\"from\":\"DOCA-01\",\"to\":\"$loc\"}}]}");

        var preview = await _plans.PreviewAsync(draft);

        Assert.True(preview.IsValid, string.Join("; ", preview.Issues.Select(i => i.Message)));
        Assert.Equal("$loc", preview.Steps[1].To);
    }

    [Fact]
    public async Task Submit_executes_a_reversible_plan_and_returns_the_listed_instance()
    {
        Plan? raised = null;
        _plans.PlanChanged += (_, p) => raised = p;

        var plan = await _plans.SubmitAsync(MoveDraft(10));

        Assert.Equal(PlanStatus.Executed, plan.Status);
        Assert.Same(plan, raised);
        Assert.Same(plan, _plans.Plans.Single(p => p.Id == plan.Id));
        Assert.Equal("Execução automática", plan.DecidedByName);
        Assert.NotNull(plan.Summary);
        Assert.Equal("sup.maria", plan.RequestedBy);
    }

    [Fact]
    public async Task Submit_of_a_critical_plan_waits_for_approval()
    {
        var plan = await _plans.SubmitAsync(AdjustDraft(-30));
        Assert.Equal((PlanStatus.PendingApproval, PlanRisk.Critical), (plan.Status, plan.Risk));
        Assert.Null(plan.Summary);
    }

    [Fact]
    public async Task Submit_of_a_failing_plan_returns_it_as_failed_with_the_message()
    {
        var plan = await _plans.SubmitAsync(MoveDraft(999));
        Assert.Equal(PlanStatus.Failed, plan.Status);
        Assert.False(string.IsNullOrWhiteSpace(plan.FailureMessage));
    }

    [Fact]
    public async Task Submit_of_a_draft_the_schema_refuses_is_stored_as_failed()
    {
        var draft = Draft("move_stock", new() { ["sku"] = "abc", ["qty"] = 1, ["from"] = "DOCA-01", ["to"] = "A-02-01" });
        var plan = await _plans.SubmitAsync(draft);
        Assert.Equal(PlanStatus.Failed, plan.Status);
        Assert.False(string.IsNullOrWhiteSpace(plan.FailureMessage));
    }

    [Fact]
    public async Task Submit_of_a_stored_plan_without_showable_steps_throws_with_the_failure_messages()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _plans.SubmitAsync(PlanDraft.FromJson("{\"steps\":[]}")));
        Assert.Contains("intenção", ex.Message);
    }

    [Fact]
    public async Task Submit_without_permission_throws_policy_denied()
    {
        _session.Current = _f.SessionFor("aud.lucia");
        await Assert.ThrowsAsync<erp_inteligente.Core.Policy.PolicyDeniedException>(() => _plans.SubmitAsync(MoveDraft(1)));
    }

    [Fact]
    public async Task Approve_raises_plan_changed_with_the_same_instance_and_updates_it_in_place()
    {
        AddBia();
        var plan = await _plans.SubmitAsync(AdjustDraft(-30));
        var changes = new List<Plan>();
        _plans.PlanChanged += (_, p) => changes.Add(p);

        await _plans.ApproveAsync(plan, _f.SessionFor("sup.bia"));

        Assert.Same(plan, Assert.Single(changes));
        Assert.Equal((PlanStatus.Executed, "Bia (supervisora)"), (plan.Status, plan.DecidedByName));
        Assert.NotNull(plan.Summary);
        Assert.Same(plan, _plans.Plans.Single(p => p.Id == plan.Id));
    }

    [Fact]
    public async Task Approve_by_the_requester_propagates_unauthorized()
    {
        var plan = await _plans.SubmitAsync(AdjustDraft(-30));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _plans.ApproveAsync(plan, _f.SessionFor("sup.maria")));
        Assert.Equal(PlanStatus.PendingApproval, plan.Status);
    }

    [Fact]
    public async Task Reject_updates_the_plan_in_place_with_the_reason()
    {
        AddBia();
        await _plans.RefreshAsync();
        var seed = _plans.Plans[1];
        var changes = new List<Plan>();
        _plans.PlanChanged += (_, p) => changes.Add(p);

        await _plans.RejectAsync(seed, _f.SessionFor("sup.bia"), "Contagem não confere");

        Assert.Same(seed, Assert.Single(changes));
        Assert.Equal((PlanStatus.Rejected, "Contagem não confere", "Bia (supervisora)"), (seed.Status, seed.RejectionReason, seed.DecidedByName));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _plans.RejectAsync(seed, _f.SessionFor("sup.bia"), "outra vez"));
        await Assert.ThrowsAsync<ArgumentException>(() => _plans.RejectAsync(seed, _f.SessionFor("sup.bia"), " "));
    }

    [Fact]
    public async Task Failed_approval_of_the_seed_plan_shows_the_failure_message()
    {
        await _plans.RefreshAsync();
        var seed = _plans.Plans[1];

        await _plans.ApproveAsync(seed, _f.SessionFor("sup.maria"));

        Assert.Equal(PlanStatus.Failed, seed.Status);
        Assert.Contains("seu papel não permite", seed.FailureMessage);
    }

    private void InsertPlan(int id, int requestedBy, string status, string risk, string planJson) =>
        _f.Exec("INSERT INTO plans (id, requested_by, planned_by, skill, intent, plan_json, dsl_version, risk, status, created_at, executed_at) VALUES " +
                $"($id, $by, 'ai', 'armazenagem', 'putaway', $json, '1.0', {risk}, {status}, '2026-10-27T10:00:00Z', " +
                (status == "'executed'" ? "'2026-10-27T10:01:00Z')" : "NULL)"),
                ("$id", id), ("$by", requestedBy), ("$json", planJson));
}
