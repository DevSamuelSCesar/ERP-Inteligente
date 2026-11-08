using System.Text.Json;
using System.Text.Json.Nodes;
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Engine;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Planner;
using erp_inteligente.Models;
using Nexus.Tests.Core.Policy;
using Nexus.Tests.Services;
using PlannerClass = erp_inteligente.Core.Planner.Planner;

namespace Nexus.Tests.Core.Planner;

/// <summary>
/// <c>search_products</c> through the real Planner loop over the seeded catalog (10 products in CD-01):
/// free text with or without SKU, partial SKUs, filters, paging, ranking, hostile input and warehouse scope.
/// </summary>
public sealed class ProductSearchTests : IDisposable
{
    private readonly PolicyTestFixture _f = new();
    private readonly ScriptedLlm _llm = new();
    private readonly UserSession _joao;

    public ProductSearchTests() => _joao = _f.SessionFor("op.joao");

    public void Dispose() => _f.Dispose();

    /// <summary>Runs one search_products call and returns the JSON inside the &lt;dados&gt; block.</summary>
    private async Task<JsonObject> Search(string argumentsJson, UserSession? user = null)
    {
        _llm.Enqueue(new LlmResponse(null, [new LlmToolCall("s1", PlannerTools.SearchProducts, argumentsJson)], null,
            LlmFinishReason.ToolCalls, new LlmUsage(100, 10)));
        _llm.Enqueue(new LlmResponse("ok", [], null, LlmFinishReason.Stop, new LlmUsage(100, 10)));
        var engine = new PlanEngine(_f.Db.Database, _f.Policy, _f.Audit, _f.Time);
        var planner = new PlannerClass(_llm, new LlmSettings(_f.Db.Store, "search-tests"), _f.Db.Database, _f.Policy, _f.Audit, engine);
        await planner.RunAsync(user ?? _joao, [], "pesquisa", new Progress<string>(), TestContext.Current.CancellationToken);

        var content = _llm.Requests[^1].Messages.Last(m => m.Role == LlmRole.Tool).Content!;
        var start = content.IndexOf('>') + 1;
        var end = content.LastIndexOf("</dados>", StringComparison.Ordinal);
        return JsonNode.Parse(content[start..end])!.AsObject();
    }

    private static string[] Skus(JsonObject result) =>
        result["items"]!.AsArray().Select(i => i!["sku"]!.GetValue<string>()).ToArray();

