using System.Security.Cryptography;
using erp_inteligente.Core.Engine;
using static Nexus.Tests.Core.Engine.EngineHarness;

namespace Nexus.Tests.Core.Engine;

public sealed class BusinessRuleTests : IDisposable
{
    private readonly EngineHarness _h = new();

    public void Dispose() => _h.Dispose();

    // ---- receive_goods ----------------------------------------------------------------------------------

    [Fact]
    public void Receive_into_dock_adds_balance_and_movement()
    {
        var result = _h.Execute(Plan(Receive("ABC-123", 10, "DOCA-01")), planId: 1);
        Assert.True(result.Succeeded);
        Assert.Equal(50, _h.Qty("ABC-123", "DOCA-01"));
        Assert.Equal(1, _h.Fixture.Scalar(
            "SELECT COUNT(*) FROM stock_movements WHERE kind = 'receive' AND quantity = 10 AND plan_id = 1 AND user_id = $u " +
            "AND from_location_id IS NULL AND to_location_id = 1 AND created_at = '2026-11-07T12:00:00Z'",
            ("$u", _h.Supervisor.UserId)));
    }

    [Fact]
    public void Receive_outside_the_dock_is_refused()
    {
        var message = _h.ExecuteRefused(Plan(Receive("ABC-123", 10, "A-01-01")));
        Assert.StartsWith("Passo 1: ", message);
        Assert.Contains("doca", message);
        Assert.Equal(40, _h.Qty("ABC-123", "A-01-01"));
    }

    [Fact]
    public void Receive_over_capacity_is_refused()
    {
        Assert.Contains("não comporta", _h.ExecuteRefused(Plan(Receive("ABC-123", 461, "DOCA-01"))));
        Assert.True(_h.Execute(Plan(Receive("ABC-123", 460, "DOCA-01"))).Succeeded);
    }

    [Fact]
    public void Receive_unknown_sku_is_refused() =>
        Assert.Contains("ZZZ-999", _h.ExecuteRefused(Plan(Receive("ZZZ-999", 1, "DOCA-01"))));

    [Fact]
    public void Expiry_tracked_product_requires_lot_and_expiry()
    {
        Assert.Contains("controla validade", _h.ExecuteRefused(Plan(Receive("LIM-200", 5, "DOCA-01"))));
        Assert.Contains("controla validade",
            _h.ExecuteRefused(Plan(Receive("LIM-200", 5, "DOCA-01", ",\"lot\":\"L200-2611\""))));
    }

    [Fact]
    public void Receive_creates_a_new_lot_with_expiry()
    {
        var plan = Plan(Receive("LIM-200", 5, "DOCA-01", ",\"lot\":\"L200-NOVO\",\"expires_on\":\"2027-05-01\""));
        Assert.True(_h.Execute(plan).Succeeded);
        Assert.Equal(1, _h.Fixture.Scalar("SELECT COUNT(*) FROM lots WHERE lot_code = 'L200-NOVO' AND expires_on = '2027-05-01'"));
        Assert.Equal(5, _h.Qty("LIM-200", "DOCA-01", "L200-NOVO"));
    }

    [Fact]
    public void Existing_lot_with_another_expiry_is_refused()
    {
        var plan = Plan(Receive("LIM-200", 5, "DOCA-01", ",\"lot\":\"L200-2611\",\"expires_on\":\"2030-01-01\""));
        Assert.Contains("outra validade", _h.ExecuteRefused(plan));
        var ok = Plan(Receive("LIM-200", 5, "DOCA-01", ",\"lot\":\"L200-2611\",\"expires_on\":\"2026-11-30\""));
        Assert.True(_h.Execute(ok).Succeeded);
        Assert.Equal(5, _h.Qty("LIM-200", "DOCA-01", "L200-2611"));
    }

