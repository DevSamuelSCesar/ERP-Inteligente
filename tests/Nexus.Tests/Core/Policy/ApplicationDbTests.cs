#pragma warning disable xUnit1051 // the CancellationToken parameters under test are optional and irrelevant here
using erp_inteligente.Core.Policy;
using erp_inteligente.Models;

namespace Nexus.Tests.Core.Policy;

public class ApplicationDbTests : IDisposable
{
    private readonly PolicyTestFixture _f = new();

    public void Dispose() => _f.Dispose();

    private const string StockSql =
        "SELECT sb.id, sb.warehouse_id FROM stock_balances sb WHERE sb.warehouse_id = $warehouse_id ORDER BY sb.id";

    private static (long Id, long Warehouse) Row(Microsoft.Data.Sqlite.SqliteDataReader r) => (r.GetInt64(0), r.GetInt64(1));

    private Task<IReadOnlyList<(long Id, long Warehouse)>> Stock(UserSession s) =>
        _f.App.QueryScopedAsync(s, Permissions.StockRead, StockSql, Row);

    private long AuditCount() => _f.Scalar("SELECT count(*) FROM audit_log");

    [Fact]
    public async Task Scoped_query_without_warehouse_parameter_throws_before_db()
    {
        var sup = _f.SessionFor("sup.maria");
        var before = AuditCount();
        // Even a session lacking the permission gets ArgumentException (and no audit row), proving no DB access.
        var op = _f.SessionFor("op.joao");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _f.App.QueryScopedAsync(op, "audit.read", "SELECT id FROM stock_balances", r => r.GetInt64(0)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _f.App.QueryScopedAsync(sup, Permissions.StockRead, "SELECT id FROM stock_balances", r => r.GetInt64(0)));
        Assert.Equal(before, AuditCount());
    }

    [Theory]
    [InlineData("DELETE FROM stock_balances WHERE warehouse_id = $warehouse_id")]
    [InlineData("UPDATE stock_balances SET quantity = 0 WHERE warehouse_id = $warehouse_id")]
    [InlineData("SELECT 1 WHERE $warehouse_id = 1; DELETE FROM stock_balances")]
    [InlineData("SELECT 1 WHERE $warehouse_id = 1; SELECT 2;")]
    [InlineData("  ")]
    public async Task Non_read_or_multi_statement_is_rejected(string sql)
    {
        var sup = _f.SessionFor("sup.maria");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _f.App.QueryScopedAsync(sup, Permissions.StockRead, sql, r => r.GetInt64(0)));
        Assert.True(_f.Scalar("SELECT count(*) FROM stock_balances") > 0);
    }

    [Fact]
    public async Task Cte_write_cannot_modify_data()
    {
        var sup = _f.SessionFor("sup.maria");
        var before = _f.Scalar("SELECT sum(quantity) FROM stock_balances");
        await Assert.ThrowsAnyAsync<Exception>(() => _f.App.QueryScopedAsync(sup, Permissions.StockRead,
            "WITH w AS (SELECT $warehouse_id AS w) UPDATE stock_balances SET quantity = 0", r => r.GetInt64(0)));
        Assert.Equal(before, _f.Scalar("SELECT sum(quantity) FROM stock_balances"));
    }

    [Fact]
    public async Task Caller_cannot_bind_warehouse_id()
    {
        var sup = _f.SessionFor("sup.maria");
        await Assert.ThrowsAsync<ArgumentException>(() => _f.App.QueryScopedAsync(
            sup, Permissions.StockRead, StockSql, Row, p => p.AddWithValue("$warehouse_id", 2)));
    }

    [Fact]
    public async Task Global_query_rejects_warehouse_parameter_and_works_otherwise()
    {
        var sup = _f.SessionFor("sup.maria");
        await Assert.ThrowsAsync<ArgumentException>(() => _f.App.QueryGlobalAsync(
            sup, Permissions.StockRead, "SELECT id FROM products WHERE $warehouse_id = 1", r => r.GetInt64(0)));

        var skus = await _f.App.QueryGlobalAsync(sup, Permissions.StockRead,
            "SELECT sku FROM products WHERE sku = $sku;", r => r.GetString(0), p => p.AddWithValue("$sku", "ABC-123"));
        Assert.Equal(["ABC-123"], skus);
    }

    [Fact]
    public async Task Query_without_permission_is_denied_and_audited()
    {
        var op = _f.SessionFor("op.joao");
        await Assert.ThrowsAsync<PolicyDeniedException>(() =>
            _f.App.QueryGlobalAsync(op, Permissions.AuditRead, "SELECT id FROM audit_log", r => r.GetInt64(0)));
        Assert.Equal(1, _f.Scalar("SELECT count(*) FROM audit_log WHERE action = 'policy.denied'"));
    }

    [Fact]
    public async Task Warehouse_isolation()
    {
        _f.Exec("INSERT INTO warehouses (id, code, name) VALUES (2, 'CD-02', 'Outro')");
        _f.Exec("INSERT INTO zones (id, warehouse_id, code, name) VALUES (90, 2, 'Z2', 'Zona 2')");
        _f.Exec("INSERT INTO locations (id, zone_id, code, capacity) VALUES (90, 90, 'X-01', 10)");
        _f.Exec("INSERT INTO stock_balances (id, warehouse_id, product_id, location_id, lot_id, quantity) " +
                "VALUES (9000, 2, 1, 90, NULL, 7)");

        var sup = _f.SessionFor("sup.maria");
        var rows1 = await Stock(sup);
        Assert.NotEmpty(rows1);
        Assert.All(rows1, r => Assert.Equal(1, r.Warehouse));
        Assert.DoesNotContain(rows1, r => r.Id == 9000);

        var other = sup with { WarehouseId = 2, WarehouseCode = "CD-02" };
        var rows2 = await Stock(other);
        Assert.Equal([(9000L, 2L)], rows2);
    }

    [Fact]
    public async Task Cancelled_token_is_honoured()
    {
        var sup = _f.SessionFor("sup.maria");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _f.App.QueryScopedAsync(sup, Permissions.StockRead, StockSql, Row, null, cts.Token));
    }
}
