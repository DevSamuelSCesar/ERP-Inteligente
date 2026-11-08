using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Engine;
using erp_inteligente.Core.Policy;
using erp_inteligente.Models;
using Nexus.Tests.Core.Policy;
using Nexus.Tests.Services;

namespace Nexus.Tests.Core.Engine;

/// <summary>State machine of plans: submit, approve, reject, over the seeded database (plan 2 is pending, from op.joao).</summary>
public sealed class PlanEngineTests : IDisposable
{
    private readonly PolicyTestFixture _f = new();
    private readonly PlanEngine _engine;
    private readonly UserSession _maria;
    private readonly UserSession _joao;

    public PlanEngineTests()
    {
        _engine = new PlanEngine(_f.Db.Database, _f.Policy, _f.Audit, _f.Time);
        _maria = _f.SessionFor("sup.maria");
        _joao = _f.SessionFor("op.joao");
    }

    public void Dispose() => _f.Dispose();

    private static string Json(params string[] steps) =>
        "{\"dsl_version\":\"1.0\",\"intent\":\"teste\",\"skill\":\"armazenagem\",\"steps\":[" + string.Join(",", steps) + "]}";

    private static string Move(string sku, int qty, string from, string to) => EngineHarness.Move(sku, qty, from, to);

    private static string Adjust(string sku, int qty, string location) => EngineHarness.Adjust(sku, qty, location);

    private UserSession AddSupervisor(string username, long warehouseId)
    {
        var id = _f.Scalar("SELECT COALESCE(MAX(id), 0) + 1 FROM users");
        _f.Exec("INSERT INTO users (id, username, display_name, password_hash, role_id, warehouse_id) " +
                "VALUES ($id, $u, $u, 'no-login-in-tests', 2, $w)", ("$id", id), ("$u", username), ("$w", warehouseId));
        return _f.SessionFor(username);
    }

    private long Qty(string sku, string location) =>
        _f.Scalar(
            "SELECT COALESCE(SUM(b.quantity), 0) FROM stock_balances b JOIN products p ON p.id = b.product_id " +
            "JOIN locations l ON l.id = b.location_id WHERE p.sku = $sku AND l.code = $loc", ("$sku", sku), ("$loc", location));

