using erp_inteligente.Core.Engine;
using static Nexus.Tests.Core.Engine.EngineHarness;

namespace Nexus.Tests.Core.Engine;

public sealed class RunnerTests : IDisposable
{
    private readonly EngineHarness _h = new();

    public void Dispose() => _h.Dispose();

    private PlanDocument FullPlan() => Plan(
        Receive("ABC-123", 10, "DOCA-01"),
        Move("ABC-123", 20, "DOCA-01", "A-02-01"),
        Adjust("ALI-300", -1, "C-01-01", ",\"lot\":\"L300-2611A\""),
        "{\"op\":\"create_product\",\"args\":{\"sku\":\"NEW-003\",\"name\":\"Novo\",\"unit\":\"un\",\"unit_cost\":3}}",
        "{\"op\":\"create_location\",\"args\":{\"code\":\"A-03-03\",\"zone\":\"A\",\"capacity\":10}}");

    [Fact]
    public void Simulation_writes_nothing_and_matches_the_real_execution()
    {
        var plan = FullPlan();
        var before = _h.Counts();
        var simulated = _h.Simulate(plan);
        Assert.True(simulated.Succeeded);
        Assert.Equal(before, _h.Counts());
        Assert.Equal(RiskLevel.Critical, simulated.Risk);

        var real = _h.Execute(plan);
        Assert.True(real.Succeeded);
        Assert.NotEqual(before, _h.Counts());
        Assert.True(simulated.Diff!.SameAs(real.Diff!));
    }

    [Fact]
    public void Simulation_leaves_no_plan_id_on_movements_and_reports_errors()
    {
        var bad = Plan(Move("ABC-124", 30, "A-01-02", "A-02-02"));
        var result = _h.Simulate(bad);
        Assert.False(result.Succeeded);
        Assert.Null(result.Diff);
        Assert.Contains("insuficiente", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public void A_failing_second_step_leaves_no_trace_of_the_first()
    {
        var before = _h.Counts();
        var qty = _h.Qty("ABC-123", "DOCA-01");
        var plan = Plan(Receive("ABC-123", 10, "DOCA-01"), Move("ABC-124", 30, "A-01-02", "A-02-02"));
        var result = _h.Execute(plan);
        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Errors);
        Assert.Equal(1, error.StepIndex);
        Assert.StartsWith("Passo 2: ", error.Message);
        Assert.Equal(before, _h.Counts());
        Assert.Equal(qty, _h.Qty("ABC-123", "DOCA-01"));
    }

    [Fact]
    public void A_throwing_on_success_hook_rolls_everything_back()
    {
        var before = _h.Counts();
        var plan = Plan(Receive("ABC-123", 10, "DOCA-01"));
        Assert.Throws<InvalidOperationException>(() =>
            _h.Executor.Execute(_h.Supervisor, plan, 1, _h.Supervisor.Has,
                (connection, tx, _) =>
                {
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "UPDATE plans SET failure_reason = 'x' WHERE id = 2";
                    cmd.ExecuteNonQuery();
                    throw new InvalidOperationException("boom");
                }));
        Assert.Equal(before, _h.Counts());
        Assert.Equal(0, _h.Fixture.Scalar("SELECT COUNT(*) FROM plans WHERE failure_reason = 'x'"));
    }

    [Fact]
    public void On_success_hook_commits_with_the_plan_effects()
    {
        var plan = Plan(Receive("ABC-123", 10, "DOCA-01"));
        RunResult? seen = null;
        _h.Executor.Execute(_h.Supervisor, plan, 1, _h.Supervisor.Has, (connection, tx, result) =>
        {
            seen = result;
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE plans SET failure_reason = 'ok-hook' WHERE id = 2";
            cmd.ExecuteNonQuery();
        });
        Assert.NotNull(seen);
        Assert.Equal(50, _h.Qty("ABC-123", "DOCA-01"));
        Assert.Equal(1, _h.Fixture.Scalar("SELECT COUNT(*) FROM plans WHERE failure_reason = 'ok-hook'"));
    }

    [Fact]
    public void On_failure_hook_runs_in_a_new_committed_transaction()
    {
        var plan = Plan(Receive("ABC-123", 10, "DOCA-01"), Move("ABC-124", 30, "A-01-02", "A-02-02"));
        var result = _h.Executor.Execute(_h.Supervisor, plan, 1, _h.Supervisor.Has,
            (_, _, _) => Assert.Fail("must not succeed"),
            (connection, tx, run) =>
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE plans SET failure_reason = $r WHERE id = 2";
                cmd.Parameters.AddWithValue("$r", run.Errors[0].Message);
                cmd.ExecuteNonQuery();
            });
        Assert.False(result.Succeeded);
        Assert.Equal(40, _h.Qty("ABC-123", "DOCA-01"));
        Assert.Equal(1, _h.Fixture.Scalar("SELECT COUNT(*) FROM plans WHERE id = 2 AND failure_reason LIKE 'Passo 2:%'"));
    }

