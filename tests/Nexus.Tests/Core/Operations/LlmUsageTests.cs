using erp_inteligente.Core.Engine;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Operations;
using erp_inteligente.Core.Planner;
using erp_inteligente.Core.Policy;
using erp_inteligente.Core.Recipes;
using erp_inteligente.Core.Skills;
using erp_inteligente.Models;
using Nexus.Tests.Core.Planner;
using Nexus.Tests.Core.Policy;
using PlannerClass = erp_inteligente.Core.Planner.Planner;

namespace Nexus.Tests.Core.Operations;

/// <summary>Feature 9: every request records its tokens; the cost report counts requests, recipes and estimated costs.</summary>
public sealed class LlmUsageTests : IDisposable
{
    private const string Request = "guarde as 40 caixas do ABC-123 da DOCA-01 no A-02-01";

    private readonly PolicyTestFixture _f = new();
    private readonly ScriptedLlm _llm = new();
    private readonly LlmUsageLog _usage;
    private readonly RecipeStore _recipes;
    private readonly UserSession _joao;

    public LlmUsageTests()
    {
        _usage = new LlmUsageLog(_f.Db.Database, _f.Time, _f.Policy);
        _recipes = new RecipeStore(_f.Db.Database, _f.Policy, _f.Audit, _f.Time, new SkillCatalog(_f.Db.Database));
        _joao = _f.SessionFor("op.joao");
    }

    public void Dispose() => _f.Dispose();

    private Task<(PlannerResult, PlannerTurn?)> Run(string request)
    {
        var settings = new LlmSettings(_f.Db.Store, "usage-tests");
        var engine = new PlanEngine(_f.Db.Database, _f.Policy, _f.Audit, _f.Time, recipes: _recipes);
        var planner = new PlannerClass(_llm, settings, _f.Db.Database, _f.Policy, _f.Audit, engine, null, _f.Time,
            recipeMatcher: new RecipeMatcher(_llm, _recipes, _f.Audit, settings, _usage), usage: _usage);
        return planner.RunAsync(_joao, [], request, new Progress<string>(), TestContext.Current.CancellationToken);
    }

    private static LlmResponse Text(string text, int prompt, int completion) =>
        new(text, [], null, LlmFinishReason.Stop, new LlmUsage(prompt, completion));

    private static LlmResponse Call(string name, string args, int prompt, int completion) =>
        new(null, [new LlmToolCall("c1", name, args)], null, LlmFinishReason.ToolCalls, new LlmUsage(prompt, completion));

    private long Count(string sql) => _f.Scalar(sql);

    [Fact]
    public async Task A_planned_request_records_one_row_with_the_summed_tokens_and_a_no_match()
    {
        _llm.Enqueue(Text("{\"receita\": null}", 300, 20))
            .Enqueue(Call("get_stock", "{\"sku\":\"ABC-123\"}", 1000, 50))
            .Enqueue(Text("ABC-123 tem 80 caixas.", 1500, 40));
        await Run(Request);

        Assert.Equal(1, Count("SELECT COUNT(*) FROM llm_usage WHERE role = 'planner' AND provider = 'deepseek' AND model = 'deepseek-v4-pro' " +
                              "AND calls = 2 AND prompt_tokens = 2500 AND completion_tokens = 90 AND result = 'answer' AND user_id = 1"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM llm_usage WHERE role = 'matcher' AND model = 'deepseek-flash' AND result = 'no_match' " +
                              "AND prompt_tokens = 300"));
    }

