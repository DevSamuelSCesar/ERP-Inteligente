using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Policy;
using erp_inteligente.Models;
using erp_inteligente.Services.Sqlite;
using Nexus.Tests.Core.Policy;

namespace Nexus.Tests.Services;

/// <summary>Queries against the seeded database (fictitious data, warehouse CD-01).</summary>
public class SqliteQueryServiceTests : IDisposable
{
    private readonly PolicyTestFixture _f = new();
    private readonly FakeSessionService _session;
    private readonly SqliteQueryService _queries;

    public SqliteQueryServiceTests()
    {
        _session = new FakeSessionService(_f.SessionFor("op.joao"));
        _queries = new SqliteQueryService(_f.App, _session);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task Stock_lists_the_thirteen_positive_balances_ordered_by_sku_and_location()
    {
        var rows = await _queries.GetStockAsync();

        Assert.Equal(13, rows.Count);
        Assert.Equal(("ABC-123", "A-01-01"), (rows[0].Sku, rows[0].Location));
        Assert.Equal(("ABC-123", "DOCA-01"), (rows[1].Sku, rows[1].Location));
        Assert.Equal(("TST-INJ-01", "A-02-01"), (rows[^1].Sku, rows[^1].Location));
    }

    [Fact]
    public async Task Stock_row_without_lot_has_empty_lot_and_no_expiry()
    {
        var row = (await _queries.GetStockAsync()).Single(r => r.Sku == "ABC-123" && r.Location == "A-01-01");

        Assert.Equal(("Caixa de parafusos 4mm", "", 40, 0, "cx"), (row.Product, row.Lot, row.Quantity, row.Reserved, row.Unit));
        Assert.Null(row.ExpiresOn);
        Assert.Equal("", row.ExpiresOnText);
    }

    [Fact]
    public async Task Stock_row_with_lot_carries_expiry_and_reserved_quantity()
    {
        var rows = await _queries.GetStockAsync();

        var lot = rows.Single(r => r.Sku == "LIM-200" && r.Location == "A-01-03");
        Assert.Equal(("L200-2611", (DateOnly?)new DateOnly(2026, 11, 30), "30/11/2026"), (lot.Lot, lot.ExpiresOn, lot.ExpiresOnText));

        var reserved = rows.Single(r => r.Sku == "ALI-300" && r.Lot == "L300-2611A");
        Assert.Equal((16, 6, 10), (reserved.Quantity, reserved.Reserved, reserved.Available));
    }

    [Fact]
    public async Task Stock_hides_zero_balances()
    {
        _f.Exec("INSERT INTO stock_balances (warehouse_id, product_id, location_id, lot_id, quantity, reserved_qty) " +
                "VALUES (1, 9, 15, NULL, 0, 0)");

        var rows = await _queries.GetStockAsync();

        Assert.Equal(13, rows.Count);
        Assert.DoesNotContain(rows, r => r.Sku == "EMB-500" && r.Location == "C-01-01");
    }

    [Fact]
    public async Task Stock_is_refused_when_the_role_has_stock_read_denied()
    {
        _f.Exec("INSERT INTO role_permissions (role_id, permission, effect) VALUES (1, 'stock.read', 'DENY')");
        _session.Current = _f.SessionFor("op.joao");

        await Assert.ThrowsAsync<PolicyDeniedException>(() => _queries.GetStockAsync());
    }

    [Fact]
    public async Task Products_list_the_catalog_with_the_warehouse_balance()
    {
        var rows = await _queries.GetProductsAsync();

        Assert.Equal(rows.Select(r => r.Sku).OrderBy(s => s, StringComparer.Ordinal), rows.Select(r => r.Sku));
        var abc = rows.Single(r => r.Sku == "ABC-123");
        var expected = (await _queries.GetStockAsync()).Where(r => r.Sku == "ABC-123").Sum(r => r.Quantity);
        Assert.Equal(("Caixa de parafusos 4mm", "cx", expected), (abc.Name, abc.Unit, abc.Balance));
    }

    [Fact]
    public async Task Product_created_without_stock_is_listed_with_zero_balance()
    {
        _f.Exec("INSERT INTO products (sku, name, unit, tracks_expiry, unit_cost_enc) VALUES ('XYZ-001', 'Parafuso sextavado', 'cx', 1, X'01')");

        var row = (await _queries.GetProductsAsync()).Single(r => r.Sku == "XYZ-001");

        Assert.Equal((0, true, "Sim", ""), (row.Balance, row.TracksExpiry, row.TracksExpiryText, row.WeightText));
    }

    [Fact]
    public async Task Product_balance_ignores_other_warehouses()
    {
        var before = (await _queries.GetProductsAsync()).Single(r => r.Sku == "ABC-123").Balance;
        SqliteTestData.AddSecondWarehouse(_f);

        Assert.Equal(before, (await _queries.GetProductsAsync()).Single(r => r.Sku == "ABC-123").Balance);
        _session.Current = _f.SessionFor("op.ana");
        Assert.Equal(99, (await _queries.GetProductsAsync()).Single(r => r.Sku == "ABC-123").Balance);
    }

    [Fact]
    public async Task Zones_list_the_warehouse_zones_with_capacity_and_occupancy()
    {
        var rows = await _queries.GetZonesAsync();

        Assert.Equal(["A", "B", "C", "DOCA"], rows.Select(r => r.Code));
        Assert.All(rows, r => Assert.True(r.IsActive));
        var locations = await _queries.GetLocationsAsync();
        Assert.Equal(locations.Sum(l => l.Capacity), rows.Sum(r => r.Capacity));
        Assert.Equal(locations.Sum(l => l.Occupied), rows.Sum(r => r.Occupied));
    }

    [Fact]
    public async Task Zones_of_another_warehouse_never_appear()
    {
        SqliteTestData.AddSecondWarehouse(_f);

        Assert.DoesNotContain(await _queries.GetZonesAsync(), z => z.Name == "Docas do CD 02");
    }

    [Fact]
    public async Task Partners_show_only_masked_documents()
    {
        var rows = await _queries.GetPartnersAsync();

        Assert.Contains(rows, r => r.Kind == "Fornecedor");
        Assert.Contains(rows, r => r.Kind == "Cliente");
        Assert.All(rows.Where(r => r.Document.Length > 0), r => Assert.Contains("*", r.Document));
    }

    [Fact]
    public async Task Locations_list_every_location_with_zone_capacity_and_occupancy()
    {
        var rows = await _queries.GetLocationsAsync();

        Assert.Equal(16, rows.Count);
        Assert.Equal("A-01-01", rows[0].Code);
        Assert.Equal(("DOCA-01", "Docas de recebimento e expedição", 500, 40), Find(rows, "DOCA-01"));
        Assert.Equal(("DOCA-02", "Docas de recebimento e expedição", 500, 0), Find(rows, "DOCA-02"));
        Assert.Equal(("A-02-02", "Porta-paletes seco", 60, 0), Find(rows, "A-02-02"));
    }

    [Fact]
    public async Task Location_occupancy_sums_both_lots_of_the_same_location()
    {
        var cold = (await _queries.GetLocationsAsync()).Single(l => l.Code == "C-01-01");

        Assert.Equal((40, 36, 4, "90%"), (cold.Capacity, cold.Occupied, cold.Free, cold.OccupancyText));
        Assert.Equal("Câmara refrigerada", cold.Zone);
    }

    [Fact]
    public async Task Orders_union_inbound_and_outbound_with_portuguese_status_and_totals()
    {
        var rows = await _queries.GetOrdersAsync();

        Assert.Equal(4, rows.Count);
        var first = Order(rows, "NF-FICT-000101");
        Assert.Equal(
            ("Entrada", "Fornecedora Fictícia Alfa Ltda", 2, 115, "Recebido", new DateOnly(2026, 10, 20)),
            (first.Kind, first.Party, first.Lines, first.Units, first.Status, first.DueOn));
        Assert.Equal(("NF-FICT-000102", "Entrada", "Previsto", 68), Summary(rows, "NF-FICT-000102"));
        Assert.Equal(("PED-FICT-5001", "Saída", "Reservado", 12), Summary(rows, "PED-FICT-5001"));
        Assert.Equal(("PED-FICT-5002", "Saída", "Aberto", 10), Summary(rows, "PED-FICT-5002"));
    }

    [Fact]
    public async Task Orders_map_shipped_and_cancelled_and_show_missing_due_date_as_minimum()
    {
        _f.Exec("INSERT INTO outbound_orders (id, warehouse_id, customer_id, order_number, status, due_on) VALUES " +
                "(3, 1, 1, 'PED-FICT-5003', 'shipped', '2026-10-15'), " +
                "(4, 1, 1, 'PED-FICT-5004', 'cancelled', NULL)");

        var rows = await _queries.GetOrdersAsync();

        Assert.Equal(6, rows.Count);
        Assert.Equal("Expedido", Summary(rows, "PED-FICT-5003").Status);
        Assert.Equal("Cancelado", Summary(rows, "PED-FICT-5004").Status);
        Assert.Equal(DateOnly.MinValue, Order(rows, "PED-FICT-5004").DueOn);
    }

    [Fact]
    public async Task Order_status_picking_has_portuguese_label()
    {
        // Every status allowed by schema.sql has a Portuguese label.
        _f.Exec("INSERT INTO outbound_orders (id, warehouse_id, customer_id, order_number, status, due_on) " +
                "VALUES (5, 1, 2, 'PED-FICT-5005', 'picking', '2026-10-30')");

        var rows = await _queries.GetOrdersAsync();

        Assert.Equal("Em separação", Summary(rows, "PED-FICT-5005").Status);
    }

    [Fact]
    public async Task Recipes_show_skill_without_version_and_active_label()
    {
        var rows = await _queries.GetRecipesAsync();

        var recipe = Assert.Single(rows);
        Assert.Equal(("armazenar_da_doca", "armazenagem", 7, "Ativa"), (recipe.Name, recipe.Skill, recipe.Uses, recipe.Status));
        Assert.StartsWith("Move a quantidade recebida", recipe.Description);
    }

    [Fact]
    public async Task Suspended_recipe_is_labelled_in_portuguese()
    {
        _f.Exec("INSERT INTO recipes (id, name, description, intent, plan_template_json, params_schema_json, dsl_version, skill_version, status, success_count) " +
                "VALUES (2, 'repor_camara', 'Repõe a câmara', 'putaway', '{}', '{}', '1.0', 'refrigerados@2.0', 'suspended', 3)");

        var rows = await _queries.GetRecipesAsync();

        var suspended = rows.Single(r => r.Name == "repor_camara");
        Assert.Equal(("refrigerados", "Suspensa", 3), (suspended.Skill, suspended.Status, suspended.Uses));
    }

    [Fact]
    public async Task Audit_of_the_seed_labels_actions_and_users_newest_first()
    {
        _session.Current = _f.SessionFor("sup.maria");

        var rows = await _queries.GetAuditAsync();

        Assert.Equal(4, rows.Count);
        Assert.Equal(("Nexus por João (operador)", "Ajuste de estoque", "sku:TST-INJ-01 · negado"),
            (rows[0].User, rows[0].Action, rows[0].Detail));
        Assert.Equal(("Nexus por João (operador)", "Plano enviado", "plan:2"), (rows[1].User, rows[1].Action, rows[1].Detail));
        Assert.Equal(("Sistema por João (operador)", "Plano executado", "plan:1"), (rows[2].User, rows[2].Action, rows[2].Detail));
        Assert.Equal(("Nexus por João (operador)", "Plano enviado", "plan:1"), (rows[3].User, rows[3].Action, rows[3].Detail));
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 10, 1, 0, TimeSpan.Zero), rows[3].At);
    }

    [Fact]
    public async Task Audit_of_user_actions_shows_actor_name_unknown_actor_failures_and_raw_actions()
    {
        _session.Current = _f.SessionFor("sup.maria");
        _f.Audit.Append(new AuditEntry(AuditActorType.User, 1, null, "auth.login", null, AuditOutcome.Allowed));
        _f.Audit.Append(new AuditEntry(AuditActorType.User, null, null, "auth.login_failed", "user:x", AuditOutcome.Failed));
        _f.Audit.Append(new AuditEntry(AuditActorType.System, null, null, "custom.thing", null, AuditOutcome.Allowed));

        var rows = await _queries.GetAuditAsync();

        Assert.Equal(("Sistema", "custom.thing", ""), (rows[0].User, rows[0].Action, rows[0].Detail));
        Assert.Equal(("Desconhecido", "Falha de login", "user:x · falhou"), (rows[1].User, rows[1].Action, rows[1].Detail));
        Assert.Equal(("João (operador)", "Login", ""), (rows[2].User, rows[2].Action, rows[2].Detail));
    }

    [Fact]
    public async Task Audit_is_limited_to_the_500_most_recent_rows()
    {
        _session.Current = _f.SessionFor("sup.maria");
        _f.Exec("WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 600) " +
                "INSERT INTO audit_log (actor_type, action, outcome, prev_hash, hash) " +
                "SELECT 'system', 'plan.execute', 'allowed', 'test', 'h' || i FROM n");

        var rows = await _queries.GetAuditAsync();

        Assert.Equal(500, rows.Count);
        // The synthetic rows have no target, so none of the four seed rows (all with a target) can be in the result.
        Assert.All(rows, r => Assert.Equal("", r.Detail));
    }

    [Fact]
    public async Task Operator_without_audit_read_gets_policy_denied_and_the_denial_is_audited()
    {
        await Assert.ThrowsAsync<PolicyDeniedException>(() => _queries.GetAuditAsync());

        Assert.Equal(1, _f.Scalar(
            "SELECT count(*) FROM audit_log WHERE action = 'policy.denied' AND target = 'permission:audit.read' " +
            "AND actor_id = $id", ("$id", _session.Current.UserId)));
    }

    [Fact]
    public async Task Warehouse_two_stock_and_locations_are_not_visible_to_warehouse_one()
    {
        SqliteTestData.AddSecondWarehouse(_f);

        var stock = await _queries.GetStockAsync();
        var locations = await _queries.GetLocationsAsync();
        var orders = await _queries.GetOrdersAsync();

        Assert.Equal(13, stock.Count);
        Assert.DoesNotContain(stock, r => r.Location == "DOCA-99");
        Assert.Equal(16, locations.Count);
        Assert.DoesNotContain(locations, l => l.Code == "DOCA-99");
        Assert.Equal(4, orders.Count);
    }

    [Fact]
    public async Task Warehouse_two_user_sees_only_warehouse_two_stock()
    {
        SqliteTestData.AddSecondWarehouse(_f);
        _session.Current = _f.SessionFor("op.ana");

        var stock = await _queries.GetStockAsync();

        var row = Assert.Single(stock);
        Assert.Equal(("ABC-123", "DOCA-99", 99), (row.Sku, row.Location, row.Quantity));
    }

    private static (string Code, string Zone, int Capacity, int Occupied) Find(IReadOnlyList<LocationRow> rows, string code)
    {
        var row = rows.Single(l => l.Code == code);
        return (row.Code, row.Zone, row.Capacity, row.Occupied);
    }

    private static OrderRow Order(IReadOnlyList<OrderRow> rows, string number) => rows.Single(o => o.Number == number);

    private static (string Number, string Kind, string Status, int Units) Summary(IReadOnlyList<OrderRow> rows, string number)
    {
        var row = Order(rows, number);
        return (row.Number, row.Kind, row.Status, row.Units);
    }
}
