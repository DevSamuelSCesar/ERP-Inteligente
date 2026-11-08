using erp_inteligente.Core.Engine;
using erp_inteligente.Models;
using Nexus.Tests.Core.Policy;
using static Nexus.Tests.Core.Engine.EngineHarness;

namespace Nexus.Tests.Core.Engine;

/// <summary>Delivery 2 of the full catalog: inbound orders, outbound order creation, and the reservation lifecycle (reserve, release, pick, ship, cancel).</summary>
public sealed class OrderOperationTests : IDisposable
{
    private const string Supplier = "Fornecedora Fictícia Alfa Ltda";
    private const string Customer = "Loja Fictícia Centro";

    private readonly EngineHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static string Step(string op, string args, string? name = null) =>
        $"{{\"op\":\"{op}\",\"args\":{{{args}}}" + (name is null ? "" : $",\"as\":\"{name}\"") + "}";

    private static string Inbound(string supplier = Supplier, string document = "NF-TESTE-001", string lines = "{\"sku\":\"ABC-123\",\"qty\":10}", string extra = "") =>
        Step("create_inbound_order", $"\"supplier\":\"{supplier}\",\"document_number\":\"{document}\",\"lines\":[{lines}]{extra}");

    private static string Outbound(string customer = Customer, string number = "PED-TESTE-001", string lines = "{\"sku\":\"ABC-123\",\"qty\":3}", string extra = "") =>
        Step("create_outbound_order", $"\"customer\":\"{customer}\",\"order_number\":\"{number}\",\"lines\":[{lines}]{extra}");

    private static string Cancel(string document, string reason = "Fornecedor desistiu") =>
        Step("cancel_inbound_order", $"\"document_number\":\"{document}\",\"reason\":\"{reason}\"");

    private long Value(string sql, params (string, object)[] p) => _h.Fixture.Scalar(sql, p);