    private string Text(string sql, params (string, object)[] p)
    {
        using var c = _f.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    private string Status(long id) => Text("SELECT status FROM plans WHERE id = $id", ("$id", id));

    private long Audits(string action, string? outcome = null) =>
        _f.Scalar("SELECT COUNT(*) FROM audit_log WHERE action = $a AND ($o IS NULL OR outcome = $o)",
            ("$a", action), ("$o", (object?)outcome ?? DBNull.Value));

    private void AssertChainValid()
    {
        using var c = _f.Open();
        Assert.True(AuditVerifier.Verify(c).IsValid);
    }

    private long SubmitCritical(UserSession requester, int qty = -30)
    {
        var r = _engine.Submit(requester, Json(Adjust("ABC-123", qty, "DOCA-01")), "user");
        Assert.Equal("pending_approval", r.Status);
        return r.PlanId!.Value;
    }

    // ---- preview ------------------------------------------------------------------------------------------

    [Fact]
    public void Preview_returns_diff_and_writes_nothing()
    {
        var plans = _f.Scalar("SELECT COUNT(*) FROM plans");
        var preview = _engine.Preview(_maria, Json(Move("ABC-123", 10, "DOCA-01", "A-02-01")));

        Assert.True(preview.IsValid);
        Assert.Equal(RiskLevel.Reversible, preview.Risk);
        Assert.NotNull(preview.Diff);
        Assert.Equal(plans, _f.Scalar("SELECT COUNT(*) FROM plans"));
        Assert.Equal(40, Qty("ABC-123", "DOCA-01"));
    }

    [Fact]
    public void Preview_reports_parse_and_business_errors()
    {
        var parse = _engine.Preview(_maria, "{\"steps\":[]}");
        Assert.False(parse.IsValid);
        Assert.Null(parse.Document);

        var business = _engine.Preview(_maria, Json(Move("ABC-123", 999, "DOCA-01", "A-02-01")));
        Assert.False(business.IsValid);
        Assert.NotNull(business.Document);
        Assert.Null(business.Diff);
        Assert.Equal(0, business.Errors[0].StepIndex);
    }

    [Fact]
    public void Preview_requires_plan_submit_and_audits_the_denial()
    {
        var auditor = _f.SessionFor("aud.lucia");
        Assert.Throws<PolicyDeniedException>(() => _engine.Preview(auditor, Json(Move("ABC-123", 1, "DOCA-01", "A-02-01"))));
        Assert.Equal(1, Audits("policy.denied"));
    }

    // ---- submit -------------------------------------------------------------------------------------------

    [Fact]
    public void Reversible_move_executes_immediately_with_diff_and_audit()
    {
        var result = _engine.Submit(_maria, Json(Move("ABC-123", 10, "DOCA-01", "A-02-01")), "user");

        Assert.Equal("executed", result.Status);
        Assert.NotNull(result.PlanId);
        Assert.Equal(30, Qty("ABC-123", "DOCA-01"));
        Assert.Equal(10, Qty("ABC-123", "A-02-01"));
        var id = result.PlanId!.Value;
        Assert.Equal("executed", Status(id));
        Assert.Equal("reversible", Text("SELECT risk FROM plans WHERE id = $id", ("$id", id)));
        Assert.Equal("1", Text("SELECT COUNT(*) FROM plans WHERE id = $id AND executed_at IS NOT NULL", ("$id", id)));
        var diff = PlanDiffJson.Deserialize(Text("SELECT simulated_diff_json FROM plans WHERE id = $id", ("$id", id)));
        Assert.Single(diff.Moves);
        Assert.True(diff.SameAs(result.Diff!));
        Assert.Equal(1, _f.Scalar("SELECT COUNT(*) FROM audit_log WHERE action = 'plan.submit' AND target = $t AND actor_type = 'user'", ("$t", $"plan:{id}")));
        Assert.Equal(1, _f.Scalar("SELECT COUNT(*) FROM audit_log WHERE action = 'plan.execute' AND target = $t AND actor_type = 'system' AND on_behalf_of = $u",
            ("$t", $"plan:{id}"), ("$u", _maria.UserId)));
        AssertChainValid();
    }

    [Fact]
    public void Ai_submission_is_audited_as_ai_on_behalf_of_the_requester()
    {
        var result = _engine.Submit(_maria, Json(Move("ABC-123", 1, "DOCA-01", "A-02-01")), "ai");

        Assert.Equal("executed", result.Status);
        Assert.Equal(1, _f.Scalar("SELECT COUNT(*) FROM audit_log WHERE action = 'plan.submit' AND target = $t AND actor_type = 'ai' AND on_behalf_of = $u",
            ("$t", $"plan:{result.PlanId}"), ("$u", _maria.UserId)));
        Assert.Equal("ai", Text("SELECT planned_by FROM plans WHERE id = $id", ("$id", result.PlanId!.Value)));
        AssertChainValid();
    }

    [Fact]
    public void Critical_adjust_by_a_supervisor_waits_for_approval_without_touching_stock()
    {
        var result = _engine.Submit(_maria, Json(Adjust("ABC-123", -30, "DOCA-01")), "user");

        Assert.Equal("pending_approval", result.Status);
        Assert.Equal(40, Qty("ABC-123", "DOCA-01"));
        var id = result.PlanId!.Value;
        Assert.Equal("critical", Text("SELECT risk FROM plans WHERE id = $id", ("$id", id)));
        var diff = PlanDiffJson.Deserialize(Text("SELECT simulated_diff_json FROM plans WHERE id = $id", ("$id", id)));
        Assert.Equal(-30, Assert.Single(diff.Adjustments).Quantity);
        Assert.Equal(0, _f.Scalar("SELECT COUNT(*) FROM audit_log WHERE action = 'plan.execute' AND target = $t", ("$t", $"plan:{id}")));
        AssertChainValid();
    }

    [Fact]
    public void Critical_adjust_by_an_operator_is_stored_as_failed_with_the_policy_message()
    {
        var result = _engine.Submit(_joao, Json(Adjust("ABC-123", -1, "DOCA-01")), "user");

        Assert.Equal("failed", result.Status);
        var id = result.PlanId!.Value;
        Assert.Equal("failed", Status(id));
        Assert.Contains("seu papel não permite", Text("SELECT failure_reason FROM plans WHERE id = $id", ("$id", id)));
        Assert.Equal(40, Qty("ABC-123", "DOCA-01"));
        Assert.Equal(1, Audits("plan.failed", "failed"));
        AssertChainValid();
    }

    [Fact]
    public void Invalid_json_is_not_stored_and_returns_errors()
    {
        var plans = _f.Scalar("SELECT COUNT(*) FROM plans");

        var result = _engine.Submit(_maria, "{not json", "user");

        Assert.Null(result.PlanId);
        Assert.Equal("invalid", result.Status);
        Assert.NotEmpty(result.Errors);
        Assert.Equal(plans, _f.Scalar("SELECT COUNT(*) FROM plans"));
    }

    [Fact]
    public void Oversized_text_is_not_stored()
    {
        var plans = _f.Scalar("SELECT COUNT(*) FROM plans");
        var result = _engine.Submit(_maria, "[\"" + new string('a', DslParser.MaxBytes) + "\"]", "user");

        Assert.Null(result.PlanId);
        Assert.Equal(plans, _f.Scalar("SELECT COUNT(*) FROM plans"));
    }

    [Fact]
    public void Dsl_invalid_but_valid_json_is_stored_as_failed()
    {
        var result = _engine.Submit(_maria, "{\"dsl_version\":\"1.0\",\"intent\":\"x\",\"steps\":[{\"op\":\"teleport\",\"args\":{}}]}", "user");

        Assert.NotNull(result.PlanId);
        Assert.Equal("failed", result.Status);
        var id = result.PlanId!.Value;
        Assert.Equal("failed", Status(id));
        Assert.NotEqual("", Text("SELECT failure_reason FROM plans WHERE id = $id", ("$id", id)));
        Assert.Equal(1, Audits("plan.failed"));
    }

    [Fact]
    public void Business_failure_of_a_reversible_plan_rolls_back_and_is_stored_as_failed()
    {
        var result = _engine.Submit(_maria, Json(Move("ABC-123", 999, "DOCA-01", "A-02-01")), "user");

        Assert.Equal("failed", result.Status);
        Assert.Equal(40, Qty("ABC-123", "DOCA-01"));
        Assert.Equal("failed", Status(result.PlanId!.Value));
    }

    [Fact]
    public void Submit_requires_plan_submit()
    {
        var admin = _f.SessionFor("adm.carlos");
        Assert.Throws<PolicyDeniedException>(() => _engine.Submit(admin, Json(Move("ABC-123", 1, "DOCA-01", "A-02-01")), "user"));
    }

    // ---- approve ------------------------------------------------------------------------------------------

    [Fact]
    public void Approving_the_seed_plan_fails_because_the_requester_lacks_the_right()
    {
        // Plan 2 was requested by op.joao (no stock.adjust): the rights of the requester decide, not the approver's.
        _engine.Approve(_maria, 2);

        Assert.Equal("failed", Status(2));
        Assert.Contains("seu papel não permite", Text("SELECT failure_reason FROM plans WHERE id = 2"));
        Assert.Equal("", Text("SELECT approved_by FROM plans WHERE id = 2"));
        Assert.Equal(1, _f.Scalar("SELECT COUNT(*) FROM audit_log WHERE action = 'plan.failed' AND details_json LIKE '%\"approver\":2%'"));
        AssertChainValid();
    }

    [Fact]
    public void Second_supervisor_approves_and_the_plan_executes()
    {
        var bia = AddSupervisor("sup.bia", 1);
        var id = SubmitCritical(_maria);

        _engine.Approve(bia, id);

        Assert.Equal("executed", Status(id));
        Assert.Equal(bia.UserId.ToString(), Text("SELECT approved_by FROM plans WHERE id = $id", ("$id", id)));
        Assert.Equal(10, Qty("ABC-123", "DOCA-01"));
        Assert.Equal(1, Audits("plan.approve", "allowed"));
        Assert.Equal(1, _f.Scalar("SELECT COUNT(*) FROM audit_log WHERE action = 'plan.execute' AND target = $t AND on_behalf_of = $u",
            ("$t", $"plan:{id}"), ("$u", _maria.UserId)));
        Assert.Single(PlanDiffJson.Deserialize(Text("SELECT simulated_diff_json FROM plans WHERE id = $id", ("$id", id))).Adjustments);
        AssertChainValid();
    }

    [Fact]
    public void Self_approval_is_refused_audited_and_leaves_the_plan_pending()
    {
        var id = SubmitCritical(_maria);

        Assert.Throws<UnauthorizedAccessException>(() => _engine.Approve(_maria, id));

        Assert.Equal("pending_approval", Status(id));
        Assert.Equal(40, Qty("ABC-123", "DOCA-01"));
        Assert.Equal(1, Audits("plan.approve", "denied"));
        AssertChainValid();
    }

    [Fact]
    public void Operator_cannot_approve()
    {
        Assert.Throws<PolicyDeniedException>(() => _engine.Approve(_joao, 2));
        Assert.Equal("pending_approval", Status(2));
    }

    [Fact]
    public void Ai_role_cannot_approve_or_reject()
    {
        var ai = _f.SessionFor("svc.ia");
        Assert.Throws<UnauthorizedAccessException>(() => _engine.Approve(ai, 2));
        Assert.Throws<UnauthorizedAccessException>(() => _engine.Reject(ai, 2, "motivo"));
        Assert.Equal("pending_approval", Status(2));
    }

    [Fact]
    public void Plan_that_became_invalid_fails_on_approval_and_applies_nothing()
    {
        var bia = AddSupervisor("sup.bia", 1);
        var id = SubmitCritical(_maria);
        // Another plan drains the source before the approval.
        Assert.Equal("executed", _engine.Submit(_maria, Json(Move("ABC-123", 40, "DOCA-01", "A-02-01")), "user").Status);

        _engine.Approve(bia, id);

        Assert.Equal("failed", Status(id));
        Assert.NotEqual("", Text("SELECT failure_reason FROM plans WHERE id = $id", ("$id", id)));
        Assert.Equal(0, Qty("ABC-123", "DOCA-01"));
        Assert.Equal(40, Qty("ABC-123", "A-02-01"));
        Assert.Equal("", Text("SELECT approved_by FROM plans WHERE id = $id", ("$id", id)));
        AssertChainValid();
    }

    [Fact]
    public void Finished_plans_cannot_be_approved_or_rejected_again()
    {
        var bia = AddSupervisor("sup.bia", 1);
        var id = SubmitCritical(_maria);
        _engine.Approve(bia, id);

        Assert.Throws<InvalidOperationException>(() => _engine.Approve(bia, id));
        Assert.Throws<InvalidOperationException>(() => _engine.Reject(bia, id, "tarde demais"));
        Assert.Throws<InvalidOperationException>(() => _engine.Approve(bia, 9999));
    }

    [Fact]
    public void Supervisor_of_another_warehouse_cannot_approve_or_reject()
    {
        SqliteTestData.AddSecondWarehouse(_f);
        var other = AddSupervisor("sup.zeca", 2);
        var id = SubmitCritical(_maria);

        Assert.Throws<InvalidOperationException>(() => _engine.Approve(other, id));
        Assert.Throws<InvalidOperationException>(() => _engine.Reject(other, id, "motivo"));
        Assert.Equal("pending_approval", Status(id));
    }

    // ---- reject -------------------------------------------------------------------------------------------

    [Fact]
    public void Reject_stores_the_decider_and_the_trimmed_reason()
    {
        _engine.Reject(_maria, 2, "  Contagem não confere  ");

        Assert.Equal("rejected", Status(2));
        Assert.Equal("Contagem não confere", Text("SELECT rejection_reason FROM plans WHERE id = 2"));
        Assert.Equal(_maria.UserId.ToString(), Text("SELECT rejected_by FROM plans WHERE id = 2"));
        Assert.Equal(1, Audits("plan.reject", "allowed"));
        AssertChainValid();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Reject_requires_a_reason(string reason)
    {
        Assert.Throws<ArgumentException>(() => _engine.Reject(_maria, 2, reason));
        Assert.Equal("pending_approval", Status(2));
    }

    [Fact]
    public void Reject_reason_is_limited_to_200_characters()
    {
        Assert.Throws<ArgumentException>(() => _engine.Reject(_maria, 2, new string('x', 201)));
        _engine.Reject(_maria, 2, new string('x', 200));
        Assert.Equal("rejected", Status(2));
    }

    [Fact]
    public void Requester_cannot_reject_their_own_plan()
    {
        var id = SubmitCritical(_maria);

        Assert.Throws<UnauthorizedAccessException>(() => _engine.Reject(_maria, id, "desisti"));

        Assert.Equal("pending_approval", Status(id));
        Assert.Equal(1, Audits("plan.reject", "denied"));
    }

    [Fact]
    public void Operator_cannot_reject()
    {
        Assert.Throws<PolicyDeniedException>(() => _engine.Reject(_joao, 2, "motivo"));
    }

    // ---- diff json ----------------------------------------------------------------------------------------

    [Fact]
    public void Diff_json_round_trips_and_tolerates_legacy_text()
    {
        var preview = _engine.Preview(_maria, Json(
            Move("ABC-123", 10, "DOCA-01", "A-02-01"), Adjust("ABC-123", -5, "DOCA-01")));
        var diff = preview.Diff!;

        Assert.True(PlanDiffJson.Deserialize(PlanDiffJson.Serialize(diff)).SameAs(diff));
        Assert.True(PlanDiffJson.Deserialize("{\"ABC-123\":{\"DOCA-01\":-40,\"A-01-01\":40}}").IsEmpty);
        Assert.True(PlanDiffJson.Deserialize("not json").IsEmpty);
        Assert.True(PlanDiffJson.Deserialize(null).IsEmpty);
        Assert.True(PlanDiffJson.Deserialize("{\"v\":1,\"moves\":[{\"sku\":1}]}").IsEmpty);
    }
}
