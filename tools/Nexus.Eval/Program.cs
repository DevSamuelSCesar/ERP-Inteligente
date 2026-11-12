using System.Text.Json;
using erp_inteligente;
using erp_inteligente.Core.Auth;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Security;
using erp_inteligente.Models;
using Nexus.Eval;

// Runs the evaluation sets of references/IA.md, section 5 (tools/eval) on a fresh database with the seed data.
// Keys come only from environment variables (never from arguments): DEEPSEEK_API_KEY, ANTHROPIC_API_KEY, OPENAI_API_KEY.
// They go to an in-memory key store of this process and are never written anywhere.
//
//   dotnet run --project tools/Nexus.Eval                                    # RAG recall@4, BM25 only (no key)
//   dotnet run --project tools/Nexus.Eval -- --embeddings local --port 8080  # hybrid, local embeddings server
//   dotnet run --project tools/Nexus.Eval -- --suites all --planner claude:claude-sonnet-5-5 --matcher claude:claude-haiku-5-5

var options = ParseArgs(args);
if (options is null)
{
    Console.Error.WriteLine("""
        Uso: Nexus.Eval [--suites rag,planning,knowledge,scope|all] [--planner provedor[:modelo]] [--matcher provedor[:modelo]]
                        [--embeddings off|openai|local] [--port N] [--limit N] [--label nome] [--out pasta]
        Chaves por variável de ambiente: DEEPSEEK_API_KEY, ANTHROPIC_API_KEY, OPENAI_API_KEY.
        """);
    return 2;
}

var evalDir = EvalSets.FindDirectory(AppContext.BaseDirectory);
var sets = EvalSets.Load(evalDir);
var store = new InMemorySecretStore();
var dbDir = Directory.CreateTempSubdirectory("nexus-eval-");
var database = DatabaseBootstrapper.OpenOrCreate(Path.Combine(dbDir.FullName, "nexus.db"), new KeyManager(store));
using var host = new AppHost(database, TimeProvider.System, Argon2Parameters.Default, AuthOptions.Default, store);
try
{
    foreach (var (provider, name) in new[] { (LlmProviderKind.DeepSeek, "DEEPSEEK_API_KEY"), (LlmProviderKind.Claude, "ANTHROPIC_API_KEY"), (LlmProviderKind.OpenAI, "OPENAI_API_KEY") })
        if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } key)
            host.LlmSettings.SetKey(provider, key);
    if (options.Port is { } port) host.LlmSettings.LocalPort = port;
    if (options.Planner is { } planner) host.LlmSettings.SetChoice(LlmUseRole.Planner, planner);
    if (options.Matcher is { } matcher) host.LlmSettings.SetChoice(LlmUseRole.Matcher, matcher);
    host.LlmSettings.EmbeddingChoice = options.Embeddings;

    var suites = options.Suites;
    var needsLlm = suites.Any(s => s != "rag");
    var plannerChoice = host.LlmSettings.ChoiceFor(LlmUseRole.Planner);
    if (needsLlm && !host.LlmSettings.IsConfiguredFor(plannerChoice.Provider))
    {
        Console.Error.WriteLine($"Sem chave para {LlmSettings.ProviderName(plannerChoice.Provider)}: defina a variável de ambiente do provedor.");
        return 3;
    }
    if (options.Embeddings is not null)
    {
        var sync = await host.VectorIndex.SyncAsync(CancellationToken.None);
        Console.WriteLine($"Vetores: {sync.Embedded} gerados" + (sync.Error is { } e ? $", falha {e}" : ""));
        if (sync.Error is not null) return 4;
    }

    var sessions = new Dictionary<string, UserSession>();
    async Task<UserSession> SessionFor(string username)
    {
        if (sessions.TryGetValue(username, out var cached)) return cached;
        var login = await host.Auth.LoginAsync(username, "demo");
        var session = login is LoginResult.Succeeded ok ? ok.Session : throw new InvalidOperationException("Login falhou: " + username);
        return sessions[username] = session;
    }

    var runner = new EvalRunner(host, SessionFor, Console.Out);
    var results = new List<CaseResult>();
    IReadOnlyList<T> Take<T>(IReadOnlyList<T> list) => options.Limit is { } n ? list.Take(n).ToList() : list;
    if (suites.Contains("rag")) results.AddRange(await runner.RagAsync(Take(sets.Knowledge), CancellationToken.None));
    if (suites.Contains("planning")) results.AddRange(await runner.PlanningAsync(Take(sets.Planning), CancellationToken.None));
    if (suites.Contains("knowledge")) results.AddRange(await runner.KnowledgeAsync(Take(sets.Knowledge), CancellationToken.None));
    if (suites.Contains("scope")) results.AddRange(await runner.ScopeAsync(Take(sets.OutOfScope), Take(sets.Proactive), CancellationToken.None));

    var label = options.Label ?? string.Join("+", new[]
    {
        needsLlm ? LlmSettings.EmbeddingModelId(plannerChoice) : null,
        options.Embeddings is { } emb ? LlmSettings.EmbeddingModelId(emb) : "bm25",
    }.Where(s => s is not null));
    var report = new EvalReport(label, DateTimeOffset.UtcNow, results);
    var outDir = options.Out ?? Path.Combine(evalDir, "results");
    Directory.CreateDirectory(outDir);
    var stem = Path.Combine(outDir, $"{report.At:yyyyMMdd-HHmm}-{string.Concat(label.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_'))}");
    await File.WriteAllTextAsync(stem + ".md", report.ToMarkdown());
    await File.WriteAllTextAsync(stem + ".json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine();
    Console.WriteLine(report.ToMarkdown());
    Console.WriteLine("Relatório: " + stem + ".md");
    return 0;
}
finally
{
    host.Dispose();
    try { dbDir.Delete(recursive: true); } catch (IOException) { }
}