    [Fact]
    public void Inbound_order_line_and_status_are_updated()
    {
        const string order = ",\"inbound_order\":\"NF-FICT-000102\"";
        var partial = Plan(Receive("ALI-300", 20, "DOCA-01", ",\"lot\":\"L300-X\",\"expires_on\":\"2026-12-20\"" + order));
        Assert.True(_h.Execute(partial).Succeeded);
        Assert.Equal(20, _h.Fixture.Scalar("SELECT received_qty FROM inbound_order_lines WHERE inbound_order_id = 2 AND product_id = 7"));
        Assert.Equal(1, _h.Fixture.Scalar("SELECT COUNT(*) FROM inbound_orders WHERE id = 2 AND status = 'receiving'"));

        var rest = Plan(
            Receive("ALI-300", 28, "DOCA-01", ",\"lot\":\"L300-X\",\"expires_on\":\"2026-12-20\"" + order),
            Receive("ALI-301", 20, "DOCA-01", ",\"lot\":\"L301-X\",\"expires_on\":\"2026-12-20\"" + order));
        Assert.True(_h.Execute(rest).Succeeded);
        Assert.Equal(1, _h.Fixture.Scalar("SELECT COUNT(*) FROM inbound_orders WHERE id = 2 AND status = 'received'"));
    }

    [Fact]
    public void Over_receipt_and_closed_orders_are_refused()
    {
        const string lot = ",\"lot\":\"L300-X\",\"expires_on\":\"2026-12-20\"";
        Assert.Contains("não é possível receber",
            _h.ExecuteRefused(Plan(Receive("ALI-300", 49, "DOCA-01", lot + ",\"inbound_order\":\"NF-FICT-000102\""))));
        Assert.Contains("totalmente recebido",
            _h.ExecuteRefused(Plan(Receive("ABC-123", 1, "DOCA-01", ",\"inbound_order\":\"NF-FICT-000101\""))));
        Assert.Contains("não prevê",
            _h.ExecuteRefused(Plan(Receive("ABC-123", 1, "DOCA-01", ",\"inbound_order\":\"NF-FICT-000102\""))));
        Assert.Contains("não existe", _h.ExecuteRefused(Plan(Receive("ABC-123", 1, "DOCA-01", ",\"inbound_order\":\"NF-NADA\""))));
    }

    // ---- move_stock -------------------------------------------------------------------------------------

    [Fact]
    public void Move_updates_both_balances_and_writes_a_movement()
    {
        Assert.True(_h.Execute(Plan(Move("ABC-123", 40, "DOCA-01", "A-02-01")), planId: 1).Succeeded);
        Assert.Equal(0, _h.Qty("ABC-123", "DOCA-01"));
        Assert.Equal(40, _h.Qty("ABC-123", "A-02-01"));
        Assert.Equal(1, _h.Fixture.Scalar(
            "SELECT COUNT(*) FROM stock_movements WHERE kind = 'move' AND quantity = 40 AND plan_id = 1 AND user_id = $u " +
            "AND from_location_id = 1 AND to_location_id = 7", ("$u", _h.Supervisor.UserId)));
    }

    [Fact]
    public void Move_respects_reserved_quantity()
    {
        var message = _h.ExecuteRefused(Plan(Move("ABC-124", 30, "A-01-02", "A-02-02")));
        Assert.Contains("insuficiente", message);
        Assert.Equal(35, _h.Qty("ABC-124", "A-01-02"));
        Assert.True(_h.Execute(Plan(Move("ABC-124", 25, "A-01-02", "A-02-02"))).Succeeded);
    }

    [Fact]
    public void Move_over_destination_capacity_is_refused()
    {
        // A-01-02 already holds 35 of its 60.
        Assert.Contains("não comporta", _h.ExecuteRefused(Plan(Move("ABC-123", 40, "A-01-01", "A-01-02"))));
    }

    [Fact]
    public void Move_to_the_same_location_is_refused() =>
        Assert.Contains("mesmo endereço", _h.ExecuteRefused(Plan(Move("ABC-123", 1, "DOCA-01", "DOCA-01"))));

    [Fact]
    public void Move_without_stock_is_refused() =>
        Assert.Contains("não há saldo", _h.ExecuteRefused(Plan(Move("ABC-123", 1, "B-01-01", "B-01-02"))));

    [Fact]
    public void Move_with_several_lots_requires_the_lot()
    {
        Assert.Contains("mais de um lote", _h.ExecuteRefused(Plan(Move("ALI-300", 5, "C-01-01", "C-01-02"))));
        Assert.True(_h.Execute(Plan(Move("ALI-300", 5, "C-01-01", "C-01-02", ",\"lot\":\"L300-2611B\""))).Succeeded);
        Assert.Equal(15, _h.Qty("ALI-300", "C-01-01", "L300-2611B"));
        Assert.Equal(5, _h.Qty("ALI-300", "C-01-02", "L300-2611B"));
    }

