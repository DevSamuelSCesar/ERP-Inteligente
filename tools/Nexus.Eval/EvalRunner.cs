using System.Globalization;
using System.Text;
using System.Text.Json;
using erp_inteligente;
using erp_inteligente.Core.Knowledge;
using erp_inteligente.Core.Memory;
using erp_inteligente.Core.Planner;
using erp_inteligente.Models;

namespace Nexus.Eval;

/// <summary>Outcome of one case: <see cref="Passed"/>, a short note and what it cost.</summary>
/// <remarks><see cref="Rank"/>: for the RAG suite, the position (1-based) of the first expected document; 0 when absent.</remarks>
public sealed record CaseResult(string Id, string Suite, bool Passed, string Note, int InvalidPlans = 0, bool FirstPlanValid = false,
    long Tokens = 0, double Seconds = 0, int Rank = 0);

/// <summary>The metrics of references/IA.md, section 5, for one run.</summary>
public sealed record EvalReport(string Label, DateTimeOffset At, IReadOnlyList<CaseResult> Cases)
{
    public IEnumerable<CaseResult> Of(string suite) => Cases.Where(c => c.Suite == suite);

    public static double Rate(IEnumerable<CaseResult> cases, Func<CaseResult, bool> ok)
    {
        var list = cases.ToList();
        return list.Count == 0 ? double.NaN : (double)list.Count(ok) / list.Count;
    }

    public string ToMarkdown()
    {
        var b = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        string Pct(double v) => double.IsNaN(v) ? "—" : (v * 100).ToString("0.0", inv) + "%";
        b.AppendLine($"# Avaliação · {Label}").AppendLine().AppendLine($"Executada em {At:yyyy-MM-dd HH:mm} UTC.").AppendLine();
        b.AppendLine("| Conjunto | Casos | Métrica | Resultado |").AppendLine("|---|---:|---|---:|");
        void Row(string suite, string metric, double value, int count) => b.AppendLine($"| {suite} | {count} | {metric} | {Pct(value)} |");
        var rag = Of("rag").ToList();
        if (rag.Count > 0)
        {
            Row("RAG (40 perguntas)", "recall@4", Rate(rag, c => c.Passed), rag.Count);
            Row("RAG (40 perguntas)", "recall@1", Rate(rag, c => c.Rank == 1), rag.Count);
            b.AppendLine($"| RAG (40 perguntas) | {rag.Count} | MRR@4 | {rag.Average(c => c.Rank == 0 ? 0 : 1.0 / c.Rank).ToString("0.000", inv)} |");
        }
        var planning = Of("planning").ToList();
        if (planning.Count > 0)
        {
            Row("Planejamento (50 pedidos)", "plano com as operações do gabarito", Rate(planning, c => c.Passed), planning.Count);
            Row("Planejamento (50 pedidos)", "plano válido na 1ª tentativa", Rate(planning, c => c.FirstPlanValid), planning.Count);
            b.AppendLine($"| Planejamento (50 pedidos) | {planning.Count} | tentativas inválidas por pedido | {planning.Average(c => c.InvalidPlans).ToString("0.00", inv)} |");
        }
        var answers = Of("knowledge").ToList();
        if (answers.Count > 0) Row("Conhecimento (40 perguntas)", "resposta correta e com fonte certa", Rate(answers, c => c.Passed), answers.Count);
        var scope = Of("out_of_scope").ToList();
        if (scope.Count > 0) Row("Fora do escopo (30)", "recusas corretas", Rate(scope, c => c.Passed), scope.Count);
        var proactive = Of("proactive").ToList();
        if (proactive.Count > 0) Row("Proativos e sem direito (10)", "desfecho esperado", Rate(proactive, c => c.Passed), proactive.Count);
        var llm = Cases.Where(c => c.Suite != "rag").ToList();
        if (llm.Count > 0)
            b.AppendLine().AppendLine($"Tokens médios por pedido: {llm.Average(c => c.Tokens).ToString("0", inv)}; tempo médio: {llm.Average(c => c.Seconds).ToString("0.0", inv)} s.");
        var failed = Cases.Where(c => !c.Passed).ToList();
        if (failed.Count > 0)
        {
            b.AppendLine().AppendLine("## Casos que falharam").AppendLine();
            foreach (var f in failed) b.AppendLine($"- `{f.Id}` ({f.Suite}): {f.Note}");
        }
        return b.ToString();
    }
}