static EvalOptions? ParseArgs(string[] args)
{
    var o = new EvalOptions();
    for (var i = 0; i < args.Length; i++)
    {
        string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException(args[i]);
        try
        {
            switch (args[i])
            {
                case "--suites":
                    var s = Next();
                    o.Suites = s == "all" ? ["rag", "planning", "knowledge", "scope"] : s.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    break;
                case "--planner": o.Planner = Choice(Next(), LlmUseRole.Planner); break;
                case "--matcher": o.Matcher = Choice(Next(), LlmUseRole.Matcher); break;
                case "--embeddings":
                    var e = Next();
                    o.Embeddings = e switch
                    {
                        "off" => null,
                        "openai" => new LlmChoice(LlmProviderKind.OpenAI, LlmSettings.OpenAiEmbeddingModel),
                        "local" => new LlmChoice(LlmProviderKind.Local, LlmSettings.LocalEmbeddingModel),
                        _ => throw new ArgumentException(e),
                    };
                    break;
                case "--port": o.Port = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--limit": o.Limit = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--label": o.Label = Next(); break;
                case "--out": o.Out = Next(); break;
                default: return null;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return null;
        }
    }
    return o.Suites.All(s => s is "rag" or "planning" or "knowledge" or "scope") ? o : null;
}

static LlmChoice Choice(string text, LlmUseRole role)
{
    var parts = text.Split(':', 2);
    var provider = Enum.Parse<LlmProviderKind>(parts[0], ignoreCase: true);
    var choice = new LlmChoice(provider, parts.Length > 1 ? parts[1] : LlmSettings.ModelsFor(provider)[role == LlmUseRole.Planner || provider == LlmProviderKind.Local ? 0 : ^1]);
    return LlmSettings.IsSupported(choice, role) ? choice : throw new ArgumentException(text);
}

sealed class EvalOptions
{
    public string[] Suites { get; set; } = ["rag"];
    public LlmChoice? Planner { get; set; }
    public LlmChoice? Matcher { get; set; }
    public LlmChoice? Embeddings { get; set; }
    public int? Port { get; set; }
    public int? Limit { get; set; }
    public string? Label { get; set; }
    public string? Out { get; set; }
}