    [Fact]
    public async Task Without_arguments_it_lists_the_whole_catalog()
    {
        var result = await Search("{}");

        Assert.Equal(10, result["total"]!.GetValue<long>());
        Assert.Equal(10, Skus(result).Length);
        Assert.False(result["has_more"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("parafuso", "ABC-123")]
    [InlineData("PARAFÚSOS", "ABC-123")]
    [InlineData("mucarela", "ALI-301")]
    [InlineData("caixa porcas", "ABC-124")]
    [InlineData("abc124", "ABC-124")]
    [InlineData("500 unidades porcas", "ABC-124")]
    public async Task Free_text_ignores_case_and_accents_and_needs_every_term(string query, string expected)
    {
        var result = await Search(JsonSerializer.Serialize(new { query }));

        Assert.Equal([expected], Skus(result));
    }

    [Theory]
    [InlineData("abc", new[] { "ABC-123", "ABC-124" })]
    [InlineData("ABC-12", new[] { "ABC-123", "ABC-124" })]
    [InlineData("abc123", new[] { "ABC-123" })]
    [InlineData("ALI-301", new[] { "ALI-301" })]
    public async Task Sku_may_be_complete_or_partial(string sku, string[] expected)
    {
        var result = await Search(JsonSerializer.Serialize(new { sku }));

        Assert.Equal(expected, Skus(result));
    }

    [Fact]
    public async Task Name_and_sku_together_narrow_the_search()
    {
        var result = await Search("""{"query":"caixa","sku":"ABC"}""");

        Assert.Equal(["ABC-123", "ABC-124"], Skus(result));
    }

    [Fact]
    public async Task Exact_sku_ranks_first()
    {
        var result = await Search("""{"query":"caixa","sku":"ABC-124"}""");

        Assert.Equal("ABC-124", Skus(result)[0]);
    }

    [Fact]
    public async Task Product_created_without_stock_is_found_by_name_and_by_has_stock_false()
    {
        _f.Exec("INSERT INTO products (sku, name, unit, tracks_expiry) VALUES ('XYZ-001', 'Parafuso sextavado', 'cx', 0)");

        var byName = await Search("""{"query":"sextavado"}""");
        var item = Assert.Single(byName["items"]!.AsArray())!;
        Assert.Equal(("XYZ-001", 0L, 0L), (item["sku"]!.GetValue<string>(), item["quantity"]!.GetValue<long>(), item["available"]!.GetValue<long>()));

        Assert.Equal(["XYZ-001"], Skus(await Search("""{"has_stock":false}""")));
        Assert.DoesNotContain("XYZ-001", Skus(await Search("""{"has_stock":true}""")));
    }

    [Fact]
    public async Task Filters_by_expiry_control_and_unit()
    {
        var expiring = await Search("""{"tracks_expiry":true}""");
        Assert.Equal(4, expiring["total"]!.GetValue<long>());
        Assert.All(expiring["items"]!.AsArray(), i => Assert.True(i!["tracks_expiry"]!.GetValue<bool>()));

        Assert.All(Skus(await Search("""{"unit":"cx"}""")), s => Assert.Contains(s, new[] { "ABC-123", "ABC-124", "ALI-300", "TST-INJ-01" }));
    }

    [Fact]
    public async Task Pages_with_limit_and_offset()
    {
        var first = await Search("""{"limit":3}""");
        var last = await Search("""{"limit":3,"offset":9}""");

        Assert.Equal(3, Skus(first).Length);
        Assert.True(first["has_more"]!.GetValue<bool>());
        Assert.Single(Skus(last));
        Assert.False(last["has_more"]!.GetValue<bool>());
        Assert.DoesNotContain(Skus(last)[0], Skus(first));
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("%%__")]
    public async Task Wildcards_alone_are_not_a_search(string query)
    {
        var result = await Search(JsonSerializer.Serialize(new { query }));

        Assert.Equal("invalid_arguments", result["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Wildcards_inside_text_only_separate_terms()
    {
        // "a%b_c" is the three literal terms a, b and c, never a LIKE pattern matching everything.
        var result = await Search("""{"query":"a%b_c"}""");

        Assert.True(result["total"]!.GetValue<long>() < 10);
        Assert.All(result["items"]!.AsArray(), i =>
        {
            var text = PlannerTools.Fold($"{i!["sku"]} {i["name"]} {i["description"]}");
            Assert.All(new[] { "a", "b", "c" }, t => Assert.Contains(t, text));
        });
    }

    [Theory]
    [InlineData("""{"query":"' OR 1=1 --"}""")]
    [InlineData("""{"query":"x'); DROP TABLE products; --"}""")]
    [InlineData("""{"query":"\" UNION SELECT unit_cost_enc FROM products --"}""")]
    public async Task Sql_in_the_text_is_only_searched_for(string arguments)
    {
        var result = await Search(arguments);

        Assert.False(result.ContainsKey("error"));
        Assert.True(result["total"]!.GetValue<long>() < 10);
        Assert.Equal(10, _f.Scalar("SELECT COUNT(*) FROM products"));
    }

    [Theory]
    [InlineData("""{"sku":"ABC'--"}""")]
    [InlineData("""{"sku":"ABC-1234"}""")]
    [InlineData("""{"sku":"ABC\n"}""")]
    [InlineData("""{"unit":"CX"}""")]
    [InlineData("""{"limit":500}""")]
    [InlineData("""{"query":"x","cost":true}""")]
    [InlineData("""{"query":"--- ..."}""")]
    public async Task Malformed_arguments_are_refused_and_audited(string arguments)
    {
        var result = await Search(arguments);

        Assert.Equal("invalid_arguments", result["error"]!.GetValue<string>());
        Assert.Equal(1, _f.Scalar("SELECT COUNT(*) FROM audit_log WHERE action = 'ai.tool' AND target = 'tool:search_products' AND outcome = 'denied'"));
    }

    [Fact]
    public async Task Results_never_carry_the_unit_cost()
    {
        var result = await Search("{}");

        Assert.All(result["items"]!.AsArray(), i => Assert.DoesNotContain(i!.AsObject(), p => p.Key.Contains("cost")));
    }

    [Fact]
    public async Task Balance_counts_only_the_session_warehouse()
    {
        var before = (await Search("""{"sku":"ABC-123"}"""))["items"]![0]!["quantity"]!.GetValue<long>();
        SqliteTestData.AddSecondWarehouse(_f);

        Assert.Equal(before, (await Search("""{"sku":"ABC-123"}"""))["items"]![0]!["quantity"]!.GetValue<long>());
        Assert.Equal(99, (await Search("""{"sku":"ABC-123"}""", _f.SessionFor("op.ana")))["items"]![0]!["quantity"]!.GetValue<long>());
    }

    [Fact]
    public async Task Allowed_call_is_audited_and_the_chain_stays_valid()
    {
        await Search("""{"query":"parafuso"}""");

        Assert.Equal(1, _f.Scalar("SELECT COUNT(*) FROM audit_log WHERE action = 'ai.tool' AND target = 'tool:search_products' AND outcome = 'allowed' AND on_behalf_of = $u",
            ("$u", _joao.UserId)));
        using var connection = _f.Open();
        Assert.True(AuditVerifier.Verify(connection).IsValid);
    }

    [Fact]
    public async Task Tool_is_offered_and_listed_first()
    {
        await Search("{}");

        Assert.Equal(PlannerTools.SearchProducts, _llm.Requests[0].Tools[0].Name);
        Assert.Contains("search_products", _llm.Requests[0].Messages[0].Content);
    }

    [Theory]
    [InlineData("Muçarela ÁÉ", "mucarela ae")]
    [InlineData("", "")]
    public void Fold_lowercases_and_removes_accents(string input, string expected) =>
        Assert.Equal(expected, PlannerTools.Fold(input));

    [Theory]
    [InlineData("abc123", "ABC-123")]
    [InlineData("ABC-1", "ABC-1")]
    [InlineData("ab", "AB")]
    public void Sku_fragment_is_normalized(string input, string expected) =>
        Assert.Equal(expected, PlannerTools.NormalizeSkuFragment(input));
}
