using System.Linq;
using System.Text;
using erp_inteligente.Core.Engine;
using erp_inteligente.Models;

namespace Nexus.Tests.Core.Engine;

public class DslParserTests
{
    private const string Plan1 = """{"intent":"putaway","steps":[{"op":"move_stock","args":{"sku":"ABC-123","qty":40,"from":"DOCA-01","to":"A-01-01"}}]}""";
    private const string Plan2 = """{"intent":"adjust","steps":[{"op":"adjust_stock","args":{"sku":"ALI-300","lot":"L300-2611A","qty":-1,"location":"C-01-01","reason":"Avaria: embalagem violada"}}]}""";

    private static string Step(string op = "move_stock", string args = """{"sku":"ABC-123","qty":1,"from":"A-01-01","to":"A-01-02"}""", string? alias = null) =>
        $$"""{"op":"{{op}}","args":{{args}}{{(alias is null ? "" : $",\"as\":\"{alias}\"")}}}""";

    private static string Doc(params string[] steps) =>
        $$"""{"dsl_version":"1.0","intent":"putaway","skill":"armazenagem","steps":[{{string.Join(",", steps)}}]}""";

    private static void AssertCode(DslParseResult r, string code)
    {
        Assert.Null(r.Document);
        Assert.Contains(r.Errors, e => e.Code == code);
    }

    [Theory]
    [InlineData(Plan1)]
    [InlineData(Plan2)]
    public void Seed_plans_parse_and_pass_schema_with_stored_version(string json)
    {
        var r = DslParser.Parse(json, "1.0");
        Assert.Empty(r.Errors);
        Assert.NotNull(r.Document);
        Assert.Empty(SchemaValidator.Validate(r.Document!));
    }

    [Fact]
    public void PlanDraft_output_parses()
    {
        var draft = PlanDraft.Create("putaway", "armazenagem", new[]
        {
            new PlanDraftStep("receive_goods", new Dictionary<string, object?> { ["sku"] = "ABC-123", ["qty"] = 5, ["location"] = "DOCA-01" }, "rec"),
            new PlanDraftStep("move_stock", new Dictionary<string, object?> { ["sku"] = "$rec", ["qty"] = 5, ["from"] = "DOCA-01", ["to"] = "A-01-01" }),
        });
        var r = DslParser.Parse(draft.Json);
        Assert.Empty(r.Errors);
        Assert.Equal(2, r.Document!.Steps.Count);
        Assert.Equal("rec", r.Document.Steps[0].As);
        Assert.Empty(SchemaValidator.Validate(r.Document));
    }

    [Fact]
    public void Unknown_op_is_rejected_with_step_index()
    {
        var r = DslParser.Parse(Doc(Step(), Step(op: "drop_table")));
        AssertCode(r, "dsl.unknown_op");
        var e = r.Errors.Single(x => x.Code == "dsl.unknown_op");
        Assert.Equal(1, e.StepIndex);
        Assert.Contains("Passo 2", e.Message);
        Assert.DoesNotContain("drop_table", e.Message);
    }

    [Fact]
    public void Bad_version_is_rejected() => AssertCode(DslParser.Parse(Doc(Step()).Replace("\"1.0\"", "\"2.0\"")), "dsl.bad_version");

    [Fact]
    public void Missing_version_is_rejected_without_stored_value()
    {
        const string json = """{"intent":"x","steps":[{"op":"move_stock","args":{}}]}""";
        AssertCode(DslParser.Parse(json), "dsl.missing_version");
    }

    [Fact]
    public void Stored_version_is_validated_too() =>
        AssertCode(DslParser.Parse(Plan1, "9.9"), "dsl.bad_version");

    [Fact]
    public void Extra_root_property_is_rejected() =>
        AssertCode(DslParser.Parse(Doc(Step()).Replace("\"intent\"", "\"admin\":true,\"intent\"")), "dsl.unknown_property");

    [Fact]
    public void Extra_step_property_is_rejected() =>
        AssertCode(DslParser.Parse(Doc(Step().Replace("\"op\"", "\"sql\":\"x\",\"op\""))), "dsl.unknown_property");

    [Fact]
    public void Duplicate_json_property_is_rejected() =>
        AssertCode(DslParser.Parse(Doc(Step()).Replace("\"intent\":\"putaway\"", "\"intent\":\"putaway\",\"intent\":\"other\"")), "dsl.invalid_json");

    [Theory]
    [InlineData("Putaway")]
    [InlineData("a-b")]
    [InlineData("")]
    public void Bad_intent_is_rejected(string intent) =>
        AssertCode(DslParser.Parse(Doc(Step()).Replace("\"putaway\"", $"\"{intent}\"")), "dsl.bad_name");

    [Fact]
    public void Intent_with_trailing_newline_is_rejected() =>
        AssertCode(DslParser.Parse(Doc(Step()).Replace("\"putaway\"", "\"putaway\\n\"")), "dsl.bad_name");

    [Fact]
    public void Null_skill_is_accepted()
    {
        var r = DslParser.Parse(Doc(Step()).Replace("\"armazenagem\"", "null"));
        Assert.Empty(r.Errors);
        Assert.Null(r.Document!.Skill);
    }

