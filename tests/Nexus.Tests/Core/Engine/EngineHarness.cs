using erp_inteligente.Core.Engine;
using erp_inteligente.Models;
using Nexus.Tests.Core.Policy;

namespace Nexus.Tests.Core.Engine;

/// <summary>Seeded database plus helpers to build, simulate and execute plans for the engine tests.</summary>
public sealed class EngineHarness : IDisposable
{
    public EngineHarness()
    {
        Fixture = new PolicyTestFixture();
        Supervisor = Fixture.SessionFor("sup.maria");
        Operator = Fixture.SessionFor("op.joao");
        Simulator = new PlanSimulator(Fixture.Db.Database, Fixture.Time);
        Executor = new PlanExecutor(Fixture.Db.Database, Fixture.Time);
    }

    public PolicyTestFixture Fixture { get; }
    public UserSession Supervisor { get; }
    public UserSession Operator { get; }
    public PlanSimulator Simulator { get; }
    public PlanExecutor Executor { get; }

    public void Dispose() => Fixture.Dispose();

    public static PlanDocument Plan(params string[] steps)
    {
        var json = "{\"dsl_version\":\"1.0\",\"intent\":\"teste\",\"steps\":[" + string.Join(",", steps) + "]}";
        var parsed = DslParser.Parse(json);
        Assert.True(parsed.Errors.Count == 0, string.Join("; ", parsed.Errors.Select(e => e.Message)));
        return parsed.Document!;
    }

    public RunResult Execute(PlanDocument plan, UserSession? who = null, long planId = 1)
    {
        var session = who ?? Supervisor;
        return Executor.Execute(session, plan, planId, session.Has, (_, _, _) => { });
    }

    public RunResult Simulate(PlanDocument plan, UserSession? who = null)
    {
        var session = who ?? Supervisor;
        return Simulator.Simulate(session, plan, session.Has);
    }

    /// <summary>Executes and expects exactly one error; returns its message.</summary>
    public string ExecuteRefused(PlanDocument plan, UserSession? who = null)
    {
        var result = Execute(plan, who);
        Assert.False(result.Succeeded);
        return Assert.Single(result.Errors).Message;
    }

    public long Qty(string sku, string location, string? lot = null) =>
        Fixture.Scalar(
            "SELECT COALESCE(SUM(b.quantity), 0) FROM stock_balances b JOIN products p ON p.id = b.product_id " +
            "JOIN locations l ON l.id = b.location_id LEFT JOIN lots t ON t.id = b.lot_id " +
            "WHERE p.sku = $sku AND l.code = $loc AND ($lot IS NULL OR t.lot_code = $lot)",
            ("$sku", sku), ("$loc", location), ("$lot", (object?)lot ?? DBNull.Value));

    public long Count(string table) => Fixture.Scalar($"SELECT COUNT(*) FROM {table}");

    public long[] Counts() =>
        new[] { "stock_balances", "stock_movements", "products", "locations", "lots" }.Select(Count).ToArray();

    public static string Receive(string sku, int qty, string location, string extra = "") =>
        $"{{\"op\":\"receive_goods\",\"args\":{{\"sku\":\"{sku}\",\"qty\":{qty},\"location\":\"{location}\"{extra}}}}}";

    public static string Move(string sku, int qty, string from, string to, string extra = "") =>
        $"{{\"op\":\"move_stock\",\"args\":{{\"sku\":\"{sku}\",\"qty\":{qty},\"from\":\"{from}\",\"to\":\"{to}\"{extra}}}}}";

    public static string Adjust(string sku, int qty, string location, string extra = "") =>
        $"{{\"op\":\"adjust_stock\",\"args\":{{\"sku\":\"{sku}\",\"qty\":{qty},\"location\":\"{location}\",\"reason\":\"Inventário\"{extra}}}}}";
}
