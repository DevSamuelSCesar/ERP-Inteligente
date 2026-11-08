using System.Linq;
using System.Text.Json.Nodes;
using erp_inteligente.Core.Engine;

namespace Nexus.Tests.Core.Engine;

public class SchemaValidatorTests
{
    private static IReadOnlyList<PlanError> Check(string op, string args, string? alias = null, string? earlierAlias = null)
    {
        var steps = new List<PlanStepDocument>();
        if (earlierAlias is not null)
            steps.Add(new PlanStepDocument(0, "create_location", JsonNode.Parse("""{"code":"A-09-09","zone":"A","capacity":1}""")!.AsObject(), earlierAlias));
        steps.Add(new PlanStepDocument(steps.Count, op, JsonNode.Parse(args)!.AsObject(), alias));
        return SchemaValidator.Validate(new PlanDocument("1.0", "test", null, steps));
    }

    private static PlanError Single(IReadOnlyList<PlanError> errors, string code, string? field)
    {
        var e = Assert.Single(errors, x => x.Code == code && x.Field == field);
        Assert.StartsWith("Passo ", e.Message);
        return e;
    }

    [Theory]
    [InlineData("receive_goods", """{"sku":"ABC-123","qty":5,"location":"DOCA-01"}""")]
    [InlineData("receive_goods", """{"sku":"ABC-123","qty":100000,"location":"A-01-01","lot":"L1","expires_on":"2027-01-31","inbound_order":"PE-1"}""")]
    [InlineData("move_stock", """{"sku":"ABC-123","qty":1,"from":"A-01-01","to":"B-02-03","lot":"L1"}""")]
    [InlineData("adjust_stock", """{"sku":"ABC-123","qty":-100000,"location":"A-01-01","reason":"Avaria"}""")]
    [InlineData("adjust_stock", """{"sku":"ABC-123","qty":3,"location":"A-01-01","reason":"Inventário"}""")]
    [InlineData("create_product", """{"sku":"ABC-123","name":"Parafuso","unit":"un"}""")]
    [InlineData("create_product", """{"sku":"ABC-123","name":"Parafuso","unit":"kg","description":"x","weight_kg":0,"tracks_expiry":true,"unit_cost":12.5}""")]
    [InlineData("create_location", """{"code":"A-01-01","zone":"A","capacity":100}""")]
    [InlineData("create_location", """{"code":"DOCA-01","zone":"DOCA","capacity":1}""")]
    public void Valid_examples_pass(string op, string args) => Assert.Empty(Check(op, args));

    [Fact]
    public void Missing_required_names_the_field()
    {
        var errors = Check("receive_goods", """{"sku":"ABC-123","location":"DOCA-01"}""");
        var e = Single(errors, "schema.required", "qty");
        Assert.Equal("Passo 1: falta a quantidade.", e.Message);
        Assert.Equal(0, e.StepIndex);
    }

    [Fact]
    public void Every_missing_required_field_is_reported_with_step_number()
    {
        var errors = Check("adjust_stock", "{}", earlierAlias: "loc");
        Assert.Equal(4, errors.Count);
        Assert.All(errors, e => Assert.Equal(1, e.StepIndex));
        Assert.Contains(errors, e => e.Message == "Passo 2: falta o SKU.");
        Assert.Contains(errors, e => e.Message == "Passo 2: falta o motivo.");
        Assert.Contains(errors, e => e.Message == "Passo 2: falta o endereço.");
    }

    [Theory]
    [InlineData("abc-123")]
    [InlineData("ABCD-123")]
    [InlineData("ABC-12")]
    [InlineData("ABC-123 ")]
    [InlineData("ABC-123\n")]
    [InlineData("ABC-١٢٣")]
    public void Bad_sku_pattern(string sku)
    {
        var args = new JsonObject { ["sku"] = sku, ["qty"] = 1, ["from"] = "A-01-01", ["to"] = "A-01-02" }.ToJsonString();
        var e = Assert.Single(Check("move_stock", args));
        Assert.Equal("sku", e.Field);
        Assert.Contains("Passo 1", e.Message);
        Assert.Contains("SKU", e.Message);
    }