    // ---- adjust_stock -----------------------------------------------------------------------------------

    [Fact]
    public void Seed_plan_two_adjustment_works_and_keeps_the_reason()
    {
        var plan = Plan("{\"op\":\"adjust_stock\",\"args\":{\"sku\":\"ALI-300\",\"lot\":\"L300-2611A\",\"qty\":-1," +
                        "\"location\":\"C-01-01\",\"reason\":\"Avaria: embalagem violada\"}}");
        var result = _h.Execute(plan, planId: 2);
        Assert.True(result.Succeeded);
        Assert.Equal(RiskLevel.Critical, result.Risk);
        Assert.Equal(15, _h.Qty("ALI-300", "C-01-01", "L300-2611A"));
        Assert.Equal(1, _h.Fixture.Scalar(
            "SELECT COUNT(*) FROM stock_movements WHERE kind = 'adjust' AND quantity = -1 AND reason = 'Avaria: embalagem violada' " +
            "AND from_location_id = 15 AND to_location_id IS NULL AND plan_id = 2"));
    }

    [Fact]
    public void Adjust_never_goes_below_reserved()
    {
        // L300-2611A holds 16 with 6 reserved.
        Assert.Contains("reservado", _h.ExecuteRefused(Plan(Adjust("ALI-300", -11, "C-01-01", ",\"lot\":\"L300-2611A\""))));
        Assert.True(_h.Execute(Plan(Adjust("ALI-300", -10, "C-01-01", ",\"lot\":\"L300-2611A\""))).Succeeded);
        Assert.Contains("negativo", _h.ExecuteRefused(Plan(Adjust("ABC-123", -41, "A-01-01"))));
    }

    [Fact]
    public void Positive_adjustment_adds_stock_and_records_the_destination()
    {
        Assert.True(_h.Execute(Plan(Adjust("ABC-123", 5, "A-01-01")), planId: 1).Succeeded);
        Assert.Equal(45, _h.Qty("ABC-123", "A-01-01"));
        Assert.Equal(1, _h.Fixture.Scalar(
            "SELECT COUNT(*) FROM stock_movements WHERE kind = 'adjust' AND quantity = 5 AND to_location_id = 3 AND from_location_id IS NULL"));
    }

    [Fact]
    public void Positive_adjustment_without_any_row_creates_a_null_lot_row()
    {
        Assert.True(_h.Execute(Plan(Adjust("EMB-500", 3, "A-01-03"))).Succeeded);
        Assert.Equal(1, _h.Fixture.Scalar(
            "SELECT COUNT(*) FROM stock_balances WHERE location_id = 5 AND lot_id IS NULL AND quantity = 3 AND product_id = 9"));
        // A second adjustment reuses the NULL-lot row instead of creating a duplicate.
        Assert.True(_h.Execute(Plan(Adjust("EMB-500", 2, "A-01-03"))).Succeeded);
        Assert.Equal(1, _h.Fixture.Scalar("SELECT COUNT(*) FROM stock_balances WHERE location_id = 5 AND product_id = 9"));
        Assert.Equal(5, _h.Qty("EMB-500", "A-01-03"));
    }

    [Fact]
    public void Adjust_over_capacity_and_without_lot_choice_are_refused()
    {
        Assert.Contains("não comporta", _h.ExecuteRefused(Plan(Adjust("ABC-123", 21, "A-01-01"))));
        Assert.Contains("mais de um lote", _h.ExecuteRefused(Plan(Adjust("ALI-300", -1, "C-01-01"))));
    }

    // ---- create_product / create_location ---------------------------------------------------------------