/// <summary>
/// Runs the evaluation sets against a real <see cref="AppHost"/>: the RAG recall needs no LLM; the other suites call the
/// Planner with whatever provider the host is configured for. Every request starts without history.
/// </summary>
public sealed class EvalRunner(AppHost host, Func<string, Task<UserSession>> session, TextWriter? log = null)
{
    public const int RecallK = 4;

    private readonly Dictionary<string, string> _codes = KnowledgeIndexer.EmbeddedSources()
        .ToDictionary(s => s.Path, s => DocumentCode(s.Markdown), StringComparer.Ordinal);

    /// <summary>"POP-03" from "# POP-03 Armazenagem (putaway)".</summary>
    public static string DocumentCode(string markdown)
    {
        var first = markdown.Split('\n', 2)[0].TrimStart('#', ' ');
        return first.Split(' ', 2)[0];
    }

    public async Task<IReadOnlyList<CaseResult>> RagAsync(IReadOnlyList<KnowledgeCase> cases, CancellationToken ct)
    {
        var results = new List<CaseResult>();
        foreach (var c in cases)
        {
            var hits = await host.KnowledgeRetriever.SearchHybridAsync(c.Question, RecallK, cancellationToken: ct);
            var found = hits.Select(h => h.SourcePath).ToList();
            var rank = found.FindIndex(s => c.Sources.Contains(s)) + 1;
            var ok = rank > 0;
            results.Add(new CaseResult(c.Id, "rag", ok, ok ? "" : "veio: " + string.Join(", ", found.Distinct().Select(Path.GetFileNameWithoutExtension)),
                Rank: rank));
            log?.WriteLine($"rag {c.Id} {(ok ? "ok" : "falhou")}");
        }
        return results;
    }

    public async Task<IReadOnlyList<CaseResult>> PlanningAsync(IReadOnlyList<PlanningCase> cases, CancellationToken ct)
    {
        var results = new List<CaseResult>();
        foreach (var c in cases)
        {
            var run = await RunAsync(c.User, c.Request, ct);
            var ops = run.Result is PlannerProposals p && p.Items.Count > 0 ? OpsOf(p.Items[0].Draft.Json) : [];
            var ok = ScorePlanning(c.Ops, ops);
            results.Add(new CaseResult(c.Id, "planning", ok, ok ? "" : $"{Kind(run.Result)}; operações: [{string.Join(", ", ops)}]",
                run.InvalidPlans, run.FirstPlanValid, run.Tokens, run.Seconds));
            log?.WriteLine($"planning {c.Id} {(ok ? "ok" : "falhou")}");
        }
        return results;
    }

    public async Task<IReadOnlyList<CaseResult>> KnowledgeAsync(IReadOnlyList<KnowledgeCase> cases, CancellationToken ct)
    {
        var results = new List<CaseResult>();
        foreach (var c in cases)
        {
            var run = await RunAsync("op.joao", c.Question, ct);
            var text = run.Result switch { PlannerAnswer a => a.Text, PlannerProposals p => p.Text ?? "", _ => "" };
            var codes = c.Sources.Select(s => _codes.GetValueOrDefault(s, "")).Where(s => s.Length > 0).ToList();
            var (correct, cited) = ScoreAnswer(text, c.Keywords, codes);
            results.Add(new CaseResult(c.Id, "knowledge", correct && cited,
                correct && cited ? "" : (correct ? "" : "faltou palavra-chave; ") + (cited ? "" : "sem a fonte " + string.Join("/", codes)),
                Tokens: run.Tokens, Seconds: run.Seconds));
            log?.WriteLine($"knowledge {c.Id} {(correct && cited ? "ok" : "falhou")}");
        }
        return results;
    }

    public async Task<IReadOnlyList<CaseResult>> ScopeAsync(IReadOnlyList<OutOfScopeCase> outOfScope, IReadOnlyList<ProactiveCase> proactive, CancellationToken ct)
    {
        var results = new List<CaseResult>();
        foreach (var c in outOfScope)
        {
            var run = await RunAsync("op.joao", c.Request, ct);
            var ok = ScoreExpectation("refusal", run.Result);
            results.Add(new CaseResult(c.Id, "out_of_scope", ok, ok ? "" : Kind(run.Result), Tokens: run.Tokens, Seconds: run.Seconds));
            log?.WriteLine($"scope {c.Id} {(ok ? "ok" : "falhou")}");
        }
        foreach (var c in proactive)
        {
            var run = await RunAsync(c.User, c.Request, ct);
            var ok = ScoreExpectation(c.Expect, run.Result);
            results.Add(new CaseResult(c.Id, "proactive", ok, ok ? "" : $"esperado {c.Expect}, veio {Kind(run.Result)}",
                run.InvalidPlans, run.FirstPlanValid, run.Tokens, run.Seconds));
            log?.WriteLine($"proactive {c.Id} {(ok ? "ok" : "falhou")}");
        }
        return results;
    }