    [Fact]
    public void Bad_skill_is_rejected() =>
        AssertCode(DslParser.Parse(Doc(Step()).Replace("armazenagem", "Armazenagem!")), "dsl.bad_name");

    [Fact]
    public void Too_many_steps_and_no_steps_are_rejected()
    {
        AssertCode(DslParser.Parse(Doc(Enumerable.Repeat(Step(), 51).ToArray())), "dsl.bad_steps");
        AssertCode(DslParser.Parse(Doc()), "dsl.bad_steps");
        var fifty = DslParser.Parse(Doc(Enumerable.Repeat(Step(), 50).ToArray()));
        Assert.Empty(fifty.Errors);
    }

    [Fact]
    public void Duplicate_as_is_rejected()
    {
        var r = DslParser.Parse(Doc(Step(alias: "a"), Step(alias: "a")));
        AssertCode(r, "dsl.duplicate_as");
        Assert.Equal(1, r.Errors.Single(e => e.Code == "dsl.duplicate_as").StepIndex);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("1a")]
    [InlineData("a-b")]
    public void Bad_as_is_rejected(string alias) => AssertCode(DslParser.Parse(Doc(Step(alias: alias))), "dsl.bad_as");

    [Fact]
    public void Forward_reference_is_rejected()
    {
        var first = Step(args: """{"sku":"$later","qty":1,"from":"A-01-01","to":"A-01-02"}""");
        var r = DslParser.Parse(Doc(first, Step(alias: "later")));
        AssertCode(r, "dsl.bad_reference");
        Assert.Equal(0, r.Errors.Single(e => e.Code == "dsl.bad_reference").StepIndex);
    }

    [Fact]
    public void Undefined_and_self_references_are_rejected()
    {
        AssertCode(DslParser.Parse(Doc(Step(args: """{"sku":"$nope","qty":1,"from":"A-01-01","to":"A-01-02"}"""))), "dsl.bad_reference");
        AssertCode(DslParser.Parse(Doc(Step(args: """{"sku":"$me","qty":1,"from":"A-01-01","to":"A-01-02"}""", alias: "me"))), "dsl.bad_reference");
    }

    [Fact]
    public void Earlier_reference_is_accepted()
    {
        var second = Step(args: """{"sku":"ABC-123","qty":1,"from":"A-01-01","to":"$loc"}""");
        var r = DslParser.Parse(Doc(Step("create_location", """{"code":"A-09-09","zone":"A","capacity":10}""", "loc"), second));
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void All_errors_are_collected()
    {
        var r = DslParser.Parse(Doc(Step(op: "x"), Step(op: "y")).Replace("\"1.0\"", "\"3\""));
        Assert.True(r.Errors.Count >= 3);
    }

    [Fact]
    public void Oversized_input_is_rejected()
    {
        var big = Doc(Step()).Replace("putaway", new string('a', DslParser.MaxBytes));
        AssertCode(DslParser.Parse(big), "dsl.too_large");
    }

    [Fact]
    public void Size_limit_counts_utf8_bytes()
    {
        // 40k two-byte characters are 80 KB in UTF-8 but only 40k UTF-16 chars.
        var json = Doc(Step()).Replace("putaway", new string('é', 40_000));
        AssertCode(DslParser.Parse(json), "dsl.too_large");
    }

    [Fact]
    public void Deep_nesting_is_rejected()
    {
        var deep = new StringBuilder();
        deep.Append(string.Concat(Enumerable.Repeat("[", 40))).Append(string.Concat(Enumerable.Repeat("]", 40)));
        var json = Doc(Step(args: $$"""{"sku":{{deep}}}"""));
        AssertCode(DslParser.Parse(json), "dsl.too_deep");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"x\"")]
    public void Malformed_or_non_object_input_is_rejected(string json)
    {
        var r = DslParser.Parse(json, "1.0");
        Assert.Null(r.Document);
        Assert.Single(r.Errors);
    }

    [Fact]
    public void Missing_args_and_non_object_step_are_rejected()
    {
        AssertCode(DslParser.Parse(Doc("""{"op":"move_stock"}""")), "dsl.bad_args");
        AssertCode(DslParser.Parse(Doc("42")), "dsl.invalid_structure");
        AssertCode(DslParser.Parse(Doc("""{"args":{}}""")), "dsl.missing_op");
    }

    [Fact]
    public void ToJson_round_trips_and_includes_version()
    {
        var parsed = DslParser.Parse(Plan1, "1.0").Document!;
        var json = parsed.ToJson();
        Assert.Contains("\"dsl_version\":\"1.0\"", json);
        var again = DslParser.Parse(json).Document!;
        Assert.Equal(json, again.ToJson());
        Assert.Equal(parsed.Intent, again.Intent);
        Assert.Equal(parsed.Steps[0].Args.ToJsonString(), again.Steps[0].Args.ToJsonString());
    }

    [Fact]
    public void ToJson_keeps_as_and_skill()
    {
        var doc = DslParser.Parse(Doc(Step(alias: "r"))).Document!;
        var again = DslParser.Parse(doc.ToJson()).Document!;
        Assert.Equal("r", again.Steps[0].As);
        Assert.Equal("armazenagem", again.Skill);
    }
}