    [Fact]
    public void Sku_message_is_business_language()
    {
        var e = Assert.Single(Check("move_stock", """{"sku":"xx","qty":1,"from":"A-01-01","to":"A-01-02"}"""));
        Assert.Equal("Passo 1: o SKU deve ter o formato ABC-123 ou ser o resultado de um passo anterior.", e.Message);
        var created = Assert.Single(Check("create_product", """{"sku":"xx","name":"n","unit":"un"}"""));
        Assert.Equal("Passo 1: o SKU deve ter o formato ABC-123.", created.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("100001")]
    [InlineData("1.5")]
    [InlineData("\"3\"")]
    public void Bad_qty(string qty)
    {
        var e = Assert.Single(Check("move_stock", $$"""{"sku":"ABC-123","qty":{{qty}},"from":"A-01-01","to":"A-01-02"}"""));
        Assert.Equal("qty", e.Field);
        Assert.Contains("quantidade", e.Message);
        Assert.Contains("Passo 1", e.Message);
    }

    [Fact]
    public void Qty_bounds_messages()
    {
        var e = Assert.Single(Check("move_stock", """{"sku":"ABC-123","qty":0,"from":"A-01-01","to":"A-01-02"}"""));
        Assert.Equal("schema.minimum", e.Code);
        Assert.Equal("Passo 1: a quantidade deve ser no mínimo 1.", e.Message);
        var m = Assert.Single(Check("move_stock", """{"sku":"ABC-123","qty":100001,"from":"A-01-01","to":"A-01-02"}"""));
        Assert.Equal("Passo 1: a quantidade deve ser no máximo 100000.", m.Message);
        var t = Assert.Single(Check("move_stock", """{"sku":"ABC-123","qty":1.5,"from":"A-01-01","to":"A-01-02"}"""));
        Assert.Equal("schema.type", t.Code);
        Assert.Equal("Passo 1: a quantidade deve ser um número inteiro.", t.Message);
    }

    [Fact]
    public void Adjust_qty_zero_is_rejected_but_negative_is_valid()
    {
        var e = Assert.Single(Check("adjust_stock", """{"sku":"ABC-123","qty":0,"location":"A-01-01","reason":"x"}"""));
        Assert.Equal("schema.not", e.Code);
        Assert.Equal("Passo 1: a quantidade não pode ser zero.", e.Message);
        Assert.Empty(Check("adjust_stock", """{"sku":"ABC-123","qty":-1,"location":"A-01-01","reason":"x"}"""));
        Assert.Single(Check("adjust_stock", """{"sku":"ABC-123","qty":-100001,"location":"A-01-01","reason":"x"}"""));
    }

    [Fact]
    public void Reason_limits()
    {
        var long_ = new string('x', 201);
        var e = Assert.Single(Check("adjust_stock", $$"""{"sku":"ABC-123","qty":1,"location":"A-01-01","reason":"{{long_}}"}"""));
        Assert.Equal("reason", e.Field);
        Assert.Contains("motivo", e.Message);
        Assert.Contains("200", e.Message);
        var empty = Assert.Single(Check("adjust_stock", """{"sku":"ABC-123","qty":1,"location":"A-01-01","reason":""}"""));
        Assert.Equal("schema.minLength", empty.Code);
    }

    [Fact]
    public void Unknown_argument_is_reported_sanitized()
    {
        var e = Assert.Single(Check("move_stock", """{"sku":"ABC-123","qty":1,"from":"A-01-01","to":"A-01-02","x\"; DROP":1}"""));
        Assert.Equal("schema.additional_property", e.Code);
        Assert.Contains("Passo 1", e.Message);
        Assert.DoesNotContain("DROP TABLE", e.Message);
        Assert.DoesNotContain("\"; ", e.Message);
        var plain = Assert.Single(Check("move_stock", """{"sku":"ABC-123","qty":1,"from":"A-01-01","to":"A-01-02","extra":1}"""));
        Assert.Contains("extra", plain.Message);
    }

    [Theory]
    [InlineData("receive_goods", """{"sku":"ABC-123","qty":1,"location":"doca-01"}""", "location", "endereço")]
    [InlineData("receive_goods", """{"sku":"ABC-123","qty":1,"location":"A-01-01","expires_on":"31/01/2027"}""", "expires_on", "validade")]
    [InlineData("receive_goods", """{"sku":"ABC-123","qty":1,"location":"A-01-01","lot":""}""", "lot", "lote")]
    [InlineData("receive_goods", """{"sku":"ABC-123","qty":1,"location":"A-01-01","inbound_order":"%41"}""", "inbound_order", "pedido de entrada")]
    [InlineData("move_stock", """{"sku":"ABC-123","qty":1,"from":"A1","to":"A-01-02"}""", "from", "origem")]
    [InlineData("move_stock", """{"sku":"ABC-123","qty":1,"from":"A-01-01","to":"A-1-02"}""", "to", "destino")]
    [InlineData("create_product", """{"sku":"ABC-123","name":"","unit":"un"}""", "name", "nome")]
    [InlineData("create_product", """{"sku":"ABC-123","name":"n","unit":"UN"}""", "unit", "unidade")]
    [InlineData("create_product", """{"sku":"ABC-123","name":"n","unit":"unidade"}""", "unit", "unidade")]
    [InlineData("create_product", """{"sku":"ABC-123","name":"n","unit":"un","weight_kg":-1}""", "weight_kg", "peso")]
    [InlineData("create_product", """{"sku":"ABC-123","name":"n","unit":"un","unit_cost":2000000000}""", "unit_cost", "custo unitário")]
    [InlineData("create_product", """{"sku":"ABC-123","name":"n","unit":"un","tracks_expiry":"sim"}""", "tracks_expiry", "controle de validade")]
    [InlineData("create_product", """{"sku":"ABC-123","name":"n","unit":"un","description":"%501"}""", "description", "descrição")]
    [InlineData("create_location", """{"code":"a-01","zone":"A","capacity":1}""", "code", "código")]
    [InlineData("create_location", """{"code":"A-01","zone":"a","capacity":1}""", "zone", "zona")]
    [InlineData("create_location", """{"code":"A-01","zone":"ABCDE","capacity":1}""", "zone", "zona")]
    [InlineData("create_location", """{"code":"A-01","zone":"A","capacity":0}""", "capacity", "capacidade")]
    public void Constraint_violations_mention_step_and_label(string op, string args, string field, string label)
    {
        args = args.Replace("%41", new string('x', 41)).Replace("%501", new string('x', 501));
        var errors = Check(op, args);
        var e = Assert.Single(errors);
        Assert.Equal(field, e.Field);
        Assert.Equal(0, e.StepIndex);
        Assert.StartsWith("Passo 1:", e.Message);
        Assert.Contains(label, e.Message);
    }

    [Fact]
    public void References_are_accepted_in_reference_fields_only()
    {
        Assert.Empty(Check("move_stock", """{"sku":"$s","qty":1,"from":"$f","to":"$loc"}""", earlierAlias: "loc"));
        var e = Assert.Single(Check("move_stock", """{"sku":"ABC-123","qty":"$x","from":"A-01-01","to":"A-01-02"}"""));
        Assert.Equal("qty", e.Field);
        var lot = Assert.Single(Check("move_stock", """{"sku":"ABC-123","qty":1,"from":"A-01-01","to":"A-01-02","lot":"$x"}""".Replace("\"$x\"", "{}")));
        Assert.Equal("lot", lot.Field);
        Assert.Single(Check("create_product", """{"sku":"$x","name":"n","unit":"un"}"""));
        Assert.Single(Check("create_location", """{"code":"$x","zone":"A","capacity":1}"""));
    }

    [Fact]
    public void Reference_with_trailing_newline_is_rejected()
    {
        var args = new JsonObject { ["sku"] = "ABC-123", ["qty"] = 1, ["from"] = "A-01-01", ["to"] = "$abc\n" }.ToJsonString();
        var e = Assert.Single(Check("move_stock", args));
        Assert.Equal("to", e.Field);
    }

    [Fact]
    public void Non_string_reference_field_is_a_text_error()
    {
        var e = Assert.Single(Check("move_stock", """{"sku":5,"qty":1,"from":"A-01-01","to":"A-01-02"}"""));
        Assert.Equal("Passo 1: o SKU deve ser um texto.", e.Message);
    }

    [Fact]
    public void Nested_objects_in_args_are_rejected()
    {
        var e = Assert.Single(Check("move_stock", """{"sku":{"a":[1]},"qty":1,"from":"A-01-01","to":"A-01-02"}"""));
        Assert.Equal("sku", e.Field);
    }

    [Fact]
    public void Errors_are_per_step()
    {
        var doc = new PlanDocument("1.0", "t", null,
        [
            new PlanStepDocument(0, "move_stock", JsonNode.Parse("""{"sku":"ABC-123","qty":1,"from":"A-01-01","to":"A-01-02"}""")!.AsObject(), null),
            new PlanStepDocument(1, "move_stock", JsonNode.Parse("""{"sku":"ABC-123","qty":0,"from":"A-01-01","to":"A-01-02"}""")!.AsObject(), null),
        ]);
        var e = Assert.Single(SchemaValidator.Validate(doc));
        Assert.Equal(1, e.StepIndex);
        Assert.StartsWith("Passo 2:", e.Message);
    }
}

public class ReferenceResolverTests
{
    [Fact]
    public void Replaces_references_with_a_deep_copy()
    {
        var step = new PlanStepDocument(1, "move_stock",
            JsonNode.Parse("""{"sku":"$s","qty":2,"from":"A-01-01","to":"$loc"}""")!.AsObject(), null);
        var resolved = ReferenceResolver.Resolve(step, new Dictionary<string, string> { ["s"] = "ABC-123", ["loc"] = "B-01-01" });
        Assert.Equal("ABC-123", (string?)resolved["sku"]);
        Assert.Equal("B-01-01", (string?)resolved["to"]);
        Assert.Equal(2, (int?)resolved["qty"]);
        Assert.Equal("$s", (string?)step.Args["sku"]);
        Assert.NotSame(step.Args, resolved);
    }

    [Fact]
    public void Missing_result_throws()
    {
        var step = new PlanStepDocument(0, "move_stock", JsonNode.Parse("""{"sku":"$s"}""")!.AsObject(), null);
        Assert.Throws<InvalidOperationException>(() => ReferenceResolver.Resolve(step, new Dictionary<string, string>()));
    }

    [Fact]
    public void Values_that_only_look_like_references_are_left_alone()
    {
        var step = new PlanStepDocument(0, "adjust_stock", JsonNode.Parse("""{"reason":"custa $5 a mais"}""")!.AsObject(), null);
        var resolved = ReferenceResolver.Resolve(step, new Dictionary<string, string>());
        Assert.Equal("custa $5 a mais", (string?)resolved["reason"]);
    }
}