    // ---- scoring ------------------------------------------------------------------------------------------

    /// <summary>Every expected operation appears at least once among the operations of the first proposal.</summary>
    public static bool ScorePlanning(IReadOnlyList<string> expected, IReadOnlyList<string> actual) =>
        actual.Count > 0 && expected.All(actual.Contains);

    /// <summary>Correct: every keyword in the answer (case and accent insensitive). Cited: one of the document codes appears.</summary>
    public static (bool Correct, bool Cited) ScoreAnswer(string answer, IReadOnlyList<string> keywords, IReadOnlyList<string> codes)
    {
        var folded = TextFold.Fold(answer);
        var correct = answer.Length > 0 && keywords.All(k => folded.Contains(TextFold.Fold(k), StringComparison.Ordinal));
        var cited = codes.Any(code => folded.Contains(TextFold.Fold(code), StringComparison.Ordinal));
        return (correct, cited);
    }

    /// <summary>refusal: no plan was proposed; proposals: valid plans and no question; proposals_or_options: plans or a question with buttons.</summary>
    public static bool ScoreExpectation(string expect, PlannerResult result) => expect switch
    {
        "refusal" => result is not PlannerProposals,
        "proposals" => result is PlannerProposals { Items.Count: > 0 },
        "proposals_or_options" => result is PlannerProposals { Items.Count: > 0 } || result is PlannerAnswer { Options.Count: > 0 },
        _ => throw new ArgumentException("Unknown expectation " + expect, nameof(expect)),
    };

    public static IReadOnlyList<string> OpsOf(string planJson)
    {
        using var document = JsonDocument.Parse(planJson);
        return document.RootElement.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array
            ? steps.EnumerateArray().Select(s => s.TryGetProperty("op", out var op) ? op.GetString() ?? "" : "").ToList()
            : [];
    }

    private static string Kind(PlannerResult result) => result switch
    {
        PlannerProposals p => $"{p.Items.Count} proposta(s)",
        PlannerAnswer a => a.Options.Count > 0 ? "resposta com opções" : "resposta",
        PlannerEscalated => "escalado",
        PlannerFailed f => "falhou: " + f.Reason,
        _ => result.GetType().Name,
    };

    // ---- one request --------------------------------------------------------------------------------------

    private sealed record Run(PlannerResult Result, int InvalidPlans, bool FirstPlanValid, long Tokens, double Seconds);

    private sealed class NoProgress : IProgress<string>
    {
        public void Report(string value) { }
    }

    private async Task<Run> RunAsync(string username, string request, CancellationToken ct)
    {
        var user = await session(username);
        var auditMark = Scalar("SELECT COALESCE(MAX(id), 0) FROM audit_log");
        var usageMark = Scalar("SELECT COALESCE(MAX(id), 0) FROM llm_usage");
        var started = DateTimeOffset.UtcNow;
        PlannerResult result;
        try
        {
            (result, _) = await host.Planner.RunAsync(user, [], request, new NoProgress(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new PlannerFailed(ex.GetType().Name);
        }
        var seconds = (DateTimeOffset.UtcNow - started).TotalSeconds;
        var codes = Strings(
            "SELECT json_extract(details_json, '$.code') FROM audit_log WHERE id > $m AND action = 'ai.tool' " +
            "AND target = 'tool:propose_plan' AND json_valid(details_json) ORDER BY id", auditMark);
        var tokens = Scalar("SELECT COALESCE(SUM(prompt_tokens + completion_tokens), 0) FROM llm_usage WHERE id > $m", usageMark);
        return new Run(result, codes.Count(c => c == "plan_invalid"), codes.FirstOrDefault() == "plan_valid", tokens, seconds);
    }

    private long Scalar(string sql, long mark = 0)
    {
        using var connection = host.Database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$m", mark);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private List<string> Strings(string sql, long mark)
    {
        using var connection = host.Database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$m", mark);
        using var reader = command.ExecuteReader();
        var list = new List<string>();
        while (reader.Read()) list.Add(reader.IsDBNull(0) ? "" : reader.GetString(0));
        return list;
    }
}
