using System.Security.Cryptography;
using erp_inteligente.Core.Engine;
using erp_inteligente.Models;
using static Nexus.Tests.Core.Engine.EngineHarness;

namespace Nexus.Tests.Core.Engine;

/// <summary>Delivery 1 of the full catalog: zones, location ranges, location/product updates, suppliers and customers.</summary>
public sealed class CatalogOperationTests : IDisposable
{
    private readonly EngineHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static string Step(string op, string args, string? name = null) =>
        $"{{\"op\":\"{op}\",\"args\":{{{args}}}" + (name is null ? "" : $",\"as\":\"{name}\"") + "}";

    private static string Zone(string code = "Q", string name = "Quarentena", string? name_ = null) =>
        Step("create_zone", $"\"code\":\"{code}\",\"name\":\"{name}\"", name_);

    private static string Range(string zone, int aisle, int from, int to, int capacity = 60, string? name = null) =>
        Step("create_location_range",
            $"\"zone\":\"{zone}\",\"aisle\":{aisle},\"level_from\":{from},\"level_to\":{to},\"capacity\":{capacity}", name);

    private PlanError Refused(PlanDocument plan)
    {
        var result = _h.Execute(plan);
        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Errors);
        Assert.StartsWith("Passo ", error.Message);
        return error;
    }

    private PlanError Refused(params string[] steps) => Refused(Plan(steps));

    private void AddRemoteWarehouse()
    {
        _h.Fixture.Exec("INSERT INTO warehouses (id, code, name) VALUES (2, 'CD2', 'Outro armazém')");
        _h.Fixture.Exec("INSERT INTO zones (id, warehouse_id, code, name) VALUES (9, 2, 'W', 'Zona remota')");
        _h.Fixture.Exec("INSERT INTO locations (zone_id, code, capacity) VALUES (9, 'W-01-01', 50)");
    }

    private long Value(string sql, params (string, object)[] p) => _h.Fixture.Scalar(sql, p);

    // ---- create_zone ----------------------------------------------------------------------------------------

    [Fact]
    public void Create_zone_stores_it_active_in_the_session_warehouse_and_reports_it()
    {
        var result = _h.Execute(Plan(Zone()));
        Assert.True(result.Succeeded);
        Assert.Equal(1, Value("SELECT COUNT(*) FROM zones WHERE code = 'Q' AND name = 'Quarentena' AND warehouse_id = 1 AND is_active = 1"));
        var created = Assert.Single(result.Diff!.Created);
        Assert.Equal(("Zona", "Q"), (created.Kind, created.Code));
        Assert.Contains("criada", created.Description);
    }

    [Fact]
    public void Create_zone_returns_its_code_for_later_steps()
    {
        var result = _h.Execute(Plan(Zone(name_: "z"), Range("$z", 1, 1, 2)));
        Assert.True(result.Succeeded);
        Assert.Equal(2, Value("SELECT COUNT(*) FROM locations WHERE code IN ('Q-01-01','Q-01-02')"));
    }

    [Fact]
    public void Duplicate_zone_is_refused_but_the_same_code_in_another_warehouse_is_not_a_conflict()
    {
        var error = Refused(Zone("A", "Outra"));
        Assert.Equal(("code", "business.duplicate_zone"), (error.Field, error.Code));
        Assert.Equal("Passo 1: a zona A já existe neste armazém.", error.Message);

        AddRemoteWarehouse();
        _h.Fixture.Exec("INSERT INTO zones (warehouse_id, code, name) VALUES (2, 'Q', 'Quarentena remota')");
        Assert.True(_h.Execute(Plan(Zone())).Succeeded);
        Assert.Equal(2, Value("SELECT COUNT(*) FROM zones WHERE code = 'Q'"));
    }

    [Fact]
    public void Blank_zone_name_is_refused()
    {
        var error = Refused(Zone("Q", "   "));
        Assert.Equal(("name", "business.invalid_name"), (error.Field, error.Code));
    }

    // ---- update_zone ----------------------------------------------------------------------------------------

    [Fact]
    public void Update_zone_renames_and_deactivates_an_empty_zone_and_describes_it()
    {
        var result = _h.Execute(Plan(
            Zone(),
            Step("update_zone", "\"code\":\"Q\",\"name\":\"Quarentena fria\",\"is_active\":false")));
        Assert.True(result.Succeeded);
        Assert.Equal(1, Value("SELECT COUNT(*) FROM zones WHERE code = 'Q' AND name = 'Quarentena fria' AND is_active = 0"));
        var updated = Assert.Single(result.Diff!.Updated);
        Assert.Equal(("Zona atualizada", "Q"), (updated.Kind, updated.Code));
        Assert.Equal("nome Quarentena → Quarentena fria; situação ativa → desativada", updated.Description);

        var summary = result.Diff.ToSummary(result.Risk);
        Assert.Contains(summary.CreatedItems, c => c is { Kind: "Zona atualizada", Code: "Q" });
        Assert.Contains(summary.Totals, t => t is { Label: "Alterações", Value: "1" });
    }

    [Fact]
    public void A_zone_with_stock_cannot_be_deactivated()
    {
        var error = Refused(Step("update_zone", "\"code\":\"A\",\"is_active\":false"));
        Assert.Equal(("is_active", "business.zone_has_stock"), (error.Field, error.Code));
        Assert.Contains("ainda tem", error.Message);
        Assert.Equal(1, Value("SELECT is_active FROM zones WHERE code = 'A'"));
    }

    [Fact]
    public void Zone_deactivation_sees_the_stock_left_by_earlier_steps_of_the_same_plan()
    {
        var stocked = Plan(Zone(), Range("Q", 1, 1, 2), Move("ABC-123", 10, "DOCA-01", "Q-01-01"),
            Step("update_zone", "\"code\":\"Q\",\"is_active\":false"));
        var error = Refused(stocked);
        Assert.Equal(("is_active", "business.zone_has_stock"), Pair(error));
        Assert.Equal(3, error.StepIndex);
        Assert.Contains("10 unidades", error.Message);

        var emptied = Plan(Zone(), Range("Q", 1, 1, 2), Move("ABC-123", 10, "DOCA-01", "Q-01-01"),
            Move("ABC-123", 10, "Q-01-01", "DOCA-01"), Step("update_zone", "\"code\":\"Q\",\"is_active\":false"));
        Assert.True(_h.Execute(emptied).Succeeded);
        Assert.Equal(0, Value("SELECT is_active FROM zones WHERE code = 'Q'"));
    }

    [Fact]
    public void Update_zone_refuses_unknown_other_warehouse_and_no_op_changes()
    {
        Assert.Equal("business.unknown_zone", Refused(Step("update_zone", "\"code\":\"Z\",\"name\":\"X\"")).Code);
        AddRemoteWarehouse();
        var remote = Refused(Step("update_zone", "\"code\":\"W\",\"name\":\"X\""));
        Assert.Equal("Passo 1: a zona W não existe neste armazém.", remote.Message);
        Assert.Equal(1, Value("SELECT COUNT(*) FROM zones WHERE code = 'W' AND name = 'Zona remota'"));
    }

    [Fact]
    public void Update_zone_with_nothing_new_is_refused_and_reactivation_works()
    {
        var same = Refused(Step("update_zone", "\"code\":\"A\",\"name\":\"Porta-paletes seco\""));
        Assert.Equal("business.nothing_to_change", same.Code);
        Assert.True(_h.Execute(Plan(Zone(), Step("update_zone", "\"code\":\"Q\",\"is_active\":false"))).Succeeded);
        var back = _h.Execute(Plan(Step("update_zone", "\"code\":\"Q\",\"is_active\":true")));
        Assert.True(back.Succeeded);
        Assert.Equal(1, Value("SELECT is_active FROM zones WHERE code = 'Q'"));
    }

    // ---- create_location_range ------------------------------------------------------------------------------

    [Fact]
    public void Range_creates_one_location_per_level_with_padded_codes_and_returns_the_first()
    {
        var result = _h.Execute(Plan(Zone(), Range("Q", 1, 1, 5, 80)));
        Assert.True(result.Succeeded);
        Assert.Equal(5, Value("SELECT COUNT(*) FROM locations l JOIN zones z ON z.id = l.zone_id WHERE z.code = 'Q' AND z.warehouse_id = 1 " +
                              "AND l.capacity = 80 AND l.is_active = 1 AND l.code IN ('Q-01-01','Q-01-02','Q-01-03','Q-01-04','Q-01-05')"));
        var created = result.Diff!.Created.Last();
        Assert.Equal(("Endereços", "Q-01-01"), (created.Kind, created.Code));
        Assert.Equal("5 endereços de Q-01-01 a Q-01-05, capacidade 80 cada", created.Description);
    }

    [Fact]
    public void Range_result_can_feed_a_later_step_and_a_single_level_is_valid()
    {
        var plan = Plan(Zone(), Range("Q", 12, 7, 7, 40, "first"), Move("ABC-123", 5, "DOCA-01", "$first"));
        Assert.True(_h.Execute(plan).Succeeded);
        Assert.Equal(5, _h.Qty("ABC-123", "Q-12-07"));
    }

    [Fact]
    public void Range_with_existing_codes_lists_them_and_creates_nothing()
    {
        var error = Refused(Plan(Range("A", 1, 1, 5)));
        Assert.Equal("business.duplicate_location", error.Code);
        Assert.Contains("A-01-01, A-01-02, A-01-03, A-01-04", error.Message);
        Assert.DoesNotContain("A-01-05", error.Message);
        Assert.Equal(0, Value("SELECT COUNT(*) FROM locations WHERE code = 'A-01-05'"));
    }

    [Fact]
    public void Range_with_existing_code_of_another_warehouse_is_refused_because_codes_are_global()
    {
        AddRemoteWarehouse();
        _h.Fixture.Exec("INSERT INTO zones (warehouse_id, code, name) VALUES (1, 'W', 'Zona local')");
        Assert.Contains("W-01-01", Refused(Plan(Range("W", 1, 1, 2))).Message);
    }

    [Fact]
    public void Range_refuses_unknown_inactive_and_other_warehouse_zones()
    {
        Assert.Equal("business.unknown_zone", Refused(Plan(Range("Q", 1, 1, 2))).Code);
        AddRemoteWarehouse();
        Assert.Equal("Passo 1: a zona W não existe neste armazém.", Refused(Plan(Range("W", 2, 1, 2))).Message);

        _h.Fixture.Exec("UPDATE zones SET is_active = 0 WHERE code = 'B'");
        var inactive = Refused(Plan(Range("B", 2, 1, 2)));
        Assert.Equal(("zone", "business.inactive_zone"), (inactive.Field, inactive.Code));
        Assert.Equal("Passo 1: a zona B está desativada.", inactive.Message);
    }

    [Fact]
    public void Range_with_inverted_levels_is_refused()
    {
        var error = Refused(Plan(Range("A", 9, 5, 2)));
        Assert.Equal(("level_to", "business.invalid_range"), (error.Field, error.Code));
        Assert.Contains("nível final", error.Message);
    }

    [Fact]
    public void Range_of_the_widest_size_stays_below_the_limit_of_100_locations()
    {
        var result = _h.Execute(Plan(Zone(), Range("Q", 99, 1, 99, 1)));
        Assert.True(result.Succeeded);
        Assert.Equal(99, Value("SELECT COUNT(*) FROM locations WHERE code LIKE 'Q-99-%'"));
    }

    // ---- update_location ------------------------------------------------------------------------------------

    [Fact]
    public void Update_location_changes_capacity_and_describes_it()
    {
        var result = _h.Execute(Plan(Step("update_location", "\"code\":\"A-01-01\",\"capacity\":80")));
        Assert.True(result.Succeeded);
        Assert.Equal(80, Value("SELECT capacity FROM locations WHERE code = 'A-01-01'"));
        var updated = Assert.Single(result.Diff!.Updated);
        Assert.Equal(("Endereço atualizado", "A-01-01", "capacidade 60 → 80"), (updated.Kind, updated.Code, updated.Description));
    }

    [Fact]
    public void Capacity_cannot_go_below_the_current_occupancy()
    {
        // A-01-01 holds 40.
        var error = Refused(Step("update_location", "\"code\":\"A-01-01\",\"capacity\":39"));
        Assert.Equal(("capacity", "business.capacity_below_stock"), (error.Field, error.Code));
        Assert.Contains("40", error.Message);
        Assert.True(_h.Execute(Plan(Step("update_location", "\"code\":\"A-01-01\",\"capacity\":40"))).Succeeded);
    }

    [Fact]
    public void Location_with_stock_cannot_be_deactivated_but_an_empty_one_can_and_comes_back()
    {
        var error = Refused(Step("update_location", "\"code\":\"A-01-01\",\"is_active\":false"));
        Assert.Equal(("is_active", "business.location_has_stock"), (error.Field, error.Code));

        var off = _h.Execute(Plan(Step("update_location", "\"code\":\"A-02-02\",\"is_active\":false")));
        Assert.True(off.Succeeded);
        Assert.Equal("situação ativo → desativado", off.Diff!.Updated.Single().Description);
        Assert.Equal(0, Value("SELECT is_active FROM locations WHERE code = 'A-02-02'"));

        Assert.True(_h.Execute(Plan(Step("update_location", "\"code\":\"A-02-02\",\"is_active\":true"))).Succeeded);
        Assert.Equal(1, Value("SELECT is_active FROM locations WHERE code = 'A-02-02'"));
    }

    [Fact]
    public void Update_location_refuses_unknown_other_warehouse_and_inactive_zone_reactivation()
    {
        Assert.Equal("business.unknown_location", Refused(Step("update_location", "\"code\":\"A-09-09\",\"capacity\":5")).Code);
        AddRemoteWarehouse();
        Assert.Equal("Passo 1: o endereço W-01-01 não existe neste armazém.",
            Refused(Step("update_location", "\"code\":\"W-01-01\",\"capacity\":5")).Message);

        _h.Fixture.Exec("UPDATE locations SET is_active = 0 WHERE code = 'A-02-02'");
        _h.Fixture.Exec("UPDATE zones SET is_active = 0 WHERE code = 'A'");
        Assert.Equal("business.inactive_zone", Refused(Step("update_location", "\"code\":\"A-02-02\",\"is_active\":true")).Code);
    }

    [Fact]
    public void Update_location_with_nothing_new_is_refused()
    {
        Assert.Equal("business.nothing_to_change", Refused(Step("update_location", "\"code\":\"A-01-01\",\"capacity\":60")).Code);
    }

    // ---- inactive zones and locations in the existing operations ---------------------------------------------

    [Fact]
    public void Create_location_refuses_an_inactive_zone()
    {
        _h.Fixture.Exec("UPDATE zones SET is_active = 0 WHERE code = 'B'");
        var error = Refused(Step("create_location", "\"code\":\"B-05-01\",\"zone\":\"B\",\"capacity\":10"));
        Assert.Equal(("zone", "business.inactive_zone"), (error.Field, error.Code));
    }

    [Fact]
    public void Move_and_receive_refuse_inactive_locations_and_locations_of_inactive_zones()
    {
        _h.Fixture.Exec("UPDATE locations SET is_active = 0 WHERE code = 'A-02-02'");
        Assert.Equal("Passo 1: o endereço A-02-02 está desativado.", Refused(Move("ABC-123", 1, "DOCA-01", "A-02-02")).Message);

        _h.Fixture.Exec("UPDATE zones SET is_active = 0 WHERE code = 'A'");
        Assert.Equal("Passo 1: a zona A do endereço A-02-01 está desativada.", Refused(Move("ABC-123", 1, "DOCA-01", "A-02-01")).Message);
        Assert.Contains("A-01-01", Refused(Move("ABC-123", 1, "A-01-01", "DOCA-02")).Message);

        _h.Fixture.Exec("UPDATE zones SET is_active = 0 WHERE code = 'DOCA'");
        Assert.Equal("Passo 1: a zona DOCA do endereço DOCA-01 está desativada.", Refused(Receive("ABC-123", 1, "DOCA-01")).Message);
    }

    // ---- update_product -------------------------------------------------------------------------------------

    private byte[]? CostBlob(string sku)
    {
        using var connection = _h.Fixture.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT unit_cost_enc FROM products WHERE sku = $sku";
        command.Parameters.AddWithValue("$sku", sku);
        return command.ExecuteScalar() as byte[];
    }

    [Fact]
    public void Update_product_changes_fields_and_describes_only_what_changed()
    {
        var result = _h.Execute(Plan(Step("update_product",
            "\"sku\":\"ABC-123\",\"name\":\"Caixa de parafusos 5mm\",\"description\":\"Nova\",\"unit\":\"un\",\"weight_kg\":3,\"tracks_expiry\":false")));
        // tracks_expiry is already false: not part of the description.
        Assert.True(result.Succeeded);
        Assert.Equal(1, Value("SELECT COUNT(*) FROM products WHERE sku = 'ABC-123' AND name = 'Caixa de parafusos 5mm' " +
                              "AND description = 'Nova' AND unit = 'un' AND weight_kg = 3.0"));
        var updated = Assert.Single(result.Diff!.Updated);
        Assert.Equal(("Produto atualizado", "ABC-123"), (updated.Kind, updated.Code));
        Assert.Equal("nome Caixa de parafusos 4mm → Caixa de parafusos 5mm; descrição alterada; unidade cx → un; peso 2.5 → 3 kg",
            updated.Description);
    }

    [Fact]
    public void Unit_cost_is_encrypted_with_the_id_as_context_and_never_stored_or_shown_in_clear()
    {
        var result = _h.Execute(Plan(Step("update_product", "\"sku\":\"ABC-123\",\"unit_cost\":12.5")));
        Assert.True(result.Succeeded);
        var id = Value("SELECT id FROM products WHERE sku = 'ABC-123'");
        var blob = CostBlob("ABC-123")!;
        Assert.NotNull(blob);
        Assert.DoesNotContain("12.5", System.Text.Encoding.UTF8.GetString(blob));
        using var cipher = _h.Fixture.Db.Database.CreateColumnCipher();
        Assert.Equal("12.5", cipher.Decrypt(blob, "products", "unit_cost_enc", id));
        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt(blob, "products", "unit_cost_enc", id + 1));

        var description = result.Diff!.Updated.Single().Description;
        Assert.Equal("custo unitário alterado", description);
        Assert.DoesNotContain("12.5", PlanDiffJson.Serialize(result.Diff));
    }

    [Fact]
    public void Update_product_refuses_unknown_sku_and_no_op()
    {
        Assert.Equal(("sku", "business.unknown_sku"), Pair(Refused(Step("update_product", "\"sku\":\"ZZZ-999\",\"name\":\"X\""))));
        Assert.Equal("business.nothing_to_change", Refused(Step("update_product", "\"sku\":\"ABC-123\",\"unit\":\"cx\"")).Code);
    }

    private static (string?, string) Pair(PlanError e) => (e.Field, e.Code);

    [Fact]
    public void Expiry_control_cannot_be_turned_on_while_there_is_balance_without_lot()
    {
        // ABC-123 has 40 + 40 units without a lot.
        var error = Refused(Step("update_product", "\"sku\":\"ABC-123\",\"tracks_expiry\":true"));
        Assert.Equal(("tracks_expiry", "business.balance_without_lot"), Pair(error));
        Assert.Contains("80 cx", error.Message);
        Assert.Equal(0, Value("SELECT tracks_expiry FROM products WHERE sku = 'ABC-123'"));
    }

    [Fact]
    public void Expiry_control_can_be_turned_on_for_a_product_without_stock_and_off_again()
    {
        var plan = Plan(
            "{\"op\":\"create_product\",\"args\":{\"sku\":\"NEW-010\",\"name\":\"Novo\",\"unit\":\"un\"}}",
            Step("update_product", "\"sku\":\"NEW-010\",\"tracks_expiry\":true"));
        var result = _h.Execute(plan);
        Assert.True(result.Succeeded);
        Assert.Equal("controle de validade não → sim", result.Diff!.Updated.Single().Description);
        Assert.Equal(1, Value("SELECT tracks_expiry FROM products WHERE sku = 'NEW-010'"));
        Assert.True(_h.Execute(Plan(Step("update_product", "\"sku\":\"ABC-123\",\"tracks_expiry\":false,\"name\":\"Outro nome\""))).Succeeded);
    }

    // ---- create_supplier / create_customer ------------------------------------------------------------------

    [Fact]
    public void Create_supplier_and_customer_store_name_and_masked_document()
    {
        var result = _h.Execute(Plan(
            Step("create_supplier", "\"name\":\"Nova Fornecedora Fictícia\",\"document_masked\":\"**.***.***/0009-**\""),
            Step("create_customer", "\"name\":\"Cliente Novo Fictício\",\"document_masked\":\"***.***.***-**\""),
            Step("create_customer", "\"name\":\"Cliente Sem Documento\"")));
        Assert.True(result.Succeeded);
        Assert.Equal(1, Value("SELECT COUNT(*) FROM suppliers WHERE name = 'Nova Fornecedora Fictícia' AND cnpj_masked = '**.***.***/0009-**' AND cnpj_enc IS NULL"));
        Assert.Equal(1, Value("SELECT COUNT(*) FROM customers WHERE name = 'Cliente Novo Fictício' AND document_masked = '***.***.***-**' AND document_enc IS NULL"));
        Assert.Equal(1, Value("SELECT COUNT(*) FROM customers WHERE name = 'Cliente Sem Documento' AND document_masked IS NULL"));
        Assert.Equal(["Fornecedor", "Cliente", "Cliente"], result.Diff!.Created.Select(c => c.Kind).ToArray());
    }

    [Fact]
    public void Partner_names_are_unique_ignoring_case_per_table()
    {
        var supplier = Refused(Step("create_supplier", "\"name\":\"fornecedora fictícia ALFA ltda\""));
        Assert.Equal(("name", "business.duplicate_name"), Pair(supplier));
        Assert.Contains("já existe um fornecedor chamado", supplier.Message);
        Assert.Equal("business.duplicate_name", Refused(Step("create_supplier", "\"name\":\"LATICÍNIOS FICTÍCIOS GAMA\"")).Code);
        Assert.Equal("business.duplicate_name", Refused(Step("create_customer", "\"name\":\"loja fictícia centro\"")).Code);
        // The same name in the other table is fine.
        Assert.True(_h.Execute(Plan(Step("create_customer", "\"name\":\"Fornecedora Fictícia Alfa Ltda\""))).Succeeded);
        // Twice in the same plan counts too.
        Assert.Equal("Passo 2: já existe um cliente chamado cliente x.", Refused(Plan(
            Step("create_customer", "\"name\":\"Cliente X\""), Step("create_customer", "\"name\":\"cliente x\""))).Message);
    }

    [Fact]
    public void Partner_name_made_of_spaces_is_refused()
    {
        Assert.Equal("business.invalid_name", Refused(Step("create_supplier", "\"name\":\"   \"")).Code);
    }

    // ---- the whole chain, all or nothing, simulation parity -------------------------------------------------

    private PlanDocument Chain(string lastMoveFrom = "DOCA-01") => Plan(
        Zone(),
        Range("Q", 1, 1, 3),
        Move("ABC-123", 10, lastMoveFrom, "Q-01-01"));

    [Fact]
    public void Chained_plan_validates_in_preview_and_applies_every_step()
    {
        var plan = Chain();
        var before = _h.Counts();
        var preview = _h.Simulate(plan);
        Assert.True(preview.Succeeded, string.Join("; ", preview.Errors.Select(e => e.Message)));
        Assert.Equal(before, _h.Counts());
        Assert.Equal(0, Value("SELECT COUNT(*) FROM zones WHERE code = 'Q'"));

        var real = _h.Execute(plan);
        Assert.True(real.Succeeded);
        Assert.True(preview.Diff!.SameAs(real.Diff!));
        Assert.Equal(1, Value("SELECT COUNT(*) FROM zones WHERE code = 'Q'"));
        Assert.Equal(3, Value("SELECT COUNT(*) FROM locations WHERE code LIKE 'Q-01-0_'"));
        Assert.Equal(10, _h.Qty("ABC-123", "Q-01-01"));
        Assert.Equal(30, _h.Qty("ABC-123", "DOCA-01"));
    }

    [Fact]
    public void A_failing_last_step_writes_nothing()
    {
        var before = _h.Counts();
        var zones = _h.Count("zones");
        var plan = Plan(Zone(), Range("Q", 1, 1, 3), Move("ABC-123", 10, "DOCA-02", "Q-01-01")); // DOCA-02 is empty
        var result = _h.Execute(plan);
        Assert.False(result.Succeeded);
        Assert.Equal(2, Assert.Single(result.Errors).StepIndex);
        Assert.Equal(before, _h.Counts());
        Assert.Equal(zones, _h.Count("zones"));
        Assert.Equal(0, Value("SELECT COUNT(*) FROM zones WHERE code = 'Q'"));
    }

    [Fact]
    public void Mixed_catalog_plan_has_simulation_execution_parity_and_a_fixed_json_roundtrip()
    {
        var plan = Plan(
            Zone(),
            Range("Q", 1, 1, 2, 30),
            Step("update_zone", "\"code\":\"Q\",\"name\":\"Quarentena 2\""),
            Step("update_location", "\"code\":\"Q-01-02\",\"capacity\":45"),
            Step("update_product", "\"sku\":\"FER-010\",\"name\":\"Furadeira 600W\",\"unit_cost\":99.9"),
            Step("create_supplier", "\"name\":\"Fornecedor Paridade\""),
            Step("create_customer", "\"name\":\"Cliente Paridade\",\"document_masked\":\"**\""));
        var simulated = _h.Simulate(plan);
        Assert.True(simulated.Succeeded, string.Join("; ", simulated.Errors.Select(e => e.Message)));
        var real = _h.Execute(plan);
        Assert.True(real.Succeeded);
        Assert.True(simulated.Diff!.SameAs(real.Diff!));
        Assert.Equal(RiskLevel.Reversible, real.Risk);

        var json = PlanDiffJson.Serialize(real.Diff!);
        Assert.True(PlanDiffJson.Deserialize(json).SameAs(real.Diff!));
        var summary = real.Diff!.ToSummary(real.Risk);
        Assert.Contains(summary.Totals, t => t is { Label: "Cadastros", Value: "4" });
        Assert.Contains(summary.Totals, t => t is { Label: "Alterações", Value: "3" });
        Assert.Equal(7, summary.CreatedItems.Count);
    }

    [Fact]
    public void Old_stored_diffs_without_the_updated_list_still_read()
    {
        var diff = PlanDiffJson.Deserialize("{\"v\":1,\"balances\":[],\"created\":[{\"kind\":\"Produto\",\"code\":\"A\",\"description\":\"b\"}]}");
        Assert.Single(diff.Created);
        Assert.Empty(diff.Updated);
    }

    // ---- rights, schema, injection --------------------------------------------------------------------------

    [Fact]
    public void Operator_without_the_rights_is_denied()
    {
        var result = _h.Execute(Plan(Zone(), Step("create_supplier", "\"name\":\"X Ltda\"")), _h.Operator);
        Assert.False(result.Succeeded);
        Assert.All(result.Errors, e => Assert.Equal("policy.denied", e.Code));
        Assert.Equal(0, Value("SELECT COUNT(*) FROM zones WHERE code = 'Q'"));
    }

    public static TheoryData<string, string> BadArguments => new()
    {
        { "create_zone", "\"code\":\"q\",\"name\":\"x\"" },
        { "create_zone", "\"code\":\"ABCDE\",\"name\":\"x\"" },
        { "create_zone", "\"code\":\"Q'; DROP TABLE zones;--\",\"name\":\"x\"" },
        { "create_zone", "\"code\":\"Q\",\"name\":\"\"" },
        { "create_zone", "\"code\":\"Q\",\"name\":\"linha\\nquebrada\"" },
        { "create_zone", "\"code\":\"Q\",\"name\":\"x\",\"extra\":1" },
        { "create_zone", "\"code\":\"Q\\n\",\"name\":\"x\"" },
        { "update_zone", "\"code\":\"Q\"" },
        { "update_zone", "\"code\":\"Q\",\"is_active\":\"false\"" },
        { "update_zone", "\"code\":\"Q\",\"capacity\":5" },
        { "create_location_range", "\"zone\":\"Q\",\"aisle\":0,\"level_from\":1,\"level_to\":2,\"capacity\":5" },
        { "create_location_range", "\"zone\":\"Q\",\"aisle\":100,\"level_from\":1,\"level_to\":2,\"capacity\":5" },
        { "create_location_range", "\"zone\":\"Q\",\"aisle\":1,\"level_from\":1,\"level_to\":100,\"capacity\":5" },
        { "create_location_range", "\"zone\":\"Q\",\"aisle\":1,\"level_from\":1,\"level_to\":2,\"capacity\":100001" },
        { "create_location_range", "\"zone\":\"Q\",\"aisle\":1.5,\"level_from\":1,\"level_to\":2,\"capacity\":5" },
        { "create_location_range", "\"zone\":\"Q' OR '1'='1\",\"aisle\":1,\"level_from\":1,\"level_to\":2,\"capacity\":5" },
        { "create_location_range", "\"zone\":\"Q\",\"aisle\":1,\"level_from\":1,\"capacity\":5" },
        { "update_location", "\"code\":\"A-01-01\"" },
        { "update_location", "\"code\":\"A-01-01\",\"capacity\":0" },
        { "update_location", "\"code\":\"A-01-01'--\",\"capacity\":5" },
        { "update_product", "\"sku\":\"ABC-123\"" },
        { "update_product", "\"sku\":\"abc-123\",\"name\":\"x\"" },
        { "update_product", "\"sku\":\"ABC-123\",\"unit\":\"UN\"" },
        { "update_product", "\"sku\":\"ABC-123\",\"unit_cost\":-1" },
        { "update_product", "\"sku\":\"ABC-123\",\"name\":\"x'); DROP TABLE products;--\\u0000\"" },
        { "create_supplier", "\"name\":\"X\",\"document_masked\":\"12.345.678/0001-90\"" },
        { "create_supplier", "\"name\":\"X\",\"document_masked\":\"**'; DROP TABLE suppliers;--\"" },
        { "create_supplier", "\"name\":\"X\",\"document_masked\":\"***************************\"" },
        { "create_customer", "\"name\":\"\"" },
        { "create_customer", "\"name\":\"X\",\"cnpj\":\"1\"" },
    };

    [Theory]
    [MemberData(nameof(BadArguments))]
    public void The_schema_rejects_bad_arguments_before_any_business_rule(string op, string args)
    {
        var parsed = DslParser.Parse("{\"dsl_version\":\"1.0\",\"intent\":\"teste\",\"steps\":[" + Step(op, args) + "]}");
        if (parsed.Document is null)
            return; // refused by the parser (e.g. a control character): equally safe
        var before = _h.Counts();
        var result = _h.Execute(parsed.Document);
        Assert.False(result.Succeeded);
        Assert.All(result.Errors, e => Assert.StartsWith("schema.", e.Code));
        Assert.Equal(before, _h.Counts());
        Assert.Equal(0, Value("SELECT COUNT(*) FROM zones WHERE code = 'Q'"));
    }

    [Fact]
    public void Injection_text_in_a_name_is_stored_as_text_not_executed()
    {
        const string name = "Robert'); DROP TABLE zones;-- <b>x</b>";
        Assert.True(_h.Execute(Plan(Zone("Q", name))).Succeeded);
        Assert.Equal(1, Value("SELECT COUNT(*) FROM zones WHERE name = $n", ("$n", name)));
        Assert.True(_h.Count("zones") >= 5);
    }

    [Fact]
    public void Update_steps_accept_references_to_earlier_results()
    {
        var plan = Plan(
            Zone(name_: "z"),
            Range("$z", 3, 1, 2, 20, "loc"),
            Step("update_zone", "\"code\":\"$z\",\"name\":\"Renomeada\""),
            Step("update_location", "\"code\":\"$loc\",\"capacity\":25"));
        Assert.True(_h.Execute(plan).Succeeded);
        Assert.Equal(25, Value("SELECT capacity FROM locations WHERE code = 'Q-03-01'"));
    }
}