    [Fact]
    public void Create_product_stores_an_encrypted_unit_cost_bound_to_its_id()
    {
        var plan = Plan("{\"op\":\"create_product\",\"args\":{\"sku\":\"NEW-001\",\"name\":\"Produto novo\",\"unit\":\"un\"," +
                       "\"unit_cost\":12.5,\"weight_kg\":1.5,\"tracks_expiry\":true}}");
        var result = _h.Execute(plan);
        Assert.True(result.Succeeded);
        var id = _h.Fixture.Scalar("SELECT id FROM products WHERE sku = 'NEW-001' AND tracks_expiry = 1 AND weight_kg = 1.5");

        byte[] blob;
        using (var connection = _h.Fixture.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT unit_cost_enc FROM products WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            blob = (byte[])command.ExecuteScalar()!;
        }
        using var cipher = _h.Fixture.Db.Database.CreateColumnCipher();
        Assert.Equal("12.5", cipher.Decrypt(blob, "products", "unit_cost_enc", id));
        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt(blob, "products", "unit_cost_enc", id + 1));
    }

    [Fact]
    public void Create_product_without_cost_leaves_it_null_and_duplicates_are_refused()
    {
        var plan = Plan("{\"op\":\"create_product\",\"args\":{\"sku\":\"NEW-002\",\"name\":\"Sem custo\",\"unit\":\"un\"}}");
        Assert.True(_h.Execute(plan).Succeeded);
        Assert.Equal(1, _h.Fixture.Scalar("SELECT COUNT(*) FROM products WHERE sku = 'NEW-002' AND unit_cost_enc IS NULL"));
        Assert.Contains("já existe", _h.ExecuteRefused(plan));
    }

    [Fact]
    public void Create_location_in_a_zone_and_duplicate_code()
    {
        var plan = Plan("{\"op\":\"create_location\",\"args\":{\"code\":\"A-03-01\",\"zone\":\"A\",\"capacity\":80}}");
        Assert.True(_h.Execute(plan).Succeeded);
        Assert.Equal(1, _h.Fixture.Scalar("SELECT COUNT(*) FROM locations WHERE code = 'A-03-01' AND capacity = 80 AND zone_id = 2"));
        Assert.Contains("já está em uso", _h.ExecuteRefused(plan));
    }

    [Fact]
    public void Create_location_in_a_zone_of_another_warehouse_is_refused()
    {
        _h.Fixture.Exec("INSERT INTO warehouses (id, code, name) VALUES (2, 'CD2', 'Outro armazém')");
        _h.Fixture.Exec("INSERT INTO zones (id, warehouse_id, code, name) VALUES (9, 2, 'Z', 'Zona remota')");
        var plan = Plan("{\"op\":\"create_location\",\"args\":{\"code\":\"Z-01-01\",\"zone\":\"Z\",\"capacity\":10}}");
        Assert.Contains("não existe neste armazém", _h.ExecuteRefused(plan));
    }

    [Fact]
    public void Reference_from_create_location_can_be_used_by_a_later_step()
    {
        var plan = Plan(
            "{\"op\":\"create_location\",\"args\":{\"code\":\"A-03-02\",\"zone\":\"A\",\"capacity\":50},\"as\":\"loc\"}",
            "{\"op\":\"move_stock\",\"args\":{\"sku\":\"ABC-123\",\"qty\":10,\"from\":\"DOCA-01\",\"to\":\"$loc\"}}");
        var result = _h.Execute(plan);
        Assert.True(result.Succeeded);
        Assert.Equal(10, _h.Qty("ABC-123", "A-03-02"));
        Assert.Equal(["A-03-02"], result.Diff!.Created.Select(c => c.Code).ToArray());
    }

    // ---- isolation --------------------------------------------------------------------------------------

    [Fact]
    public void Other_warehouse_locations_look_like_unknown_ones()
    {
        _h.Fixture.Exec("INSERT INTO warehouses (id, code, name) VALUES (2, 'CD2', 'Outro armazém')");
        _h.Fixture.Exec("INSERT INTO zones (id, warehouse_id, code, name) VALUES (9, 2, 'DOCA', 'Doca remota')");
        _h.Fixture.Exec("INSERT INTO locations (zone_id, code, capacity) VALUES (9, 'DOCA-09', 100)");
        var remote = _h.ExecuteRefused(Plan(Move("ABC-123", 1, "DOCA-09", "A-01-01")));
        var unknown = _h.ExecuteRefused(Plan(Move("ABC-123", 1, "DOCA-08", "A-01-01")));
        Assert.Equal("Passo 1: o endereço DOCA-09 não existe neste armazém.", remote);
        Assert.Equal(remote.Replace("DOCA-09", "DOCA-08"), unknown);
        Assert.Contains("não existe neste armazém", _h.ExecuteRefused(Plan(Receive("ABC-123", 1, "DOCA-09"))));
        Assert.Contains("não existe neste armazém", _h.ExecuteRefused(Plan(Adjust("ABC-123", 1, "DOCA-09"))));
    }
}
