using erp_inteligente.Core.Engine;
using erp_inteligente.Core.Knowledge;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Planner;
using erp_inteligente.Models;
using Nexus.Eval;

namespace Nexus.Tests;

/// <summary>The evaluation sets of references/IA.md, section 5 (tools/eval) and the runner that scores them.</summary>
public sealed class EvalTests : IDisposable
{
    private static readonly EvalSets Sets = EvalSets.Load(EvalSets.FindDirectory(AppContext.BaseDirectory));

    /// <summary>Planner calls follow the script; the Recipe Matcher (a call without tools) always answers "no recipe".</summary>
    private sealed class RoutingLlm : ILlmProvider
    {
        public Queue<LlmResponse> Script { get; } = new();

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(request.Tools.Count == 0
                ? new LlmResponse("""{"receita": null}""", [], null, LlmFinishReason.Stop, new LlmUsage(50, 5))
                : Script.Dequeue());
    }

    private readonly RoutingLlm _llm = new();
    private readonly TestApp _app;

    public EvalTests() => _app = new TestApp(llm: _llm);

    public void Dispose() => _app.Dispose();

    private static LlmResponse Text(string text) => new(text, [], null, LlmFinishReason.Stop, new LlmUsage(100, 10));

    private static LlmResponse Call(string name, string args) =>
        new(null, [new LlmToolCall("c1", name, args)], null, LlmFinishReason.ToolCalls, new LlmUsage(100, 10));

    private EvalRunner Runner() => new(_app.Host, _app.LoginAsync);

    // ---- the sets ----------------------------------------------------------------------------------------

    [Fact]
    public void Sets_have_the_sizes_of_the_spec_and_unique_ids()
    {
        Assert.Equal(50, Sets.Planning.Count);
        Assert.Equal(40, Sets.Knowledge.Count);
        Assert.Equal(30, Sets.OutOfScope.Count);
        Assert.Equal(10, Sets.Proactive.Count);
        var ids = Sets.Planning.Select(c => c.Id).Concat(Sets.Knowledge.Select(c => c.Id))
            .Concat(Sets.OutOfScope.Select(c => c.Id)).Concat(Sets.Proactive.Select(c => c.Id)).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(Sets.Proactive, c => Assert.Contains(c.Expect, EvalSets.Expectations));
        Assert.Contains(Sets.Proactive, c => c.Expect == "refusal");
    }

    [Fact]
    public async Task Every_planning_case_is_allowed_to_its_user_and_uses_catalog_operations()
    {
        foreach (var c in Sets.Planning)
        {
            var user = await _app.LoginAsync(c.User);
            var rights = _app.Host.Policy.EffectiveForAi(user);
            foreach (var op in c.Ops)
            {
                Assert.True(OperationCatalog.TryGet(op, out var definition), $"{c.Id}: {op} is not in the catalog");
                Assert.True(rights.Contains(definition.Permission), $"{c.Id}: {c.User} cannot {op}");
            }
        }
    }

    [Fact]
    public void Every_expected_source_is_an_embedded_document()
    {
        var paths = KnowledgeIndexer.EmbeddedSources().Select(s => s.Path).ToHashSet();
        Assert.All(Sets.Knowledge, c =>
        {
            Assert.NotEmpty(c.Keywords);
            Assert.All(c.Sources, s => Assert.Contains(s, paths));
        });
        Assert.Equal("POP-03", EvalRunner.DocumentCode("# POP-03 Armazenagem (putaway)\n\n## Objetivo"));
    }

    // ---- scoring -----------------------------------------------------------------------------------------

    [Fact]
    public void Scoring_rules()
    {
        Assert.True(EvalRunner.ScorePlanning(["receive_goods", "move_stock"], ["receive_goods", "move_stock", "move_stock"]));
        Assert.False(EvalRunner.ScorePlanning(["receive_goods", "move_stock"], ["receive_goods"]));
        Assert.False(EvalRunner.ScorePlanning(["move_stock"], []));

        Assert.Equal((true, true), EvalRunner.ScoreAnswer("Refrigerados vão para a ZONA C (POP-03).", ["zona c"], ["POP-03"]));
        Assert.Equal((true, false), EvalRunner.ScoreAnswer("Vão para a zona C.", ["zona c"], ["POP-03"]));
        Assert.Equal((false, true), EvalRunner.ScoreAnswer("Veja o POP-03.", ["zona c"], ["POP-03"]));
        Assert.True(EvalRunner.ScoreAnswer("Não, a expedição exige conferência.", ["expedicao"], []).Correct); // accents ignored

        var answer = new PlannerAnswer("Não posso.");
        var question = new PlannerAnswer("Qual lote?", [new PlannerOption("L1", "lote L1")]);
        Assert.True(EvalRunner.ScoreExpectation("refusal", answer));
        Assert.False(EvalRunner.ScoreExpectation("proposals", answer));
        Assert.True(EvalRunner.ScoreExpectation("proposals_or_options", question));
        Assert.False(EvalRunner.ScoreExpectation("proposals_or_options", answer));
        Assert.Equal(["move_stock", "adjust_stock"],
            EvalRunner.OpsOf("""{"steps":[{"op":"move_stock","args":{}},{"op":"adjust_stock","args":{}}]}"""));
    }

    // ---- the runner over the real core -------------------------------------------------------------------

    [Fact]
    public async Task Planning_case_counts_invalid_attempts_tokens_and_the_answer_key()
    {
        const string plan = """{"title":"Guardar","rationale":"Atende.","plan":{"dsl_version":"1.0","intent":"putaway","skill":"armazenagem","steps":[{"op":"move_stock","args":{"sku":"ABC-123","qty":40,"from":"DOCA-01","to":"Q-99-99"}}]}}""";
        _llm.Script.Enqueue(Call("propose_plan", plan));
        _llm.Script.Enqueue(Call("propose_plan", plan.Replace("Q-99-99", "A-02-02", StringComparison.Ordinal)));
        _llm.Script.Enqueue(Text("Pronto."));

        var result = Assert.Single(await Runner().PlanningAsync([Sets.Planning[0]], TestContext.Current.CancellationToken));

        Assert.True(result.Passed, result.Note);
        Assert.Equal(1, result.InvalidPlans);
        Assert.False(result.FirstPlanValid);
        Assert.Equal(330 + 55, result.Tokens); // three Planner calls and the Recipe Matcher call before them
    }

    [Fact]
    public async Task Rag_and_scope_suites_run_on_the_real_core()
    {
        var rag = await Runner().RagAsync(Sets.Knowledge.Take(5).ToList(), TestContext.Current.CancellationToken);
        Assert.All(rag, r => Assert.True(r.Passed, r.Id + " " + r.Note));
        Assert.All(rag, r => Assert.InRange(r.Rank, 1, EvalRunner.RecallK));

        _llm.Script.Enqueue(Text("Só ajudo com o armazém."));
        var scope = await Runner().ScopeAsync([Sets.OutOfScope[0]], [], TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(scope).Passed);

        var report = new EvalReport("teste", DateTimeOffset.UnixEpoch, [.. rag, .. scope]);
        var markdown = report.ToMarkdown();
        Assert.Contains("| RAG (40 perguntas) | 5 | recall@4 | 100.0% |", markdown);
        Assert.Contains("| Fora do escopo (30) | 1 | recusas corretas | 100.0% |", markdown);
    }
}
