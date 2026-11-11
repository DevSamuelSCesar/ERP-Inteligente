using erp_inteligente.Core.Engine;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Planner;
using erp_inteligente.Core.Recipes;
using erp_inteligente.Core.Skills;
using erp_inteligente.Models;
using Nexus.Tests.Core.Policy;
using PlannerClass = erp_inteligente.Core.Planner.Planner;

namespace Nexus.Tests.Core.Planner;

/// <summary>Feature 8 in the Planner: each role sends its own provider and model; the Matcher accepts the fine-tuned format.</summary>
public sealed class ModelPerRoleTests : IDisposable
{
    private const string Request = "guarde as 40 caixas do ABC-123 da DOCA-01 no A-02-01";

    private readonly PolicyTestFixture _f = new();
    private readonly ScriptedLlm _llm = new();
    private readonly LlmSettings _settings;
    private readonly RecipeStore _store;
    private readonly UserSession _joao;

    public ModelPerRoleTests()
    {
        _settings = new LlmSettings(_f.Db.Store, "role-tests");
        _store = new RecipeStore(_f.Db.Database, _f.Policy, _f.Audit, _f.Time, new SkillCatalog(_f.Db.Database));
        _joao = _f.SessionFor("op.joao");
    }

    public void Dispose() => _f.Dispose();

    private Task<(PlannerResult Result, PlannerTurn? Turn)> Run(string request)
    {
        var engine = new PlanEngine(_f.Db.Database, _f.Policy, _f.Audit, _f.Time, recipes: _store);
        var planner = new PlannerClass(_llm, _settings, _f.Db.Database, _f.Policy, _f.Audit, engine, null, _f.Time,
            recipeMatcher: new RecipeMatcher(_llm, _store, _f.Audit, _settings));
        return planner.RunAsync(_joao, [], request, new Progress<string>(), TestContext.Current.CancellationToken);
    }

    private static LlmResponse Text(string text) => new(text, [], null, LlmFinishReason.Stop, new LlmUsage(10, 10));

    [Fact]
    public async Task Planner_and_matcher_send_their_own_provider_and_model()
    {
        _settings.SetChoice(LlmUseRole.Planner, new LlmChoice(LlmProviderKind.Claude, LlmSettings.ClaudeStrongModel));
        _settings.SetChoice(LlmUseRole.Matcher, new LlmChoice(LlmProviderKind.OpenAI, LlmSettings.OpenAiFastModel));
        _llm.Enqueue(Text("{\"receita\": null}")).Enqueue(Text("Preciso confirmar o destino."));

        await Run(Request);

        Assert.Equal((LlmProviderKind.OpenAI, LlmSettings.OpenAiFastModel), (_llm.Requests[0].Provider, _llm.Requests[0].Model));
        Assert.Equal((LlmProviderKind.Claude, LlmSettings.ClaudeStrongModel), (_llm.Requests[1].Provider, _llm.Requests[1].Model));
    }

    [Fact]
    public async Task Defaults_keep_deepseek_for_both_roles()
    {
        _llm.Enqueue(Text("{\"receita\": null}")).Enqueue(Text("ok"));
        await Run(Request);
        Assert.Equal((LlmProviderKind.DeepSeek, LlmSettings.FastModel), (_llm.Requests[0].Provider, _llm.Requests[0].Model));
        Assert.Equal((LlmProviderKind.DeepSeek, LlmSettings.StrongModel), (_llm.Requests[1].Provider, _llm.Requests[1].Model));
    }

    [Fact]
    public async Task The_fine_tuned_answer_format_is_accepted_by_the_matcher()
    {
        _settings.SetChoice(LlmUseRole.Matcher, new LlmChoice(LlmProviderKind.Local, LlmSettings.LocalModel));
        _llm.Enqueue(Text("""{"intent":"putaway","recipe":"armazenar_da_doca","params":{"sku":"ABC-123","qty":40,"dock":"DOCA-01","target":"A-02-01"}}"""));

        var (result, _) = await Run(Request);

        var item = Assert.Single(Assert.IsType<PlannerProposals>(result).Items);
        Assert.Equal(PlanOrigin.Recipe, item.Draft.Origin);
        Assert.Equal(LlmProviderKind.Local, Assert.Single(_llm.Requests).Provider);
    }

    [Fact]
    public async Task The_fine_tuned_none_answer_falls_back_to_the_planner()
    {
        _llm.Enqueue(Text("""{"intent":"none"}""")).Enqueue(Text("ok"));
        var (result, _) = await Run(Request);
        Assert.IsType<PlannerAnswer>(result);
        Assert.Equal(2, _llm.Requests.Count);
    }

    [Fact]
    public async Task A_missing_key_names_the_provider_of_the_planner()
    {
        _settings.SetChoice(LlmUseRole.Planner, new LlmChoice(LlmProviderKind.Claude, LlmSettings.ClaudeStrongModel));
        _llm.Throw(new LlmException(LlmErrorKind.NotConfigured, "x"));
        var (result, _) = await Run("oi");
        Assert.Equal("Configure a chave da Anthropic (Claude) em Configurações (administrador).", Assert.IsType<PlannerFailed>(result).Reason);
    }

    [Fact]
    public void Matcher_prompt_is_the_file_the_dataset_generator_reads()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Core", "Recipes", "Prompts", "matcher.md");
        Assert.Equal(File.ReadAllText(file).Replace("\r\n", "\n").TrimEnd(), RecipeMatcher.SystemPrompt);
        Assert.Contains("{\"receita\": null}", RecipeMatcher.SystemPrompt);
    }
}
