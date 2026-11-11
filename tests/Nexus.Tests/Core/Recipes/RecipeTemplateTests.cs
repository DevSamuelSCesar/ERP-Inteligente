using System.Text.Json.Nodes;
using erp_inteligente.Core.Engine;
using erp_inteligente.Core.Recipes;

namespace Nexus.Tests.Core.Recipes;

public sealed class RecipeTemplateTests
{
    private static PlanDocument Parse(string json)
    {
        var parsed = DslParser.Parse(json);
        Assert.True(parsed.Document is not null, string.Join("; ", parsed.Errors.Select(e => e.Message)));
        return parsed.Document!;
    }

    private const string TwoMoves =
        """{"dsl_version":"1.0","intent":"putaway","skill":"armazenagem","steps":[""" +
        """{"op":"move_stock","args":{"sku":"ABC-123","qty":10,"from":"DOCA-01","to":"A-02-01"}},""" +
        """{"op":"move_stock","args":{"sku":"ABC-123","qty":5,"from":"A-02-01","to":"A-02-02"}}]}""";

    private const string ChainedZone =
        """{"dsl_version":"1.0","intent":"structure","steps":[""" +
        """{"op":"create_zone","args":{"code":"Q","name":"Quarentena"},"as":"zona"},""" +
        """{"op":"create_location_range","args":{"zone":"$zona","aisle":1,"level_from":1,"level_to":3,"capacity":100}}]}""";

    private static RecipeRecord Record(ExtractedTemplate t, PlanDocument doc) =>
        new(1, "teste", "Teste", doc.Intent, doc.Skill, "armazenagem@1", "1.0", RecipeStatus.Active, 0, 0, 0,
            t.TemplateJson, t.ParamsSchemaJson, t.ExampleParams.ToJsonString(), t.Signature, null, null, null, null, null, null)
        {
            Parameters = RecipeTemplate.Parameters(t.ParamsSchemaJson, t.ExampleParams.ToJsonString()),
            Operations = RecipeTemplate.Operations(t.TemplateJson),
        };

    [Fact]
    public void Signature_ignores_values_and_result_names_but_not_structure()
    {
        Assert.Equal("move_stock(from,qty,sku,to);move_stock(from,qty,sku,to)", RecipeTemplate.Signature(Parse(TwoMoves)));
        var renamed = ChainedZone.Replace("\"zona\"", "\"z\"").Replace("$zona", "$z").Replace("\"Q\"", "\"X\"");
        Assert.Equal(RecipeTemplate.Signature(Parse(ChainedZone)), RecipeTemplate.Signature(Parse(renamed)));
        Assert.Equal("create_zone(code,name)>@1;create_location_range(aisle,capacity,level_from,level_to,zone=@1)",
            RecipeTemplate.Signature(Parse(ChainedZone)));
        Assert.NotEqual(RecipeTemplate.Signature(Parse(TwoMoves)),
            RecipeTemplate.Signature(Parse(TwoMoves.Replace(",\"to\":\"A-02-02\"", ",\"to\":\"A-02-02\",\"lot\":\"L1\""))));
    }

    [Fact]
    public void Extract_turns_literals_into_typed_parameters_and_keeps_references()
    {
        var doc = Parse(ChainedZone);
        var t = RecipeTemplate.Extract(doc);

        Assert.Contains("\"zone\":\"$zona\"", t.TemplateJson); // the step reference stays
        Assert.Contains("\"code\":\"$code\"", t.TemplateJson);
        var parameters = RecipeTemplate.Parameters(t.ParamsSchemaJson, t.ExampleParams.ToJsonString());
        Assert.Equal(["code", "name", "aisle", "level_from", "level_to", "capacity"], parameters.Select(p => p.Name));
        Assert.Equal("integer", parameters.Single(p => p.Name == "aisle").Type);
        Assert.Equal("corredor", parameters.Single(p => p.Name == "aisle").Label);
        Assert.Equal("Q", parameters.Single(p => p.Name == "code").Example);
    }

    [Fact]
    public void Repeated_arguments_get_one_parameter_per_step()
    {
        var t = RecipeTemplate.Extract(Parse(TwoMoves));
        var names = RecipeTemplate.Parameters(t.ParamsSchemaJson, null).Select(p => p.Name).ToList();
        Assert.Equal(["sku_1", "qty_1", "from_1", "to_1", "sku_2", "qty_2", "from_2", "to_2"], names);
        Assert.Equal("quantidade (passo 2)", RecipeTemplate.Parameters(t.ParamsSchemaJson, null).Single(p => p.Name == "qty_2").Label);
    }

    [Fact]
    public void Instantiate_fills_the_template_and_round_trips_to_the_same_plan()
    {
        var doc = Parse(TwoMoves);
        var t = RecipeTemplate.Extract(doc);
        var instance = RecipeTemplate.Instantiate(Record(t, doc), t.ExampleParams);

        Assert.True(instance.IsValid, string.Join("; ", instance.Errors));
        var rebuilt = Parse(instance.PlanJson!);
        Assert.Equal(doc.ToJson(), rebuilt.ToJson());
        Assert.Empty(SchemaValidator.Validate(rebuilt));
    }

    [Fact]
    public void Instantiate_refuses_missing_wrong_and_unknown_values_with_business_messages()
    {
        var doc = Parse(TwoMoves);
        var t = RecipeTemplate.Extract(doc);
        var recipe = Record(t, doc);

        var missing = (JsonObject)t.ExampleParams.DeepClone();
        missing.Remove("qty_1");
        Assert.Contains("Informe quantidade (passo 1).", RecipeTemplate.Instantiate(recipe, missing).Errors);

        var wrong = (JsonObject)t.ExampleParams.DeepClone();
        wrong["sku_2"] = "x' OR '1'='1";
        Assert.Contains("Valor inválido para SKU (passo 2).", RecipeTemplate.Instantiate(recipe, wrong).Errors);

        var extra = (JsonObject)t.ExampleParams.DeepClone();
        extra["steps"] = "[]";
        Assert.Contains("A receita recebeu um parâmetro desconhecido.", RecipeTemplate.Instantiate(recipe, extra).Errors);
    }

    [Theory]
    [InlineData("integer", "40", "40")]
    [InlineData("integer", " -3 ", "-3")]
    [InlineData("number", "2,5", "2.5")]
    [InlineData("boolean", "sim", "true")]
    [InlineData("string", " ABC-123 ", "\"ABC-123\"")]
    [InlineData("array", "[{\"sku\":\"ABC-123\",\"qty\":2}]", "[{\"sku\":\"ABC-123\",\"qty\":2}]")]
    public void Form_values_are_converted_to_the_parameter_type(string type, string text, string json)
    {
        Assert.True(RecipeTemplate.TryConvert(new RecipeParameter("p", "valor", type, true, null), text, out var value, out _));
        Assert.Equal(json, value!.ToJsonString());
    }

    [Theory]
    [InlineData("integer", "4a")]
    [InlineData("integer", "")]
    [InlineData("boolean", "talvez")]
    [InlineData("array", "{}")]
    public void Bad_form_values_are_refused(string type, string text)
    {
        Assert.False(RecipeTemplate.TryConvert(new RecipeParameter("p", "valor", type, true, null), text, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }
}