    [Fact]
    public void Operator_is_denied_adjustments_and_catalog_changes_and_nothing_runs()
    {
        var before = _h.Counts();
        var plan = Plan(
            Receive("ABC-123", 10, "DOCA-01"),
            Adjust("ABC-123", 1, "A-01-01"),
            "{\"op\":\"create_product\",\"args\":{\"sku\":\"NEW-004\",\"name\":\"Novo\",\"unit\":\"un\"}}");
        var result = _h.Execute(plan, _h.Operator);
        Assert.False(result.Succeeded);
        Assert.Equal([1, 2], result.Errors.Select(e => e.StepIndex!.Value).ToArray());
        Assert.All(result.Errors, e => Assert.Equal("policy.denied", e.Code));
        Assert.Equal("Passo 2: seu papel não permite Ajuste de estoque.", result.Errors[0].Message);
        Assert.Equal(before, _h.Counts());
        Assert.False(_h.Simulate(plan, _h.Operator).Succeeded);
    }

    [Fact]
    public void Operator_can_receive_and_move()
    {
        var plan = Plan(Receive("ABC-123", 10, "DOCA-01"), Move("ABC-123", 5, "DOCA-01", "A-02-01"));
        Assert.True(_h.Execute(plan, _h.Operator).Succeeded);
    }

    [Fact]
    public void Schema_errors_stop_before_anything_runs()
    {
        var parsed = DslParser.Parse(
            "{\"dsl_version\":\"1.0\",\"intent\":\"x\",\"steps\":[{\"op\":\"move_stock\",\"args\":{\"sku\":\"abc\",\"qty\":1,\"from\":\"A-01-01\",\"to\":\"A-01-02\"}}]}");
        var result = _h.Execute(parsed.Document!);
        Assert.False(result.Succeeded);
        Assert.StartsWith("Passo 1: ", result.Errors[0].Message);
    }

    [Fact]
    public void Summary_has_totals_flows_balances_and_created_items()
    {
        var result = _h.Simulate(FullPlan());
        var summary = result.Diff!.ToSummary(result.Risk);
        string Total(string label) => summary.Totals.Single(t => t.Label == label).Value;

        Assert.Equal("20 cx", Total("Quantidade movimentada"));
        Assert.Equal("10 cx", Total("Quantidade recebida"));
        Assert.Equal("-1 cx", Total("Ajuste líquido"));
        Assert.Equal("3", Total("Endereços afetados"));
        Assert.Equal("2", Total("SKUs"));
        Assert.Equal("2", Total("Cadastros"));
        Assert.Equal("Crítico", Total("Risco"));

        var flow = Assert.Single(summary.Flows);
        Assert.Equal(new erp_inteligente.Models.MoveFlow("ABC-123", 20, "cx", "DOCA-01", "A-02-01"), flow);

        // Only receipts and adjustments are balances; the move shows as a flow.
        Assert.Contains(summary.Balances, b => b is { Sku: "ABC-123", Location: "DOCA-01", Before: 40, After: 50 });
        Assert.Contains(summary.Balances, b => b is { Sku: "ALI-300", Location: "C-01-01", Before: 16, After: 15 });
        Assert.DoesNotContain(summary.Balances, b => b.Location == "A-02-01");
        Assert.Equal(["Produto", "Endereço"], summary.CreatedItems.Select(c => c.Kind).ToArray());
    }

    [Fact]
    public void Summary_for_a_reversible_plan_omits_inapplicable_totals()
    {
        var result = _h.Simulate(Plan(Move("ABC-123", 20, "DOCA-01", "A-02-01")));
        var labels = result.Diff!.ToSummary(result.Risk).Totals.Select(t => t.Label).ToArray();
        Assert.Equal(["Quantidade movimentada", "Endereços afetados", "SKUs", "Risco"], labels);
        Assert.Equal("Reversível", result.Diff.ToSummary(result.Risk).Totals[^1].Value);
    }

    [Fact]
    public void Diff_builder_aggregates_first_before_and_last_after()
    {
        var b = new PlanDiffBuilder();
        b.AddDirectBalance("ABC-123", "DOCA-01", null, 40, 50, "cx");
        b.AddDirectBalance("ABC-123", "DOCA-01", null, 50, 45, "cx");
        b.AddDirectBalance("ABC-123", "DOCA-01", "L1", 0, 5, "cx");
        var diff = b.Build();
        Assert.Equal(2, diff.Balances.Count);
        Assert.Equal(new BalanceDelta("ABC-123", "DOCA-01", null, 40, 45, "cx"), diff.Balances[0]);
        var summary = diff.ToSummary(RiskLevel.Reversible);
        Assert.Equal(new erp_inteligente.Models.BalanceChange("ABC-123", "DOCA-01", 40, 50, "cx"), Assert.Single(summary.Balances));
    }

    [Fact]
    public void Mixed_units_are_summed_without_a_unit()
    {
        var b = new PlanDiffBuilder();
        b.AddMove("ABC-123", null, "A-01-01", "A-01-02", 2, "cx");
        b.AddMove("FER-010", null, "B-01-01", "B-01-02", 3, "un");
        var total = b.Build().ToSummary(RiskLevel.Reversible).Totals.Single(t => t.Label == "Quantidade movimentada");
        Assert.Equal("5", total.Value);
    }
}