    [Fact]
    public async Task A_request_solved_by_a_recipe_records_only_the_matcher()
    {
        _llm.Enqueue(Text("""{"receita":"armazenar_da_doca","parametros":{"sku":"ABC-123","qty":40,"dock":"DOCA-01","target":"A-02-01"}}""", 400, 60));
        await Run(Request);

        Assert.Equal(1, Count("SELECT COUNT(*) FROM llm_usage"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM llm_usage WHERE role = 'matcher' AND result = 'recipe'"));
    }

    [Fact]
    public async Task A_provider_failure_is_recorded_as_failed()
    {
        _llm.Throw(new LlmException(LlmErrorKind.Unavailable, "x"));
        await Run("oi");
        Assert.Equal(1, Count("SELECT COUNT(*) FROM llm_usage WHERE role = 'planner' AND result = 'failed' AND calls = 1 AND prompt_tokens = 0"));
    }

    [Fact]
    public void Report_counts_requests_recipe_share_and_costs_per_currency()
    {
        var maria = _f.SessionFor("sup.maria");
        _usage.Record(_joao, new LlmUsageEntry(LlmUseRole.Planner, new LlmChoice(LlmProviderKind.Claude, LlmSettings.ClaudeStrongModel), 3, 1_000_000, 100_000, "proposals"));
        _usage.Record(_joao, new LlmUsageEntry(LlmUseRole.Matcher, new LlmChoice(LlmProviderKind.Claude, LlmSettings.ClaudeFastModel), 1, 1_000_000, 0, "recipe"));
        _usage.Record(_joao, new LlmUsageEntry(LlmUseRole.Matcher, new LlmChoice(LlmProviderKind.Claude, LlmSettings.ClaudeFastModel), 1, 0, 0, "no_match"));
        _usage.Record(_joao, new LlmUsageEntry(LlmUseRole.Planner, new LlmChoice(LlmProviderKind.DeepSeek, LlmSettings.StrongModel), 1, 1_000_000, 1_000_000, "answer"));
        _usage.Record(_joao, new LlmUsageEntry(LlmUseRole.Planner, new LlmChoice(LlmProviderKind.OpenAI, LlmSettings.OpenAiStrongModel), 1, 500, 500, "answer"));

        var report = _usage.Report(maria, _f.Time.GetUtcNow().AddDays(-30));

        Assert.Equal(4, report.Requests); // 3 planner requests + 1 solved by a recipe
        Assert.Equal(1, report.ByRecipe);
        Assert.Equal(0.25, report.RecipeShare);
        Assert.Equal(2m + 1m + 0.1m, report.CostByCurrency["USD"]); // Sonnet 1M in + 0.1M out; Haiku 1M in
        Assert.Equal(9m + 27m, report.CostByCurrency["CNY"]);
        Assert.Equal(1000, report.Unpriced);
        Assert.Equal(4, report.Lines.Count); // the two Haiku matcher rows are one line
        var haiku = report.Lines.Single(l => l.Model == LlmSettings.ClaudeFastModel);
        Assert.Equal((2L, 2L), (haiku.Requests, haiku.Calls));
        Assert.Throws<PolicyDeniedException>(() => _usage.Report(_joao, _f.Time.GetUtcNow().AddDays(-30)));
    }

    [Fact]
    public void Old_usage_is_outside_the_period()
    {
        _usage.Record(_joao, new LlmUsageEntry(LlmUseRole.Planner, new LlmChoice(LlmProviderKind.DeepSeek, LlmSettings.StrongModel), 1, 10, 10, "answer"));
        _f.Time.Advance(TimeSpan.FromDays(10));
        Assert.Equal(0, _usage.Report(_f.SessionFor("sup.maria"), _f.Time.GetUtcNow().AddDays(-7)).Requests);
    }

    [Fact]
    public void Cached_input_is_priced_at_its_share_of_the_input_price()
    {
        var choice = new LlmChoice(LlmProviderKind.Claude, LlmSettings.ClaudeStrongModel);
        _usage.Record(_joao, new LlmUsageEntry(LlmUseRole.Planner, choice, 3, 1_000_000, 100_000, "answer")
        {
            CacheReadTokens = 800_000,
            CacheWriteTokens = 100_000,
        });
        // A cached part larger than the prompt is clamped to it.
        _usage.Record(_joao, new LlmUsageEntry(LlmUseRole.Matcher, new LlmChoice(LlmProviderKind.Claude, LlmSettings.ClaudeFastModel), 1, 100, 10, "no_match")
        {
            CacheReadTokens = 5_000,
        });

        var report = _usage.Report(_f.SessionFor("sup.maria"), _f.Time.GetUtcNow().AddDays(-1));

        var planner = report.Lines.Single(l => l.Role == LlmUseRole.Planner);
        // Per million: 100k uncached × US$ 2 + 800k read × US$ 0.20 + 100k written × US$ 2.50 + 100k out × US$ 10.
        Assert.Equal(0.20m + 0.16m + 0.25m + 1.00m, planner.Cost);
        Assert.Equal(800_000, planner.CacheReadTokens);
        Assert.Equal(100, Count("SELECT cache_read_tokens FROM llm_usage WHERE role = 'matcher'"));
        Assert.Equal(0.10m * 0.10m * 100 / 1_000_000m + 0.50m * 10 / 1_000_000m, report.Lines.Single(l => l.Role == LlmUseRole.Matcher).Cost);
    }
}