    private PlanError Refused(params string[] steps)
    {
        var result = _h.Execute(Plan(steps));
        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Errors);
        Assert.StartsWith("Passo ", error.Message);
        return error;
    }

    private void AddRemoteWarehouseOrders()
    {
        _h.Fixture.Exec("INSERT INTO warehouses (id, code, name) VALUES (2, 'CD2', 'Outro armazém')");
        _h.Fixture.Exec("INSERT INTO inbound_orders (warehouse_id, supplier_id, document_number, status) VALUES (2, 1, 'NF-REMOTA-1', 'expected')");
        _h.Fixture.Exec("INSERT INTO outbound_orders (warehouse_id, customer_id, order_number, status) VALUES (2, 1, 'PED-REMOTO-1', 'open')");
    }

    // ---- create_inbound_order -------------------------------------------------------------------------------

    [Fact]
    public void Create_inbound_order_stores_header_and_lines_and_reports_it()
    {
        var result = _h.Execute(Plan(Inbound(supplier: "fornecedora fictícia ALFA ltda", lines:
            "{\"sku\":\"ABC-123\",\"qty\":10},{\"sku\":\"FER-010\",\"qty\":5}", extra: ",\"expected_on\":\"2026-12-01\"")));
        Assert.True(result.Succeeded);
        Assert.Equal(1, Value("SELECT COUNT(*) FROM inbound_orders WHERE document_number = 'NF-TESTE-001' AND status = 'expected' " +
            "AND warehouse_id = 1 AND supplier_id = 1 AND expected_on = '2026-12-01'"));
        Assert.Equal(15, Value("SELECT SUM(l.expected_qty) FROM inbound_order_lines l JOIN inbound_orders o ON o.id = l.inbound_order_id " +
            "WHERE o.document_number = 'NF-TESTE-001' AND l.received_qty = 0"));
        var created = Assert.Single(result.Diff!.Created);
        Assert.Equal(("Pedido de entrada", "NF-TESTE-001"), (created.Kind, created.Code));
        Assert.Contains("2 linhas, 15 unidades", created.Description);
        Assert.Contains(Supplier, created.Description);
    }

    [Fact]
    public void Created_inbound_order_can_be_received_against()
    {
        var result = _h.Execute(Plan(
            Inbound(extra: ""),
            Receive("ABC-123", 10, "DOCA-01", ",\"inbound_order\":\"NF-TESTE-001\"")));
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.Equal(1, Value("SELECT COUNT(*) FROM inbound_orders WHERE document_number = 'NF-TESTE-001' AND status = 'received'"));
    }

    [Fact]
    public void Inbound_document_must_be_unique_in_the_warehouse_but_not_across_warehouses()
    {
        var duplicate = Refused(Inbound(document: "NF-FICT-000102"));
        Assert.Equal(("document_number", "business.duplicate_document"), (duplicate.Field, duplicate.Code));
        Assert.Contains("NF-FICT-000102", duplicate.Message);

        AddRemoteWarehouseOrders();
        Assert.True(_h.Execute(Plan(Inbound(document: "NF-REMOTA-1"))).Succeeded);
    }

    [Fact]
    public void Inbound_supplier_must_exist_and_be_active()
    {
        Assert.Equal("business.unknown_supplier", Refused(Inbound(supplier: "Ninguém Ltda")).Code);
        _h.Fixture.Exec("UPDATE suppliers SET is_active = 0 WHERE id = 1");
        Assert.Equal("business.inactive_supplier", Refused(Inbound()).Code);
    }

    [Fact]
    public void Inbound_lines_are_checked_for_unknown_and_repeated_skus()
    {
        Assert.Equal("business.unknown_sku", Refused(Inbound(lines: "{\"sku\":\"ZZZ-999\",\"qty\":1}")).Code);
        var repeated = Refused(Inbound(lines: "{\"sku\":\"ABC-123\",\"qty\":1},{\"sku\":\"ABC-123\",\"qty\":2}"));
        Assert.Equal(("lines", "business.duplicate_sku"), (repeated.Field, repeated.Code));
    }

    [Fact]
    public void Impossible_dates_are_refused()
    {
        Assert.Equal("business.invalid_date", Refused(Inbound(extra: ",\"expected_on\":\"2026-02-30\"")).Code);
        Assert.Equal("business.invalid_date", Refused(Outbound(extra: ",\"due_on\":\"2026-13-01\"")).Code);
    }

    [Fact]
    public void Two_steps_with_the_same_document_conflict_inside_one_plan()
    {
        var result = _h.Execute(Plan(Inbound(), Inbound()));
        Assert.False(result.Succeeded);
        Assert.Equal("business.duplicate_document", Assert.Single(result.Errors).Code);
        Assert.Equal(0, Value("SELECT COUNT(*) FROM inbound_orders WHERE document_number = 'NF-TESTE-001'"));
    }

    // ---- cancel_inbound_order -------------------------------------------------------------------------------

    [Fact]
    public void Cancel_marks_an_expected_order_cancelled()
    {
        var result = _h.Execute(Plan(Cancel("NF-FICT-000102")));
        Assert.True(result.Succeeded);
        Assert.Equal(1, Value("SELECT COUNT(*) FROM inbound_orders WHERE document_number = 'NF-FICT-000102' AND status = 'cancelled'"));
        var updated = Assert.Single(result.Diff!.Updated);
        Assert.Equal(("Pedido de entrada cancelado", "NF-FICT-000102"), (updated.Kind, updated.Code));
        Assert.Contains("Fornecedor desistiu", updated.Description);
        Assert.Equal(RiskLevel.Critical, result.Risk);
    }

    [Fact]
    public void Cancel_refuses_received_receiving_cancelled_and_unknown_orders()
    {
        var received = Refused(Cancel("NF-FICT-000101"));
        Assert.Equal(("document_number", "business.order_not_cancellable"), (received.Field, received.Code));
        Assert.Contains("totalmente recebido", received.Message);

        _h.Fixture.Exec("UPDATE inbound_orders SET status = 'receiving' WHERE document_number = 'NF-FICT-000102'");
        Assert.Contains("já teve mercadoria recebida", Refused(Cancel("NF-FICT-000102")).Message);

        _h.Fixture.Exec("UPDATE inbound_orders SET status = 'cancelled' WHERE document_number = 'NF-FICT-000102'");
        Assert.Contains("já está cancelado", Refused(Cancel("NF-FICT-000102")).Message);

        Assert.Equal("business.unknown_order", Refused(Cancel("NF-NAO-EXISTE")).Code);
    }

    [Fact]
    public void Cancel_does_not_reach_orders_of_another_warehouse()
    {
        AddRemoteWarehouseOrders();
        var error = Refused(Cancel("NF-REMOTA-1"));
        Assert.Equal("business.unknown_order", error.Code);
        Assert.Equal(1, Value("SELECT COUNT(*) FROM inbound_orders WHERE document_number = 'NF-REMOTA-1' AND status = 'expected'"));
    }

    [Fact]
    public void Cancel_an_order_created_in_the_same_plan_and_then_receiving_against_it_is_refused()
    {
        var result = _h.Execute(Plan(Inbound(), Cancel("NF-TESTE-001"),
            Receive("ABC-123", 1, "DOCA-01", ",\"inbound_order\":\"NF-TESTE-001\"")));
        Assert.False(result.Succeeded);
        Assert.Equal("business.order_closed", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void Cancel_needs_a_real_reason()
    {
        var result = _h.Execute(Plan(Step("cancel_inbound_order", "\"document_number\":\"NF-FICT-000102\",\"reason\":\"   \"")));
        Assert.False(result.Succeeded);
        Assert.Equal("business.invalid_reason", Assert.Single(result.Errors).Code);
    }

    // ---- create_outbound_order ------------------------------------------------------------------------------

    [Fact]
    public void Create_outbound_order_stores_it_open_with_its_lines()
    {
        var result = _h.Execute(Plan(Outbound(lines: "{\"sku\":\"ABC-123\",\"qty\":3},{\"sku\":\"FER-010\",\"qty\":1}", extra: ",\"due_on\":\"2026-12-15\"")));
        Assert.True(result.Succeeded);
        Assert.Equal(1, Value("SELECT COUNT(*) FROM outbound_orders WHERE order_number = 'PED-TESTE-001' AND status = 'open' " +
            "AND warehouse_id = 1 AND customer_id = 1 AND due_on = '2026-12-15'"));
        Assert.Equal(4, Value("SELECT SUM(l.ordered_qty) FROM outbound_order_lines l JOIN outbound_orders o ON o.id = l.outbound_order_id " +
            "WHERE o.order_number = 'PED-TESTE-001' AND l.picked_qty = 0"));
        var created = Assert.Single(result.Diff!.Created);
        Assert.Equal(("Pedido de saída", "PED-TESTE-001"), (created.Kind, created.Code));
        Assert.Contains("2 linhas, 4 unidades", created.Description);
    }

    [Fact]
    public void Outbound_order_number_is_unique_globally_without_leaking_the_warehouse()
    {
        AddRemoteWarehouseOrders();
        var local = Refused(Outbound(number: "PED-FICT-5001"));
        var remote = Refused(Outbound(number: "PED-REMOTO-1"));
        Assert.Equal("business.duplicate_order", local.Code);
        Assert.Equal("business.duplicate_order", remote.Code);
        Assert.DoesNotContain("armazém", remote.Message);
        Assert.Equal("Passo 1: o número de pedido PED-REMOTO-1 já está em uso.", remote.Message);
    }

    [Fact]
    public void Outbound_customer_must_exist_and_be_active()
    {
        Assert.Equal("business.unknown_customer", Refused(Outbound(customer: "Cliente Inexistente")).Code);
        Assert.True(_h.Execute(Plan(Outbound(customer: "loja fictícia CENTRO"))).Succeeded);
        _h.Fixture.Exec("UPDATE customers SET is_active = 0 WHERE id = 1");
        Assert.Equal("business.inactive_customer", Refused(Outbound(number: "PED-TESTE-002")).Code);
    }

    [Fact]
    public void Outbound_lines_must_name_known_distinct_skus()
    {
        Assert.Equal("business.unknown_sku", Refused(Outbound(lines: "{\"sku\":\"ZZZ-999\",\"qty\":1}")).Code);
        Assert.Equal("business.duplicate_sku", Refused(Outbound(lines: "{\"sku\":\"ABC-123\",\"qty\":1},{\"sku\":\"ABC-123\",\"qty\":1}")).Code);
    }

    // ---- schema, policy, simulation -------------------------------------------------------------------------

    [Theory]
    [InlineData("\"supplier\":\"X\",\"document_number\":\"nf-1\",\"lines\":[{\"sku\":\"ABC-123\",\"qty\":1}]", "número do documento")]
    [InlineData("\"supplier\":\"X\",\"document_number\":\"NF-OK-1\",\"lines\":[]", "ao menos 1 item")]
    [InlineData("\"supplier\":\"X\",\"document_number\":\"NF-OK-1\"", "falta a lista de itens")]
    [InlineData("\"supplier\":\"X\",\"document_number\":\"NF-OK-1\",\"lines\":[{\"sku\":\"ABC-123\",\"qty\":0}]", "na linha 1")]
    [InlineData("\"supplier\":\"X\",\"document_number\":\"NF-OK-1\",\"lines\":[{\"sku\":\"abc\",\"qty\":1}]", "na linha 1, o SKU")]
    [InlineData("\"supplier\":\"X\",\"document_number\":\"NF-OK-1\",\"lines\":[{\"sku\":\"ABC-123\",\"qty\":1,\"price\":2}]", "na linha 1")]
    [InlineData("\"supplier\":\"X\",\"document_number\":\"NF-OK-1\",\"expected_on\":\"amanhã\",\"lines\":[{\"sku\":\"ABC-123\",\"qty\":1}]", "AAAA-MM-DD")]
    public void Malformed_inbound_arguments_are_refused_in_portuguese_before_any_write(string args, string expected)
    {
        var result = _h.Execute(Plan(Step("create_inbound_order", args)));
        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Message.Contains(expected, StringComparison.Ordinal));
        Assert.All(result.Errors, e => Assert.StartsWith("Passo 1:", e.Message));
        Assert.Equal(0, Value("SELECT COUNT(*) FROM inbound_orders WHERE document_number = 'NF-OK-1'"));
    }

    [Fact]
    public void More_than_fifty_lines_are_refused()
    {
        var lines = string.Join(",", Enumerable.Range(0, 51).Select(i => $"{{\"sku\":\"ABC-{i:000}\",\"qty\":1}}"));
        var result = _h.Execute(Plan(Outbound(lines: lines)));
        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Message.Contains("no máximo 50", StringComparison.Ordinal));
    }

    [Fact]
    public void Hostile_text_is_stored_as_data_only()
    {
        var result = _h.Execute(Plan(Cancel("NF-FICT-000102", "x'); DROP TABLE inbound_orders;--")));
        Assert.True(result.Succeeded);
        Assert.Equal(1, Value("SELECT COUNT(*) FROM inbound_orders WHERE status = 'cancelled'"));
        var injected = Refused(Step("create_inbound_order",
            "\"supplier\":\"x' OR 1=1 --\",\"document_number\":\"NF-OK-1\",\"lines\":[{\"sku\":\"ABC-123\",\"qty\":1}]"));
        Assert.Equal("business.unknown_supplier", injected.Code);
    }

    [Fact]
    public void Order_operations_need_the_order_manage_permission()
    {
        _h.Fixture.Exec("DELETE FROM role_permissions WHERE permission = 'order.manage'");
        var denied = _h.Execute(Plan(Outbound()), _h.Fixture.SessionFor("op.joao"));
        Assert.False(denied.Succeeded);
        Assert.Equal("policy.denied", Assert.Single(denied.Errors).Code);
    }

    [Fact]
    public void Simulation_matches_execution_and_leaves_no_trace()
    {
        var plan = Plan(Inbound(), Outbound(), Cancel("NF-FICT-000102"));
        var before = Value("SELECT COUNT(*) FROM inbound_orders") + Value("SELECT COUNT(*) FROM outbound_orders");
        var simulated = _h.Simulate(plan);
        Assert.True(simulated.Succeeded);
        Assert.Equal(before, Value("SELECT COUNT(*) FROM inbound_orders") + Value("SELECT COUNT(*) FROM outbound_orders"));
        Assert.Equal(1, Value("SELECT COUNT(*) FROM inbound_orders WHERE document_number = 'NF-FICT-000102' AND status = 'expected'"));
        var executed = _h.Execute(plan);
        Assert.True(executed.Succeeded);
        Assert.True(simulated.Diff!.SameAs(executed.Diff!));
        Assert.Equal(RiskLevel.Critical, executed.Risk);
    }

    [Fact]
    public void Diff_json_round_trips_and_summary_lists_the_new_orders()
    {
        var result = _h.Execute(Plan(Inbound(), Cancel("NF-FICT-000102")));
        var back = PlanDiffJson.Deserialize(PlanDiffJson.Serialize(result.Diff!));
        Assert.True(back.SameAs(result.Diff!));
        var summary = back.ToSummary(result.Risk);
        Assert.Contains(summary.Created!, c => c.Kind == "Pedido de entrada" && c.Code == "NF-TESTE-001");
        Assert.Contains(summary.Created!, c => c.Kind == "Pedido de entrada cancelado");
    }

    // ---- reservations ---------------------------------------------------------------------------------------

    private static string Lines(params (string Sku, int Qty)[] lines) =>
        string.Join(",", lines.Select(l => $"{{\"sku\":\"{l.Sku}\",\"qty\":{l.Qty}}}"));

    private void NewOrder(string number, params (string Sku, int Qty)[] lines) =>
        Assert.True(_h.Execute(Plan(Outbound(number: number, lines: Lines(lines)))).Succeeded);

    private static string Reserve(string number, string? lines = null) =>
        Step("reserve_stock", $"\"order_number\":\"{number}\"" + (lines is null ? "" : $",\"lines\":[{lines}]"));

    private static string Release(string number, string? lines = null) =>
        Step("release_reservation", $"\"order_number\":\"{number}\"" + (lines is null ? "" : $",\"lines\":[{lines}]"));

    private static string PickTo(string number, string to = "DOCA-02", string? lines = null) =>
        Step("pick_items", $"\"order_number\":\"{number}\",\"to\":\"{to}\"" + (lines is null ? "" : $",\"lines\":[{lines}]"));

    private static string Ship(string number) => Step("ship_order", $"\"order_number\":\"{number}\"");

    private static string CancelOut(string number, string reason = "Cliente desistiu") =>
        Step("cancel_outbound_order", $"\"order_number\":\"{number}\",\"reason\":\"{reason}\"");

    private void Run(params string[] steps)
    {
        var result = _h.Execute(Plan(steps));
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    private string OrderStatus(string number) =>
        Text("SELECT status FROM outbound_orders WHERE order_number = $n", ("$n", number));

    private string Text(string sql, params (string, object)[] p)
    {
        using var c = _h.Fixture.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    /// <summary>Quantity held by the open reservation rows of an order line (by SKU), per state.</summary>
    private long Held(string number, string sku, string state) =>
        Value("SELECT COALESCE(SUM(r.quantity), 0) FROM order_reservations r JOIN outbound_order_lines l ON l.id = r.outbound_order_line_id " +
              "JOIN outbound_orders o ON o.id = l.outbound_order_id JOIN products p ON p.id = l.product_id " +
              "WHERE o.order_number = $n AND p.sku = $s AND r.state = $st", ("$n", number), ("$s", sku), ("$st", state));

    private long Reserved(string sku, string location) =>
        Value("SELECT COALESCE(SUM(b.reserved_qty), 0) FROM stock_balances b JOIN products p ON p.id = b.product_id " +
              "JOIN locations l ON l.id = b.location_id WHERE p.sku = $s AND l.code = $l", ("$s", sku), ("$l", location));

    /// <summary>Invariant: reserved_qty of every balance equals the quantity of its open rows (legacy seed rows aside).</summary>
    private void AssertReservationsConsistent(params long[] legacyBalanceIds)
    {
        var skip = legacyBalanceIds.Length == 0 ? "-1" : string.Join(",", legacyBalanceIds);
        Assert.Equal(0, Value(
            "SELECT COUNT(*) FROM stock_balances b WHERE b.id NOT IN (" + skip + ") AND b.reserved_qty <> " +
            "COALESCE((SELECT SUM(r.quantity) FROM order_reservations r WHERE r.balance_id = b.id AND r.state IN ('reserved', 'picked')), 0)"));
    }

    [Fact]
    public void Reserve_all_lines_allocates_fefo_across_lots()
    {
        NewOrder("PED-R-1", ("ALI-300", 15), ("ABC-124", 5));
        var result = _h.Execute(Plan(Reserve("PED-R-1")));
        Assert.True(result.Succeeded);

        // ALI-300: lot A (expires first) has 10 available (16 - 6 held by the legacy PED-FICT-5002 reservation), the rest comes from lot B.
        Assert.Equal(15, Held("PED-R-1", "ALI-300", "reserved"));
        Assert.Equal(16, Value("SELECT reserved_qty FROM stock_balances b JOIN lots t ON t.id = b.lot_id WHERE t.lot_code = 'L300-2611A'"));
        Assert.Equal(5, Value("SELECT reserved_qty FROM stock_balances b JOIN lots t ON t.id = b.lot_id WHERE t.lot_code = 'L300-2611B'"));
        Assert.Equal(5, Held("PED-R-1", "ABC-124", "reserved"));
        Assert.Equal(15, Reserved("ABC-124", "A-01-02"));
        Assert.Equal(21, Reserved("ALI-300", "C-01-01"));
        Assert.Equal("reserved", OrderStatus("PED-R-1"));
        Assert.Equal(2, result.Diff!.Updated.Count);
        Assert.All(result.Diff.Updated, u => Assert.Equal("Reserva de estoque", u.Kind));
        Assert.Contains("ALI-300: 10 em C-01-01 (lote L300-2611A), 5 em C-01-01 (lote L300-2611B)", result.Diff.Updated.Select(u => u.Description));
        Assert.Equal(0, Value("SELECT COUNT(*) FROM stock_movements WHERE kind = 'pick'")); // reserving moves nothing
    }

    [Fact]
    public void Fefo_prefers_the_earliest_expiry_over_the_lowest_location_code()
    {
        _h.Fixture.Exec("UPDATE lots SET expires_on = '2026-10-01' WHERE lot_code = 'L200-2703'");
        NewOrder("PED-R-2", ("LIM-200", 5));
        Run(Reserve("PED-R-2"));
        Assert.Equal(5, Reserved("LIM-200", "A-01-04"));
        Assert.Equal(0, Reserved("LIM-200", "A-01-03"));
    }

    [Fact]
    public void Dock_stock_is_ignored_unless_the_location_is_given()
    {
        NewOrder("PED-R-3", ("ABC-123", 50));
        var error = Refused(Reserve("PED-R-3"));
        Assert.Equal("business.insufficient_stock", error.Code);
        Assert.Contains("faltam 10 de ABC-123", error.Message);

        Run(Reserve("PED-R-3", "{\"sku\":\"ABC-123\",\"qty\":30,\"location\":\"DOCA-01\"}"));
        Assert.Equal(30, Reserved("ABC-123", "DOCA-01"));
        Assert.Equal(0, Reserved("ABC-123", "A-01-01"));
    }

    [Fact]
    public void Shortfall_lists_every_sku_and_reserves_nothing()
    {
        NewOrder("PED-R-4", ("ABC-123", 50), ("FER-011", 25), ("ABC-124", 5));
        var before = Value("SELECT COALESCE(SUM(reserved_qty), 0) FROM stock_balances");
        var error = Refused(Reserve("PED-R-4"));
        Assert.Equal("business.insufficient_stock", error.Code);
        Assert.Contains("faltam 10 de ABC-123, 5 de FER-011", error.Message);
        Assert.Contains("Nada foi reservado", error.Message);
        Assert.Equal(before, Value("SELECT COALESCE(SUM(reserved_qty), 0) FROM stock_balances"));
        Assert.Equal(0, Held("PED-R-4", "ABC-124", "reserved"));
        Assert.Equal("open", OrderStatus("PED-R-4"));
    }

    [Fact]
    public void Reservation_never_exceeds_the_ordered_quantity_and_can_be_done_in_parts()
    {
        NewOrder("PED-R-5", ("ABC-124", 5), ("EMB-500", 4));
        var over = Refused(Reserve("PED-R-5", "{\"sku\":\"ABC-124\",\"qty\":6}"));
        Assert.Equal("business.over_reservation", over.Code);
        Assert.Equal("business.sku_not_in_order", Refused(Reserve("PED-R-5", "{\"sku\":\"FER-010\",\"qty\":1}")).Code);
        Assert.Equal("business.duplicate_sku", Refused(Reserve("PED-R-5", "{\"sku\":\"ABC-124\",\"qty\":1},{\"sku\":\"ABC-124\",\"qty\":1}")).Code);

        Run(Reserve("PED-R-5", "{\"sku\":\"ABC-124\",\"qty\":2}"));
        Assert.Equal(2, Held("PED-R-5", "ABC-124", "reserved"));
        Assert.Equal("business.over_reservation", Refused(Reserve("PED-R-5", "{\"sku\":\"ABC-124\",\"qty\":4}")).Code);
        Run(Reserve("PED-R-5")); // the rest of every line
        Assert.Equal(5, Held("PED-R-5", "ABC-124", "reserved"));
        Assert.Equal(4, Held("PED-R-5", "EMB-500", "reserved"));
        Assert.Equal("business.nothing_to_reserve", Refused(Reserve("PED-R-5")).Code);
        AssertReservationsConsistent(Legacy);
    }

    // The legacy PED-FICT-5002 reservation (ALI-300 lot A, 6 units) has no rows: its balance is excluded from the invariant.
    private const long Legacy = 8;

    [Fact]
    public void Reserve_is_refused_for_unknown_other_warehouse_and_finished_orders()
    {
        AddRemoteWarehouseOrders();
        Assert.Equal("business.unknown_order", Refused(Reserve("PED-REMOTO-1")).Code);
        Assert.Equal("business.unknown_order", Refused(Reserve("PED-NAO-EXISTE")).Code);
        NewOrder("PED-R-6", ("ABC-124", 1));
        foreach (var status in new[] { "picking", "shipped", "cancelled" })
        {
            _h.Fixture.Exec("UPDATE outbound_orders SET status = $s WHERE order_number = 'PED-R-6'", ("$s", status));
            var error = Refused(Reserve("PED-R-6"));
            Assert.Equal("business.order_status", error.Code);
            Assert.Contains("PED-R-6", error.Message);
        }
    }

    [Fact]
    public void Stock_of_another_warehouse_is_never_reserved()
    {
        AddRemoteWarehouseOrders();
        _h.Fixture.Exec("INSERT INTO zones (id, warehouse_id, code, name) VALUES (9, 2, 'W', 'Zona remota')");
        _h.Fixture.Exec("INSERT INTO locations (zone_id, code, capacity) VALUES (9, 'W-01-01', 900)");
        _h.Fixture.Exec("INSERT INTO stock_balances (warehouse_id, product_id, location_id, quantity) " +
                        "SELECT 2, 2, id, 500 FROM locations WHERE code = 'W-01-01'");
        NewOrder("PED-R-7", ("ABC-124", 100));
        Assert.Contains("faltam 75 de ABC-124", Refused(Reserve("PED-R-7")).Message);
    }

    [Fact]
    public void Release_gives_part_or_all_back_and_splits_rows_without_changing_sums()
    {
        NewOrder("PED-L-1", ("ABC-124", 5), ("EMB-500", 4));
        Run(Reserve("PED-L-1"));
        var rowsBefore = Value("SELECT COUNT(*) FROM order_reservations");

        Run(Release("PED-L-1", "{\"sku\":\"ABC-124\",\"qty\":2}"));
        Assert.Equal(3, Held("PED-L-1", "ABC-124", "reserved"));
        Assert.Equal(13, Reserved("ABC-124", "A-01-02"));
        Assert.Equal(rowsBefore + 1, Value("SELECT COUNT(*) FROM order_reservations")); // original closed, remainder opened
        Assert.Equal("reserved", OrderStatus("PED-L-1"));

        Assert.Equal("business.over_release", Refused(Release("PED-L-1", "{\"sku\":\"ABC-124\",\"qty\":4}")).Code);
        Run(Release("PED-L-1"));
        Assert.Equal(0, Held("PED-L-1", "EMB-500", "reserved"));
        Assert.Equal(10, Reserved("ABC-124", "A-01-02")); // only the legacy PED-FICT-5001 hold remains
        Assert.Equal("open", OrderStatus("PED-L-1"));
        Assert.Equal("business.order_status", Refused(Release("PED-L-1")).Code);
        AssertReservationsConsistent(Legacy);
    }

    [Fact]
    public void Release_of_the_backfilled_seed_order_returns_it_to_open()
    {
        Run(Release("PED-FICT-5001"));
        Assert.Equal("open", OrderStatus("PED-FICT-5001"));
        Assert.Equal(0, Reserved("ABC-124", "A-01-02"));
        Assert.Equal(0, Reserved("FER-010", "B-01-01"));
    }

    [Fact]
    public void Pick_moves_reserved_stock_to_the_dock_and_keeps_it_reserved()
    {
        NewOrder("PED-P-1", ("ABC-124", 5), ("FER-010", 2));
        Run(Reserve("PED-P-1"));
        var result = _h.Execute(Plan(PickTo("PED-P-1")));
        Assert.True(result.Succeeded);

        Assert.Equal(30, _h.Qty("ABC-124", "A-01-02"));
        Assert.Equal(5, _h.Qty("ABC-124", "DOCA-02"));
        Assert.Equal(5, Reserved("ABC-124", "DOCA-02"));
        Assert.Equal(10, Reserved("ABC-124", "A-01-02")); // legacy hold untouched
        Assert.Equal(5, Held("PED-P-1", "ABC-124", "picked"));
        Assert.Equal(0, Held("PED-P-1", "ABC-124", "reserved"));
        Assert.Equal(2, Value("SELECT COUNT(*) FROM outbound_order_lines l JOIN outbound_orders o ON o.id = l.outbound_order_id " +
            "WHERE o.order_number = 'PED-P-1' AND l.picked_qty = l.ordered_qty"));
        Assert.Equal(2, Value("SELECT COUNT(*) FROM stock_movements WHERE kind = 'pick' AND quantity > 0 AND to_location_id = " +
            "(SELECT id FROM locations WHERE code = 'DOCA-02')"));
        Assert.Equal("picking", OrderStatus("PED-P-1"));
        Assert.Equal(2, result.Diff!.Moves.Count);
        Assert.Equal(2, result.Diff.Updated.Count(u => u.Kind == "Separação de pedido"));
        AssertReservationsConsistent(Legacy, 2, 3);
    }

    [Fact]
    public void Partial_pick_splits_the_row_and_keeps_per_line_sums()
    {
        NewOrder("PED-P-2", ("ABC-124", 5));
        Run(Reserve("PED-P-2"));
        Run(PickTo("PED-P-2", "DOCA-02", "{\"sku\":\"ABC-124\",\"qty\":2}"));
        Assert.Equal(2, Held("PED-P-2", "ABC-124", "picked"));
        Assert.Equal(3, Held("PED-P-2", "ABC-124", "reserved"));
        Assert.Equal(2, Value("SELECT picked_qty FROM outbound_order_lines l JOIN outbound_orders o ON o.id = l.outbound_order_id WHERE o.order_number = 'PED-P-2'"));
        Assert.Equal(5, Held("PED-P-2", "ABC-124", "picked") + Held("PED-P-2", "ABC-124", "reserved"));
        Assert.Equal("picking", OrderStatus("PED-P-2"));
        Assert.Equal("business.over_pick", Refused(PickTo("PED-P-2", "DOCA-02", "{\"sku\":\"ABC-124\",\"qty\":4}")).Code);

        Run(PickTo("PED-P-2", "DOCA-01"));
        Assert.Equal(5, Held("PED-P-2", "ABC-124", "picked"));
        Assert.Equal("business.nothing_to_pick", Refused(PickTo("PED-P-2")).Code);

        // Releasing is for reserved stock only; nothing is left to release.
        Assert.Equal("business.nothing_to_release", Refused(Release("PED-P-2")).Code);
        AssertReservationsConsistent(Legacy, 2);
    }

    [Fact]
    public void Pick_needs_an_active_dock_with_room()
    {
        NewOrder("PED-P-3", ("ABC-124", 5));
        Run(Reserve("PED-P-3"));
        Assert.Equal("business.not_dock", Refused(PickTo("PED-P-3", "A-02-01")).Code);
        Assert.Equal("business.unknown_location", Refused(PickTo("PED-P-3", "DOCA-09")).Code);
        _h.Fixture.Exec("UPDATE locations SET capacity = 3 WHERE code = 'DOCA-02'");
        Assert.Equal("business.capacity", Refused(PickTo("PED-P-3")).Code);
        _h.Fixture.Exec("UPDATE locations SET is_active = 0 WHERE code = 'DOCA-02'");
        Assert.Equal("business.unknown_location", Refused(PickTo("PED-P-3")).Code);
        Assert.Equal(5, Held("PED-P-3", "ABC-124", "reserved"));
    }

    [Fact]
    public void Pick_of_an_open_order_is_refused()
    {
        NewOrder("PED-P-4", ("ABC-124", 5));
        Assert.Equal("business.order_status", Refused(PickTo("PED-P-4")).Code);
    }

    [Fact]
    public void Ship_is_refused_until_every_line_is_fully_picked_then_takes_stock_out()
    {
        NewOrder("PED-S-1", ("ABC-124", 5), ("FER-010", 2));
        Assert.Equal("business.order_status", Refused(Ship("PED-S-1")).Code); // open
        Run(Reserve("PED-S-1"));
        Assert.Equal("business.order_status", Refused(Ship("PED-S-1")).Code); // reserved, not picked
        Run(PickTo("PED-S-1", "DOCA-02", "{\"sku\":\"ABC-124\",\"qty\":3}"));
        var partial = Refused(Ship("PED-S-1"));
        Assert.Equal("business.not_fully_picked", partial.Code);
        Assert.Contains("faltam 2 de ABC-124, 2 de FER-010", partial.Message);
        Run(PickTo("PED-S-1"));

        var result = _h.Execute(Plan(Ship("PED-S-1")));
        Assert.True(result.Succeeded);
        Assert.Equal(RiskLevel.Critical, result.Risk);
        Assert.Equal(0, _h.Qty("ABC-124", "DOCA-02"));
        Assert.Equal(0, Reserved("ABC-124", "DOCA-02"));
        Assert.Equal(0, _h.Qty("FER-010", "DOCA-02"));
        Assert.Equal(5, Held("PED-S-1", "ABC-124", "shipped"));
        Assert.Equal("shipped", OrderStatus("PED-S-1"));
        Assert.Equal(-5, Value("SELECT SUM(quantity) FROM stock_movements m JOIN products p ON p.id = m.product_id " +
            "WHERE m.kind = 'ship' AND p.sku = 'ABC-124' AND m.from_location_id = (SELECT id FROM locations WHERE code = 'DOCA-02') AND m.to_location_id IS NULL"));
        Assert.Contains(result.Diff!.Updated, u => u.Kind == "Pedido expedido" && u.Code == "PED-S-1");
        Assert.Equal("business.order_status", Refused(Ship("PED-S-1")).Code);
        AssertReservationsConsistent(Legacy, 2);
    }

    [Fact]
    public void Cancel_from_open_releases_nothing_and_closes_the_order()
    {
        NewOrder("PED-C-1", ("ABC-124", 5));
        var result = _h.Execute(Plan(CancelOut("PED-C-1")));
        Assert.True(result.Succeeded);
        Assert.Equal(RiskLevel.Critical, result.Risk);
        Assert.Equal("cancelled", OrderStatus("PED-C-1"));
        Assert.Equal("business.order_status", Refused(CancelOut("PED-C-1")).Code);
        Assert.Equal("business.order_status", Refused(Reserve("PED-C-1")).Code);
    }

    [Fact]
    public void Cancel_from_reserved_releases_the_reservations()
    {
        NewOrder("PED-C-2", ("ABC-124", 5));
        Run(Reserve("PED-C-2"));
        var result = _h.Execute(Plan(CancelOut("PED-C-2")));
        Assert.True(result.Succeeded);
        Assert.Equal(10, Reserved("ABC-124", "A-01-02"));
        Assert.Equal(0, Held("PED-C-2", "ABC-124", "reserved"));
        Assert.Contains("5 unidades de reserva liberadas", Assert.Single(result.Diff!.Updated).Description);
        AssertReservationsConsistent(Legacy);
    }

    [Fact]
    public void Cancel_from_picking_keeps_the_picked_stock_on_the_dock_without_reservation()
    {
        NewOrder("PED-C-3", ("ABC-124", 5));
        Run(Reserve("PED-C-3"));
        Run(PickTo("PED-C-3", "DOCA-02", "{\"sku\":\"ABC-124\",\"qty\":2}"));
        var result = _h.Execute(Plan(CancelOut("PED-C-3")));
        Assert.True(result.Succeeded);
        Assert.Equal(2, _h.Qty("ABC-124", "DOCA-02"));
        Assert.Equal(0, Reserved("ABC-124", "DOCA-02"));
        Assert.Equal(10, Reserved("ABC-124", "A-01-02"));
        Assert.Contains("continua em DOCA-02", Assert.Single(result.Diff!.Updated).Description);
        Assert.Equal("cancelled", OrderStatus("PED-C-3"));
        AssertReservationsConsistent(Legacy);
    }

    [Fact]
    public void Cancel_is_refused_for_shipped_orders_blank_reasons_and_other_warehouses()
    {
        NewOrder("PED-C-4", ("ABC-124", 1));
        Run(Reserve("PED-C-4"));
        Run(PickTo("PED-C-4"));
        Run(Ship("PED-C-4"));
        Assert.Equal("business.order_status", Refused(CancelOut("PED-C-4")).Code);
        NewOrder("PED-C-5", ("ABC-124", 1));
        Assert.Equal("business.invalid_reason", Refused(Step("cancel_outbound_order", "\"order_number\":\"PED-C-5\",\"reason\":\"  \"")).Code);
        AddRemoteWarehouseOrders();
        Assert.Equal("business.unknown_order", Refused(CancelOut("PED-REMOTO-1")).Code);
    }

    [Fact]
    public void Cancel_of_the_backfilled_seed_order_frees_its_reserved_stock()
    {
        Run(CancelOut("PED-FICT-5001"));
        Assert.Equal(0, Reserved("ABC-124", "A-01-02"));
        Assert.Equal(0, Reserved("FER-010", "B-01-01"));
        Assert.Equal("cancelled", OrderStatus("PED-FICT-5001"));
    }

    [Fact]
    public void Reservation_operations_need_their_own_permissions()
    {
        NewOrder("PED-X-1", ("ABC-124", 1));
        Run(Reserve("PED-X-1"));
        Run(PickTo("PED-X-1"));
        var denied = _h.Execute(Plan(Ship("PED-X-1")), _h.Operator);
        Assert.False(denied.Succeeded);
        Assert.Equal("policy.denied", Assert.Single(denied.Errors).Code);
        Assert.Equal("picking", OrderStatus("PED-X-1"));
    }

    [Theory]
    [InlineData("reserve_stock", "\"order_number\":\"ped-1\"", "número do pedido")]
    [InlineData("reserve_stock", "\"order_number\":\"PED-1\",\"lines\":[]", "ao menos 1 item")]
    [InlineData("reserve_stock", "\"order_number\":\"PED-1\",\"lines\":[{\"sku\":\"ABC-124\"}]", "na linha 1")]
    [InlineData("reserve_stock", "\"order_number\":\"PED-1\",\"lines\":[{\"sku\":\"ABC-124\",\"qty\":1,\"location\":\"x\"}]", "na linha 1, o endereço")]
    [InlineData("pick_items", "\"order_number\":\"PED-1\"", "falta o destino")]
    [InlineData("ship_order", "\"order_number\":\"PED-1\",\"force\":true", "force")]
    [InlineData("cancel_outbound_order", "\"order_number\":\"PED-1\"", "falta o motivo")]
    public void Malformed_reservation_arguments_are_refused_before_any_write(string op, string args, string expected)
    {
        var result = _h.Execute(Plan(Step(op, args)));
        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Message.Contains(expected, StringComparison.Ordinal));
        Assert.All(result.Errors, e => Assert.StartsWith("Passo 1:", e.Message));
    }

    [Fact]
    public void Chained_plan_creates_reserves_and_picks_with_references_and_simulation_matches()
    {
        var plan = Plan(
            Step("create_outbound_order", "\"customer\":\"Loja Fictícia Centro\",\"order_number\":\"PED-CH-1\",\"lines\":[{\"sku\":\"ABC-124\",\"qty\":5},{\"sku\":\"ALI-300\",\"qty\":12}]", "o"),
            Step("reserve_stock", "\"order_number\":\"$o\""),
            Step("pick_items", "\"order_number\":\"$o\",\"to\":\"DOCA-02\""));
        var countsBefore = Value("SELECT COUNT(*) FROM order_reservations") + Value("SELECT COUNT(*) FROM outbound_orders");
        var simulated = _h.Simulate(plan);
        Assert.True(simulated.Succeeded, string.Join("; ", simulated.Errors.Select(e => e.Message)));
        Assert.Equal(countsBefore, Value("SELECT COUNT(*) FROM order_reservations") + Value("SELECT COUNT(*) FROM outbound_orders"));

        var executed = _h.Execute(plan);
        Assert.True(executed.Succeeded);
        Assert.True(simulated.Diff!.SameAs(executed.Diff!));
        Assert.Equal("picking", OrderStatus("PED-CH-1"));
        Assert.Equal(5, Held("PED-CH-1", "ABC-124", "picked"));
        Assert.Equal(12, Held("PED-CH-1", "ALI-300", "picked"));
        Assert.Equal(12, _h.Qty("ALI-300", "DOCA-02"));
        AssertReservationsConsistent(Legacy, 2, 8, 9);
        var back = PlanDiffJson.Deserialize(PlanDiffJson.Serialize(executed.Diff!));
        Assert.True(back.SameAs(executed.Diff!));
    }

    [Fact]
    public void Ship_runs_as_a_critical_plan_approved_by_another_supervisor()
    {
        var f = _h.Fixture;
        var engine = new PlanEngine(f.Db.Database, f.Policy, f.Audit, f.Time);
        var maria = _h.Supervisor;
        var joao = f.SessionFor("op.joao");

        var prepare = engine.Submit(joao, Json(
            Step("create_outbound_order", "\"customer\":\"Loja Fictícia Centro\",\"order_number\":\"PED-CH-2\",\"lines\":[{\"sku\":\"ABC-124\",\"qty\":4}]", "o"),
            Step("reserve_stock", "\"order_number\":\"$o\""),
            Step("pick_items", "\"order_number\":\"$o\",\"to\":\"DOCA-02\"")), "user");
        Assert.Equal("executed", prepare.Status);
        Assert.Equal("picking", OrderStatus("PED-CH-2"));

        var submitted = engine.Submit(maria, Json(Ship("PED-CH-2")), "user");
        Assert.Equal("pending_approval", submitted.Status);
        Assert.Equal("picking", OrderStatus("PED-CH-2"));
        Assert.Equal(4, _h.Qty("ABC-124", "DOCA-02"));

        var id = f.Scalar("SELECT COALESCE(MAX(id), 0) + 1 FROM users");
        f.Exec("INSERT INTO users (id, username, display_name, password_hash, role_id, warehouse_id) " +
               "VALUES ($id, 'sup.outro', 'sup.outro', 'no-login-in-tests', 2, 1)", ("$id", id));
        engine.Approve(f.SessionFor("sup.outro"), submitted.PlanId!.Value);

        Assert.Equal("shipped", OrderStatus("PED-CH-2"));
        Assert.Equal(0, _h.Qty("ABC-124", "DOCA-02"));
        Assert.Equal("executed", Text("SELECT status FROM plans WHERE id = $id", ("$id", submitted.PlanId!.Value)));
        Assert.Equal(1, Value("SELECT COUNT(*) FROM stock_movements WHERE kind = 'ship' AND plan_id = $p", ("$p", submitted.PlanId!.Value)));
    }

    private static string Json(params string[] steps) =>
        "{\"dsl_version\":\"1.0\",\"intent\":\"teste\",\"skill\":\"armazenagem\",\"steps\":[" + string.Join(",", steps) + "]}";
}
