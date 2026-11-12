using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Engine;
using erp_inteligente.Core.Knowledge;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Memory;
using erp_inteligente.Core.Policy;
using erp_inteligente.Core.Recipes;
using erp_inteligente.Core.Skills;
using erp_inteligente.Models;

namespace erp_inteligente.Core.Planner;

/// <summary>
/// The Planner (references/ARQUITETURA.md, sections 1, 4 and 12.8): talks to the LLM with read tools,
/// <c>propose_plan</c> and <c>offer_options</c>. The model reads data through validated, parameterized queries on the
/// read-only AI connection and can only propose plans (up to <see cref="PlannerOptions.MaxProposals"/> alternatives), which
/// the deterministic engine validates and simulates (<c>Preview</c>, nothing is written).
/// The model is an untrusted user: every tool argument is validated, every call is audited, and tool results are
/// wrapped as reference data.
/// </summary>
public sealed partial class Planner(
    ILlmProvider llm,
    LlmSettings settings,
    NexusDatabase database,
    PolicyEngine policy,
    IAuditLog audit,
    PlanEngine engine,
    PlannerOptions? options = null,
    TimeProvider? time = null,
    SkillCatalog? skills = null,
    MemoryStore? memories = null,
    MemoryRetriever? memoryRetriever = null,
    KnowledgeRetriever? knowledge = null,
    RecipeMatcher? recipeMatcher = null,
    Operations.LlmUsageLog? usage = null,
    MemoryFeedback? feedback = null)
{
    /// <summary>One <c>llm_usage</c> row per request that called the planning model (feature 9).</summary>
    private void RecordUsage(UserSession user, RunState state, PlannerResult result)
    {
        if (usage is null || state.Calls == 0) return;
        var kind = result switch
        {
            PlannerAnswer => "answer",
            PlannerProposals => "proposals",
            PlannerEscalated => "escalated",
            _ => "failed",
        };
        usage.Record(user, new Operations.LlmUsageEntry(LlmUseRole.Planner, settings.ChoiceFor(LlmUseRole.Planner),
            state.Calls, state.PromptTokens, state.CompletionTokens, kind)
        {
            CacheReadTokens = state.CacheReadTokens,
            CacheWriteTokens = state.CacheWriteTokens,
        });
    }

    internal const string ProposePlan = "propose_plan";
    internal const string OfferOptions = "offer_options";
    internal const string ProposeSkill = "propose_skill";
    internal const int MaxSkillLoads = 3;
    internal const int MaxSkillReasonLength = 300;
    private const int MaxIndexDescriptionLength = 300;
    internal const int MaxTitleLength = 80;
    internal const int MaxRationaleLength = 300;
    internal const int MaxAssumptions = 8;
    internal const int MaxAssumptionLength = 200;
    internal const int MaxOptions = 4;
    internal const int MaxOptionLabelLength = 60;
    internal const int MaxOptionRequestLength = 300;
    internal const int MaxRequestLength = 2000;
    internal const int MaxAnswerLength = 1500;
    private const int MaxToolCallsPerRound = 8;
    private const int MaxReportedErrors = 10;
    private const int MaxAuditArgsLength = 200;
    internal const int MaxMemoriesPerRequest = 2;
    internal const int MaxContextChars = 2500;
    private const int MaxContextMemories = 5;
    private const int MaxContextMemoryChars = 1200;
    private const int MaxContextChunks = 2;
    private const int MaxContextChunkChars = 800;
    private const int MinContextChunkChars = 120;
    /// <summary>Minimum BM25 score (-bm25, heading weighted 2x) for a procedure chunk to be injected on its own; calibrated on the 22 embedded documents.</summary>
    internal const double MinContextKnowledgeScore = 3.0;
    private const int MinContextTerms = 2;
    private const double MinContextMemoryConfidence = 0.3;
    private const string PromptResource = "Nexus.Planner.system.md";

    private readonly PlannerOptions _options = options ?? PlannerOptions.Default;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>What one run accumulates: valid proposals, suggested options and the count of rejected attempts.</summary>
    private sealed class RunState
    {
        public List<ProposalItem> Proposals { get; } = [];
        public List<PlannerOption> Options { get; set; } = [];
        public int Invalid { get; set; }
        public HashSet<string> PlanJsons { get; } = new(StringComparer.Ordinal);
        public List<SkillContent> LoadedSkills { get; } = [];
        public SkillProposal? Skill { get; set; }
        public List<RememberedMemory> Remembered { get; } = [];
        public List<SuspiciousContent> Suspicious { get; } = [];

        /// <summary>Memories the model had in front of it (automatic context and search_memories): recorded with its plans.</summary>
        public HashSet<long> MemoryIds { get; } = [];
        public bool HasResult => Proposals.Count > 0 || Skill is not null;

        /// <summary>Calls and tokens of the planning model in this request (feature 9: cost panel).</summary>
        public int Calls { get; set; }
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int CacheReadTokens { get; set; }
        public int CacheWriteTokens { get; set; }

        public void Count(LlmResponse response)
        {
            PromptTokens += Math.Max(0, response.Usage.PromptTokens);
            CompletionTokens += Math.Max(0, response.Usage.CompletionTokens);
            CacheReadTokens += Math.Max(0, response.Usage.CacheReadTokens);
            CacheWriteTokens += Math.Max(0, response.Usage.CacheWriteTokens);
        }
    }

    public Task<(PlannerResult Result, PlannerTurn? Turn)> RunAsync(
        UserSession user, IReadOnlyList<PlannerTurn> history, string request, IProgress<string> status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(status);
        return RunCoreAsync(user, history, request.Trim(), status, cancellationToken);
    }

    private async Task<(PlannerResult Result, PlannerTurn? Turn)> RunCoreAsync(
        UserSession user, IReadOnlyList<PlannerTurn> history, string request, IProgress<string> status, CancellationToken ct)
    {
        if (request.Length == 0)
            return (new PlannerFailed("Escreva um pedido."), null);
        if (request.Length > MaxRequestLength)
            return (new PlannerFailed($"O pedido passa de {MaxRequestLength} caracteres."), null);

        var rights = await Task.Run(() => policy.EffectiveForAi(user), ct);
        var aiUserId = await Task.Run(FindAiUser, ct);
        if (!rights.Contains(Permissions.StockRead))
        {
            Append(aiUserId, user, "ai.query", "permission:" + Permissions.StockRead, AuditOutcome.Denied, null);
            return (new PlannerFailed("Seu papel não permite consultas pelo Nexus."), null);
        }

        var operations = OperationCatalog.V1.Where(o => rights.Contains(o.Permission)).ToList();
        var canPropose = rights.Contains(Permissions.PlanSubmit) && operations.Count > 0;
        var tools = PlannerTools.ReadTools().ToList();
        if (skills is not null) tools.Add(PlannerTools.LoadSkillTool());
        // The AI role is denied skill.manage on purpose (the AI never saves its own instructions). The tool is gated on the
        // REQUESTING USER's right, not on the AI intersection: the AI only drafts, and the human saves with their own rights
        // when they confirm.
        var canDraftSkill = skills is not null && await Task.Run(() => policy.IsAllowed(user, Permissions.SkillManage), ct);
        if (canPropose) tools.Add(BuildProposeTool(operations));
        if (canDraftSkill) tools.Add(BuildProposeSkillTool());
        tools.Add(BuildOfferOptionsTool());
        var canSearchMemories = memoryRetriever is not null;
        var canSearchKnowledge = knowledge is not null;
        var canRemember = memories is not null && rights.Contains(Permissions.MemoryWrite);
        if (canSearchKnowledge) tools.Add(PlannerTools.DefinitionTool(PlannerTools.SearchKnowledge));
        if (canSearchMemories) tools.Add(PlannerTools.DefinitionTool(PlannerTools.SearchMemories));
        if (canRemember) tools.Add(PlannerTools.RememberTool());
        if (recipeMatcher is not null) tools.Add(PlannerTools.DefinitionTool(PlannerTools.SearchRecipes));

        var state = new RunState();
        var suspiciousRequest = await Task.Run(() => ScanRequest(aiUserId, user, request), ct);

        // Recipe first (references/ARQUITETURA.md, section 4, step 2): a match skips planning by the strong model. A request
        // that looks like an injection never takes this shortcut; it gets the full Planner and its defenses.
        if (recipeMatcher is not null && canPropose && !suspiciousRequest)
        {
            status.Report("Procurando receita");
            if (await TryRecipeAsync(user, aiUserId, rights, request, ct) is { } recipeResult)
                return recipeResult;
        }

        var messages = new List<LlmMessage> { LlmMessage.System(BuildSystemPrompt(user, operations, rights, canDraftSkill, canRemember, canSearchKnowledge)) };
        foreach (var turn in history.TakeLast(_options.MaxHistoryTurns))
        {
            if (turn.Reply.Role != LlmRole.Assistant) continue;
            messages.Add(LlmMessage.User(turn.Request));
            messages.Add(LlmMessage.Assistant(turn.Reply.Content, null, turn.Reply.ReasoningContent));
        }
        // Identifiers (SKU, address, lot, quantity) say what to do it with, not which procedure applies: they are left out
        // of the topic and at least two distinct words must match, so a stray common word never injects a chunk. With
        // embeddings on, the topic is also embedded (masked) and a chunk may enter by meaning above the context threshold.
        IReadOnlyList<KnowledgeHit> contextChunks = [];
        if (knowledge is not null)
        {
            try
            {
                contextChunks = await knowledge.SearchHybridAsync(Identifiers().Replace(request, " "), MaxContextChunks,
                    MinContextKnowledgeScore, MinContextTerms, forContext: true, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Trace.TraceWarning("Planner: knowledge search failed: " + ex.GetType().Name);
            }
        }
        var context = await Task.Run(() => BuildContext(user, aiUserId, state, request, contextChunks), ct);
        messages.Add(LlmMessage.User(context + request));

        var tokens = 0;
        status.Report("Planejando");

        // Every result carries what the injection scan found, whatever the way out, and its token usage is recorded.
        (PlannerResult, PlannerTurn?) Flag((PlannerResult Result, PlannerTurn? Turn) outcome)
        {
            RecordUsage(user, state, outcome.Result);
            return (outcome.Result with { Suspicious = state.Suspicious.ToArray() }, outcome.Turn);
        }

        // With valid proposals in hand a limit never throws them away: the operator gets what was prepared.
        (PlannerResult, PlannerTurn?) Limit(PlannerEscalated escalated) =>
            Flag(state.HasResult ? Conclude(request, state, null, null) : (escalated, null));

        for (var round = 1; round <= _options.MaxRounds; round++)
        {
            if (tokens >= _options.MaxTokensPerRequest)
                return Limit(BudgetExceeded());

            LlmResponse response;
            try
            {
                state.Calls++;
                response = await llm.CompleteAsync(
                    PlannerRequest(messages.ToArray(), tools), ct);
                state.Count(response);
            }
            catch (LlmException ex)
            {
                if (state.HasResult) return Flag(Conclude(request, state, null, null));
                return Flag((new PlannerFailed(MessageFor(ex.Kind)), null));
            }
            tokens += Math.Max(0, response.Usage.PromptTokens) + Math.Max(0, response.Usage.CompletionTokens);

            if (response.ToolCalls.Count == 0)
            {
                var text = CleanText(response.Content);
                if (state.HasResult)
                    return Flag(Conclude(request, state, text.Length > 0 ? text : null, response.ReasoningContent));
                if (text.Length == 0 && state.Options.Count > 0)
                    text = "Como prefere seguir?";
                if (text.Length == 0)
                    return Flag(response.FinishReason == LlmFinishReason.Length
                        ? (new PlannerEscalated("A resposta ficou longa demais para ser concluída. Faça um pedido mais específico."), null)
                        : (new PlannerFailed("O Nexus não devolveu uma resposta válida. Tente de novo."), null));
                return Flag((new PlannerAnswer(text, state.Options.ToArray()) { Remembered = state.Remembered.ToArray() }, Remember(request, text, response.ReasoningContent)));
            }

            messages.Add(LlmMessage.Assistant(response));
            var index = 0;
            foreach (var call in response.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();
                if (++index > MaxToolCallsPerRound)
                {
                    messages.Add(LlmMessage.ToolResult(call.Id, Wrap("tool:" + SafeToolName(call.Name), PlannerTools.Error("too_many_calls", "Chamadas demais de uma vez.").Json)));
                    continue;
                }

                var outcome = await Task.Run(() => RunTool(user, aiUserId, rights, canPropose, canDraftSkill, canRemember, state, call, status), ct);
                if (call.Name == ProposePlan && !outcome.Allowed && outcome.Code != "too_many_proposals") state.Invalid++;
                if (call.Name == ProposeSkill && !outcome.Allowed && outcome.Code is not ("too_many_skill_proposals" or "not_allowed")) state.Invalid++;
                var source = "tool:" + SafeToolName(call.Name);
                // Only data read from outside the model is scanned: the Planner's own messages and the model's echoed
                // arguments (a plan error) are not external content, and an approved skill is an instruction by design.
                var suspicious = outcome.Allowed && !outcome.Raw && call.Name is { } toolName && ScansResult(toolName)
                    && (await Task.Run(() => ScanToolResult(aiUserId, user, state, source, outcome.Json), ct)).Count > 0;
                messages.Add(LlmMessage.ToolResult(call.Id, outcome.Raw ? outcome.Json : Wrap(source, outcome.Json, suspicious)));
            }

            if (state.Proposals.Count >= _options.MaxProposals)
                return Flag(await FinalTextAsync(user, request, state, messages, tokens, ct));

            if (state.Invalid >= _options.MaxInvalidPlans)
                return Limit(new PlannerEscalated(
                    $"Não consegui montar um plano válido em {_options.MaxInvalidPlans} tentativas. " +
                    "Descreva o pedido com mais detalhes (SKU, quantidade e endereços) ou use a tela \"Novo plano\"."));
        }

        return Limit(new PlannerEscalated(
            "O pedido exigiu consultas demais e não foi concluído. Tente dividi-lo em partes menores."));
    }

    /// <summary>The proposal limit was reached: one more round without tools asks the model for the closing text.</summary>
    private async Task<(PlannerResult, PlannerTurn?)> FinalTextAsync(
        UserSession user, string request, RunState state, List<LlmMessage> messages, int tokens, CancellationToken ct)
    {
        string? text = null;
        string? reasoning = null;
        if (tokens < _options.MaxTokensPerRequest)
        {
            try
            {
                state.Calls++;
                var response = await llm.CompleteAsync(
                    PlannerRequest(messages.ToArray(), []), ct);
                state.Count(response);
                if (response.ToolCalls.Count == 0 && CleanText(response.Content) is { Length: > 0 } cleaned)
                {
                    text = cleaned;
                    reasoning = response.ReasoningContent;
                }
            }
            catch (LlmException)
            {
                // The proposals are already valid; the default text is enough.
            }
        }
        return Conclude(request, state, text, reasoning);
    }

    private static (PlannerResult, PlannerTurn?) Conclude(string request, RunState state, string? text, string? reasoning)
    {
        var skill = state.Skill;
        var shown = text ?? (state.Proposals.Count switch
        {
            0 => $"Montei a skill {skill?.Document.Name} para a sua confirmação.",
            1 => "Montei um plano para a sua confirmação.",
            _ => $"Montei {state.Proposals.Count} alternativas de plano para a sua escolha.",
        });
        var memory = shown;
        if (state.Proposals.Count > 0) memory += " Planos: " + string.Join("; ", state.Proposals.Select(p => p.Title)) + ".";
        if (skill is not null) memory += " Skill proposta: " + skill.Document.Name + ".";
        if (memory.Length > MaxAnswerLength) memory = memory[..MaxAnswerLength];

        PlannerResult result = state.Proposals.Count > 0
            ? new PlannerProposals(state.Proposals.ToArray(), shown, state.Options.ToArray(), skill) { Remembered = state.Remembered.ToArray() }
            : new PlannerAnswer(shown, state.Options.ToArray(), skill) { Remembered = state.Remembered.ToArray() };
        return (result, Remember(request, memory, reasoning));
    }

    // ---- tools ---------------------------------------------------------------------------------------------

    private ToolOutcome RunTool(
        UserSession user, long? aiUserId, IReadOnlySet<string> rights, bool canPropose, bool canDraftSkill, bool canRemember, RunState state,
        LlmToolCall call, IProgress<string> status)
    {
        var name = call.Name ?? string.Empty;
        try
        {
            if (name == ProposePlan)
            {
                if (!canPropose)
                    return Finish(PlannerTools.Error("not_allowed", "Você não tem permissão para propor planos."));
                return ProposePlanTool(user, aiUserId, rights, state, call, status);
            }
            if (name == ProposeSkill)
            {
                if (!canDraftSkill)
                    return Finish(PlannerTools.Error("not_allowed", "Você não tem permissão para propor skills."));
                return ProposeSkillTool(user, aiUserId, state, call, status);
            }
            if (name == PlannerTools.LoadSkill)
            {
                if (skills is null)
                    return Finish(PlannerTools.Error("unknown_tool", "Ferramenta desconhecida."), "unknown");
                if (!rights.Contains(Permissions.StockRead))
                    return Finish(PlannerTools.Error("not_allowed", "Sem permissão de consulta."));
                var bad = PlannerTools.Validate(name, call.ArgumentsJson, out var skillArgs);
                if (bad is not null) return Finish(bad);
                status.Report(PlannerTools.StatusFor(name, skillArgs));
                return Finish(LoadSkillTool(rights, state, skillArgs.GetProperty("nome").GetString()!));
            }
            if (name == OfferOptions)
                return Finish(OfferOptionsTool(state, call));
            if (name == PlannerTools.Remember)
            {
                if (!canRemember)
                    return Finish(PlannerTools.Error("not_allowed", "Você não tem permissão para registrar memórias."));
                return Finish(RememberTool(user, state, call, status));
            }
            if ((name == PlannerTools.SearchKnowledge && knowledge is null) || (name == PlannerTools.SearchMemories && memoryRetriever is null)
                || (name == PlannerTools.SearchRecipes && recipeMatcher is null))
                return Finish(PlannerTools.Error("unknown_tool", "Ferramenta desconhecida."), "unknown");
            if (!PlannerTools.IsReadTool(name))
                return Finish(PlannerTools.Error("unknown_tool", "Ferramenta desconhecida."), "unknown");
            if (!rights.Contains(Permissions.StockRead))
                return Finish(PlannerTools.Error("not_allowed", "Sem permissão de consulta."));

            var invalid = PlannerTools.Validate(name, call.ArgumentsJson, out var args);
            if (invalid is not null) return Finish(invalid);

            status.Report(PlannerTools.StatusFor(name, args));
            if (name == PlannerTools.SearchKnowledge) return Finish(PlannerTools.ExecuteSearchKnowledge(knowledge!, args));
            if (name == PlannerTools.SearchMemories)
            {
                var found = PlannerTools.ExecuteSearchMemories(memoryRetriever!, user, args);
                state.MemoryIds.UnionWith(MemoryIdsOf(found.Json));
                return Finish(found);
            }
            if (name == PlannerTools.SearchRecipes) return Finish(PlannerTools.ExecuteSearchRecipes(database, args));
            return Finish(PlannerTools.Execute(database, name, args, user.WarehouseId));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The model must never see exception text (paths, SQL, keys).
            var failed = PlannerTools.Error("tool_failed", "A consulta falhou. Tente outra ou responda sem ela.");
            Append(aiUserId, user, "ai.tool", "tool:" + SafeToolName(name), AuditOutcome.Failed, Details(call, "tool_failed"));
            return failed;
        }

        ToolOutcome Finish(ToolOutcome outcome, string? targetName = null)
        {
            Append(aiUserId, user, "ai.tool", "tool:" + (targetName ?? SafeToolName(name)),
                outcome.Allowed ? AuditOutcome.Allowed : AuditOutcome.Denied, Details(call, outcome.Code));
            return outcome;
        }
    }

    /// <summary>
    /// Saves a memory through <see cref="MemoryStore"/> (the filter and the audit live there). Audited here with the outcome
    /// code only: the content never goes to the <c>ai.tool</c> row. A refusal counts nothing and is not an error.
    /// </summary>
    private ToolOutcome RememberTool(UserSession user, RunState state, LlmToolCall call, IProgress<string> status)
    {
        var invalid = PlannerTools.Validate(PlannerTools.Remember, call.ArgumentsJson, out var args);
        if (invalid is not null) return invalid;
        if (state.Remembered.Count >= MaxMemoriesPerRequest)
            return PlannerTools.Error("too_many_memories", $"Já foram registradas {MaxMemoriesPerRequest} memórias neste pedido. Siga sem registrar mais.");

        string Get(string key) => args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;
        status.Report(PlannerTools.StatusFor(PlannerTools.Remember, args));
        var reference = Get("scope_ref");
        MemoryWriteResult result;
        try
        {
            result = memories!.Remember(user, MemoryDb.KindFromDb(Get("kind")), MemoryDb.ScopeFromDb(Get("scope")),
                reference.Length == 0 ? null : reference, Get("content"), MemoryOrigin.Ai);
        }
        catch (PolicyDeniedException)
        {
            return PlannerTools.Error("not_allowed", "Você não tem permissão para registrar memórias.");
        }

        if (!result.Saved)
        {
            var refused = new JsonObject { ["status"] = "refused", ["reason"] = result.Reason ?? "A memória foi recusada." };
            return new ToolOutcome(PlannerTools.Serialize(refused), false, "memory_refused");
        }
        state.Remembered.Add(new RememberedMemory(result.Id, result.Content, result.Reinforced));
        var saved = new JsonObject { ["status"] = "saved", ["reinforced"] = result.Reinforced };
        return new ToolOutcome(PlannerTools.Serialize(saved), true, "memory_saved");
    }

    /// <summary>
    /// The automatic context of a request: the best memories and procedure chunks, as reference data placed in front of the
    /// current user message (never in the system prompt or the history). Nothing here may stop the request.
    /// </summary>
    private string BuildContext(UserSession user, long? aiUserId, RunState state, string request, IReadOnlyList<KnowledgeHit> chunks)
    {
        if (memoryRetriever is null && knowledge is null) return string.Empty;
        try
        {
            var memoryRows = new List<MemoryHit>();
            if (memoryRetriever is not null)
                memoryRows.AddRange(memoryRetriever.Search(user, request, new MemorySearchOptions(MaxContextMemories, MinContextMemoryConfidence)));
            if (memoryRows.Count == 0 && chunks.Count == 0) return string.Empty;

            var builder = new StringBuilder();
            var usedMemories = new List<long>();
            if (memoryRows.Count > 0)
            {
                var kept = memoryRows.ToList();
                string block;
                while (true)
                {
                    block = Wrap("memorias", PlannerTools.Serialize(new JsonArray(kept.Select(m => (JsonNode?)PlannerTools.MemoryRow(m)).ToArray())));
                    if (block.Length <= MaxContextMemoryChars || kept.Count <= 1) break;
                    kept.RemoveAt(kept.Count - 1);
                }
                if (block.Length <= MaxContextMemoryChars)
                {
                    // Every memory is scanned (no short-circuit), so each suspicious one is audited.
                    var flagged = kept.Select(m => ScanBlock(aiUserId, user, state, "memorias", "id:" + m.Id, m.Content).Count > 0).ToList();
                    if (flagged.Contains(true))
                        block = block.Replace("<dados fonte=\"memorias\">", $"<dados fonte=\"memorias\" {SuspiciousAttribute}>", StringComparison.Ordinal);
                    builder.Append(block).Append('\n');
                    usedMemories.AddRange(kept.Select(m => m.Id));
                    state.MemoryIds.UnionWith(usedMemories);
                }
            }
            var usedPaths = new List<string>();
            foreach (var chunk in chunks)
            {
                var source = "knowledge:" + new string(chunk.SourcePath.Where(c => c is not ('"' or '<' or '>' or '\n' or '\r')).ToArray())
                    + "#" + new string(chunk.Heading.Where(c => c is not ('"' or '<' or '>' or '\n' or '\r')).ToArray());
                var overhead = "<dados fonte=\"\" >".Length + SuspiciousAttribute.Length + source.Length + "</dados>\n".Length;
                var room = Math.Min(MaxContextChunkChars, MaxContextChars - builder.Length - overhead);
                if (room < MinContextChunkChars) break;
                var shortened = PlannerTools.Shorten(chunk.Content.Trim(), room);
                var suspicious = ScanBlock(aiUserId, user, state, source, null, shortened).Count > 0;
                var content = shortened.Replace("<", "\\u003c", StringComparison.Ordinal).Replace(">", "\\u003e", StringComparison.Ordinal);
                builder.Append(Wrap(source, content, suspicious)).Append('\n');
                if (!usedPaths.Contains(chunk.SourcePath, StringComparer.Ordinal)) usedPaths.Add(chunk.SourcePath);
            }
            if (builder.Length == 0) return string.Empty;

            if (usedMemories.Count > 0)
            {
                try { memories?.MarkUsed(user, usedMemories); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    System.Diagnostics.Trace.TraceWarning("Planner: marking memories as used failed: " + ex.GetType().Name);
                }
            }
            var details = new JsonObject
            {
                ["memories"] = new JsonArray(usedMemories.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["knowledge"] = new JsonArray(usedPaths.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
            };
            Append(aiUserId, user, "ai.context", "context", AuditOutcome.Allowed, details.ToJsonString());
            return builder.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning("Planner: building the context failed: " + ex.GetType().Name);
            return string.Empty;
        }
    }

    private ToolOutcome ProposePlanTool(
        UserSession user, long? aiUserId, IReadOnlySet<string> rights, RunState state, LlmToolCall call, IProgress<string> status)
    {
        ToolOutcome Reject(ToolOutcome outcome)
        {
            Append(aiUserId, user, "ai.tool", "tool:" + ProposePlan, AuditOutcome.Denied, Details(call, outcome.Code));
            return outcome;
        }

        if (state.Proposals.Count >= _options.MaxProposals)
            return Reject(PlannerTools.Error("too_many_proposals", $"Já há {_options.MaxProposals} planos válidos. Encerre com um texto curto."));

        if (!TryParseObject(call.ArgumentsJson, out var root))
            return Reject(PlannerTools.Error("invalid_arguments", "Os argumentos não são um objeto JSON válido."));

        var bad = new List<string>();
        var title = CleanField(root, "title", MaxTitleLength, bad);
        var rationale = CleanField(root, "rationale", MaxRationaleLength, bad);
        var assumptions = new List<string>();
        if (root.TryGetPropertyValue("assumptions", out var rawAssumptions) && rawAssumptions is not null)
        {
            if (rawAssumptions is not JsonArray list || list.Count > MaxAssumptions)
                bad.Add("assumptions");
            else
                foreach (var item in list)
                {
                    var one = item is JsonValue v && v.TryGetValue<string>(out var t) ? CleanInline(t) : null;
                    if (string.IsNullOrEmpty(one) || one.Length > MaxAssumptionLength) { bad.Add("assumptions"); break; }
                    assumptions.Add(one);
                }
        }
        var planJson = ExtractPlan(root);
        if (planJson is null) bad.Add("plan");
        if (bad.Count > 0 || title is null || rationale is null || planJson is null)
            return Reject(PlannerTools.Error("invalid_arguments",
                "Argumentos inválidos: " + string.Join(", ", bad.Distinct()) + ". Envie title (1 a 80), rationale (1 a 300), " +
                "assumptions (até 8 textos de até 200) e plan {dsl_version, intent, steps}."));
        planJson = WithSkill(planJson, state, rights);
        if (!state.PlanJsons.Add(planJson))
            return Reject(PlannerTools.Error("duplicate_plan", "Este plano já foi proposto. Proponha uma alternativa diferente ou encerre com texto."));

        status.Report("Validando o plano");
        EnginePreview preview;
        try
        {
            preview = engine.Preview(user, planJson, "ai");
        }
        catch (PolicyDeniedException)
        {
            return Reject(PlannerTools.Error("not_allowed", "Você não tem permissão para propor planos."));
        }

        if (preview.Errors.Count > 0 || preview.Document is null)
        {
            var errors = new JsonArray();
            foreach (var e in preview.Errors.Take(MaxReportedErrors))
                errors.Add(new JsonObject
                {
                    ["step"] = e.StepIndex,
                    ["field"] = e.Field,
                    ["code"] = e.Code,
                    ["message"] = e.Message,
                });
            var json = PlannerTools.Serialize(new JsonObject { ["status"] = "invalid", ["errors"] = errors });
            // The codes go to the audit row: recurring ones become pattern memories (references/ARQUITETURA.md, section 8).
            Append(aiUserId, user, "ai.tool", "tool:" + ProposePlan, AuditOutcome.Denied,
                Details(call, "plan_invalid", preview.Errors.Select(e => e.Code).Distinct().Take(MaxReportedErrors)));
            feedback?.ObserveErrors(user, preview.Document?.Skill ?? SkillOfPlan(planJson), preview.Errors);
            return new ToolOutcome(json, false, "plan_invalid");
        }

        Append(aiUserId, user, "ai.tool", "tool:" + ProposePlan, AuditOutcome.Allowed, Details(call, "plan_valid"));
        state.Proposals.Add(new ProposalItem(title, rationale, assumptions, PlanDraft.FromJson(planJson, PlanOrigin.Ai, memoryIds: state.MemoryIds.ToArray()), preview));
        var summary = new JsonObject { ["status"] = "valid", ["proposals"] = state.Proposals.Count, ["risk"] = preview.Risk.ToString().ToLowerInvariant() };
        return new ToolOutcome(PlannerTools.Serialize(summary), true, "plan_valid");
    }

    /// <summary>
    /// Sets the plan's <c>skill</c> field (the model's choice is only a hint): a skill loaded in this run wins, then a known
    /// skill the model named, then the deterministic mapping from the first step's operation.
    /// </summary>
    private string WithSkill(string planJson, RunState state, IReadOnlySet<string> rights)
    {
        if (TryParseObject(planJson, out var plan)
            && plan["steps"] is JsonArray { Count: > 0 } steps && steps[0] is JsonObject first
            && first["op"] is JsonValue opValue && opValue.TryGetValue<string>(out var op) && OperationCatalog.TryGet(op, out _))
        {
            var given = plan["skill"] is JsonValue v && v.TryGetValue<string>(out var g) && SkillDocument.IsValidName(g) ? g : null;
            string? chosen;
            if (given is not null && state.LoadedSkills.Any(l => l.Name == given)) chosen = given;
            else if (state.LoadedSkills.FirstOrDefault(l => l.Operations.Contains(op)) is { } loaded) chosen = loaded.Name;
            else if (given is not null && skills?.Load(given, rights) is not null) chosen = given;
            else chosen = SkillMapping.ForOperation(op);
            plan["skill"] = chosen;
            return plan.ToJsonString();
        }
        return planJson;
    }

    /// <summary>
    /// Returns the approved body of a skill as an instruction (not as <c>&lt;dados&gt;</c>): the prompt tells the model its
    /// limits. '&lt;' and '&gt;' are escaped so the body can never close the surrounding tag.
    /// </summary>
    private ToolOutcome LoadSkillTool(IReadOnlySet<string> rights, RunState state, string name)
    {
        if (state.LoadedSkills.Any(l => l.Name == name))
            return new ToolOutcome("""{"status":"already_loaded"}""", true, "already_loaded");
        if (state.LoadedSkills.Count >= MaxSkillLoads)
            return PlannerTools.Error("too_many_skills", $"Já foram carregadas {MaxSkillLoads} skills neste pedido. Siga com elas.");

        var content = skills!.Load(name, rights);
        if (content is null)
            return PlannerTools.Error("skill_not_found", "Skill não encontrada. Use um nome do índice.");
        state.LoadedSkills.Add(content);
        var body = content.Body.Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
        var text = $"<skill nome=\"{content.Name}\" versao=\"{content.Version}\">\n{body}\n</skill>";
        return new ToolOutcome(text, true, "skill_loaded", Raw: true);
    }

    private ToolOutcome ProposeSkillTool(UserSession user, long? aiUserId, RunState state, LlmToolCall call, IProgress<string> status)
    {
        // Audited with the outcome code only: the body of a skill never goes to the audit log.
        ToolOutcome Reject(ToolOutcome outcome)
        {
            Append(aiUserId, user, "ai.tool", "tool:" + ProposeSkill, AuditOutcome.Denied, Details(call, outcome.Code));
            return outcome;
        }

        if (state.Skill is not null)
            return Reject(PlannerTools.Error("too_many_skill_proposals", "Já há uma skill proposta. Encerre com um texto curto."));
        if (!TryParseObject(call.ArgumentsJson, out var root))
            return Reject(PlannerTools.Error("invalid_arguments", "Os argumentos não são um objeto JSON válido."));

        var issues = new List<SkillIssue>();
        foreach (var key in root.Select(p => p.Key))
            if (key is not ("nome" or "descricao" or "operacoes" or "corpo" or "motivo"))
            {
                issues.Add(new SkillIssue("argumentos", "Há campos desconhecidos. Envie só nome, descricao, operacoes, corpo e motivo."));
                break;
            }
        string? Text(string field)
        {
            if (root[field] is JsonValue v && v.TryGetValue<string>(out var t)) return t;
            issues.Add(new SkillIssue(field, "Campo obrigatório (texto)."));
            return null;
        }
        var nome = Text("nome");
        var descricao = Text("descricao");
        var corpo = Text("corpo");
        var motivoRaw = Text("motivo");
        var operations = new List<string>();
        if (root["operacoes"] is JsonArray list && list.Count <= SkillDocument.MaxOperations)
        {
            foreach (var item in list)
            {
                if (item is JsonValue v && v.TryGetValue<string>(out var op)) operations.Add(op);
                else { issues.Add(new SkillIssue("operacoes", "Use uma lista de nomes de operações (texto).")); break; }
            }
        }
        else
            issues.Add(new SkillIssue("operacoes", $"Envie uma lista com até {SkillDocument.MaxOperations} operações."));
        var motivo = CleanInline(motivoRaw);
        if (motivoRaw is not null && (motivo.Length == 0 || motivo.Length > MaxSkillReasonLength))
            issues.Add(new SkillIssue("motivo", $"O motivo deve ter de 1 a {MaxSkillReasonLength} caracteres."));

        SkillDocument? document = null;
        if (issues.Count == 0)
        {
            document = new SkillDocument(nome!, descricao!, operations, corpo!).Normalized();
            issues.AddRange(document.Validate());
        }
        if (issues.Count > 0 || document is null)
        {
            var errors = new JsonArray();
            foreach (var issue in issues.Take(MaxReportedErrors))
                errors.Add(new JsonObject { ["field"] = issue.Field, ["message"] = issue.Message });
            var json = PlannerTools.Serialize(new JsonObject { ["status"] = "invalid", ["errors"] = errors });
            return Reject(new ToolOutcome(json, false, "skill_invalid"));
        }

        status.Report("Validando a skill");
        var current = skills!.Load(document.Name)?.Version;
        state.Skill = new SkillProposal(document, motivo, current);
        Append(aiUserId, user, "ai.tool", "tool:" + ProposeSkill, AuditOutcome.Allowed, Details(call, "skill_valid"));
        return new ToolOutcome("""{"status":"valid"}""", true, "skill_valid");
    }

    private static ToolOutcome OfferOptionsTool(RunState state, LlmToolCall call)
    {
        const string Hint = "Envie options: 1 a 4 itens {label (até 60), request (até 300)}.";
        if (!TryParseObject(call.ArgumentsJson, out var root) || root["options"] is not JsonArray list || list.Count is < 1 or > MaxOptions)
            return PlannerTools.Error("invalid_arguments", Hint);
        var options = new List<PlannerOption>();
        foreach (var item in list)
        {
            if (item is not JsonObject obj)
                return PlannerTools.Error("invalid_arguments", Hint);
            var bad = new List<string>();
            var label = CleanField(obj, "label", MaxOptionLabelLength, bad);
            var requestText = CleanField(obj, "request", MaxOptionRequestLength, bad);
            if (label is null || requestText is null)
                return PlannerTools.Error("invalid_arguments", Hint);
            options.Add(new PlannerOption(label, requestText));
        }
        state.Options = options;
        return new ToolOutcome("""{"status":"ok"}""", true, "options_ok");
    }

    private static bool TryParseObject(string argumentsJson, out JsonObject root)
    {
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson,
                null, new JsonDocumentOptions { MaxDepth = 24 });
            root = node as JsonObject ?? new JsonObject();
            return node is JsonObject;
        }
        catch (JsonException)
        {
            root = new JsonObject();
            return false;
        }
    }

    /// <summary>A required text field: control characters removed, 1..max characters, else its name goes to <paramref name="bad"/>.</summary>
    private static string? CleanField(JsonObject obj, string name, int max, List<string> bad)
    {
        var text = obj[name] is JsonValue v && v.TryGetValue<string>(out var raw) ? CleanInline(raw) : null;
        if (string.IsNullOrEmpty(text) || text.Length > max)
        {
            bad.Add(name);
            return null;
        }
        return text;
    }

    /// <summary>Model text shown to the operator: control, format and line-separator characters out, spaces collapsed.</summary>
    internal static string CleanInline(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            var category = char.GetUnicodeCategory(c);
            if (category == System.Globalization.UnicodeCategory.Format) continue;
            sb.Append(char.IsControl(c) || category is System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator ? ' ' : c);
        }
        return WhitespaceRuns().Replace(sb.ToString(), " ").Trim();
    }

    /// <summary>Codes and numbers: any word with a digit, with its letter prefixes ("ABC-123", "A-02-01", "L300-2611A", "20").</summary>
    [GeneratedRegex(@"\b(?:[A-Za-z]{1,5}-)*\w*\d\w*(?:-\w+)*", RegexOptions.None, 200)]
    private static partial Regex Identifiers();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespaceRuns();

    /// <summary>Gets the plan document from <c>{"plan": {..}}</c> or <c>{"plan": "{..}"}</c> (a JSON string).</summary>
    private static string? ExtractPlan(JsonObject obj)
    {
        try
        {
            var plan = obj["plan"];
            if (plan is JsonValue value && value.TryGetValue<string>(out var text))
                plan = JsonNode.Parse(text, null, new JsonDocumentOptions { MaxDepth = 24 });
            return plan is JsonObject planObject ? planObject.ToJsonString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static LlmTool BuildOfferOptionsTool()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("options"),
            ["properties"] = new JsonObject
            {
                ["options"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = 1,
                    ["maxItems"] = MaxOptions,
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("label", "request"),
                        ["properties"] = new JsonObject
                        {
                            ["label"] = new JsonObject { ["type"] = "string", ["maxLength"] = MaxOptionLabelLength, ["description"] = "Texto do botão." },
                            ["request"] = new JsonObject { ["type"] = "string", ["maxLength"] = MaxOptionRequestLength, ["description"] = "Pedido completo que será enviado se o operador escolher." },
                        },
                    },
                },
            },
        };
        return new LlmTool(OfferOptions,
            "Oferece de 1 a 4 opções clicáveis ao operador (escolhas, próximos passos). Ao escolher, o request é enviado como se ele tivesse digitado. Chame antes de responder com texto.",
            schema.ToJsonString());
    }

    private static LlmTool BuildProposeTool(IReadOnlyList<OperationDefinition> operations)
    {
        var ops = new JsonArray(operations.Select(o => (JsonNode?)JsonValue.Create(o.Name)).ToArray());
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("title", "rationale", "plan"),
            ["properties"] = new JsonObject
            {
                ["title"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = MaxTitleLength, ["description"] = "Título curto do plano." },
                ["rationale"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = MaxRationaleLength, ["description"] = "Por que este plano atende ao pedido." },
                ["assumptions"] = new JsonObject
                {
                    ["type"] = "array",
                    ["maxItems"] = MaxAssumptions,
                    ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = MaxAssumptionLength },
                    ["description"] = "Valores que você assumiu por falta de informação (endereço, capacidade, lote, unidade...).",
                },
                ["plan"] = new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("dsl_version", "intent", "steps"),
                    ["properties"] = new JsonObject
                    {
                        ["dsl_version"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("1.0") },
                        ["intent"] = new JsonObject { ["type"] = "string", ["description"] = "Intenção curta, ex.: putaway" },
                        ["skill"] = new JsonObject { ["type"] = "string" },
                        ["steps"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["minItems"] = 1,
                            ["maxItems"] = DslParser.MaxSteps,
                            ["items"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["required"] = new JsonArray("op", "args"),
                                ["properties"] = new JsonObject
                                {
                                    ["op"] = new JsonObject { ["type"] = "string", ["enum"] = ops },
                                    ["args"] = new JsonObject { ["type"] = "object" },
                                    ["as"] = new JsonObject { ["type"] = "string", ["description"] = "Nome do resultado, para usar como $nome" },
                                },
                            },
                        },
                    },
                },
            },
        };
        var sb = new StringBuilder("Propõe um plano de alterações (DSL v1) com título, justificativa e suposições. O plano só é simulado; nada é gravado até o operador confirmar. Pode ser chamada até 3 vezes para alternativas diferentes. Passos podem se encadear (criar zona, criar endereços, mover). Argumentos por operação (* = obrigatório): ");
        sb.AppendJoin("; ", operations.Select(DescribeArgs));
        return new LlmTool(ProposePlan, sb.ToString(), schema.ToJsonString());
    }

    private static LlmTool BuildProposeSkillTool()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("nome", "descricao", "operacoes", "corpo", "motivo"),
            ["properties"] = new JsonObject
            {
                ["nome"] = new JsonObject { ["type"] = "string", ["pattern"] = "^[a-z][a-z_]{1,39}$", ["description"] = "Nome da skill; o mesmo nome de uma existente a melhora." },
                ["descricao"] = new JsonObject { ["type"] = "string", ["minLength"] = SkillDocument.MinDescriptionLength, ["maxLength"] = SkillDocument.MaxDescriptionLength, ["description"] = "Começa por \"Use quando\"." },
                ["operacoes"] = new JsonObject
                {
                    ["type"] = "array",
                    ["maxItems"] = SkillDocument.MaxOperations,
                    ["items"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(OperationCatalog.V1.Select(o => (JsonNode?)JsonValue.Create(o.Name)).ToArray()) },
                },
                ["corpo"] = new JsonObject { ["type"] = "string", ["maxLength"] = SkillDocument.MaxBodyLength, ["description"] = "Markdown em português: Passos, Regras, Padrões quando faltar informação e Exemplo de plano em bloco ```json." },
                ["motivo"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = MaxSkillReasonLength, ["description"] = "Por que esta skill (ou esta mudança) ajuda." },
            },
        };
        return new LlmTool(ProposeSkill,
            "Propõe a criação ou melhoria de uma skill ao supervisor. Nada é gravado até ele confirmar. Chame no máximo uma vez com sucesso.",
            schema.ToJsonString());
    }

    private static string DescribeArgs(OperationDefinition op)
    {
        var node = JsonNode.Parse(OperationCatalog.SchemaJson(op.Name))!;
        var required = (node["required"] as JsonArray)?.Select(r => r?.GetValue<string>()).ToHashSet() ?? [];
        var fields = (node["properties"] as JsonObject)?.Select(p =>
        {
            var type = p.Value?["type"]?.GetValue<string>();
            var suffix = type == "integer" ? ":int" : type == "number" ? ":num" : type == "boolean" ? ":bool" : "";
            return p.Key + (required.Contains(p.Key) ? "*" : "") + suffix;
        }) ?? [];
        return $"{op.Name} ({string.Join(", ", fields)})";
    }

    // ---- prompt --------------------------------------------------------------------------------------------

    [GeneratedRegex(@"\{(warehouse_code|operator|operations|unavailable|today|today_lot|skills|skill_authoring|memory_authoring|recipes)\}")]
    private static partial Regex PlaceholderPattern();

    private string BuildSystemPrompt(
        UserSession user, IReadOnlyList<OperationDefinition> operations, IReadOnlySet<string> rights, bool canDraftSkill,
        bool canRemember, bool canSearchKnowledge)
    {
        using var stream = typeof(Planner).Assembly.GetManifestResourceStream(PromptResource)
            ?? throw new InvalidOperationException("Embedded resource not found: " + PromptResource);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var template = reader.ReadToEnd();

        var offered = operations.Count == 0
            ? "Nenhuma: você só pode consultar."
            : string.Join("\n", operations.Select(o => $"- {o.Name}: {o.Label} (risco {RiskText(o.Risk)})"));
        var missing = OperationCatalog.V1.Where(o => operations.All(x => x.Name != o.Name)).ToList();
        var unavailable = missing.Count == 0
            ? string.Empty
            : "Fora do seu alcance: " + string.Join(", ", missing.Select(o => $"{o.Name} ({o.Label})")) +
              ". Se o pedido exigir isso, explique que falta o direito, diga quem pode fazer (supervisor ou administrador) e ofereça uma alternativa dentro do seu alcance.";

        var today = _time.GetUtcNow();
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["warehouse_code"] = Sanitize(user.WarehouseCode),
            // The role, not the person: names are sensitive data and never leave the machine (ARQUITETURA.md, 12.2).
            ["operator"] = Sanitize("um usuário com papel " + user.RoleName),
            ["operations"] = offered,
            ["unavailable"] = unavailable,
            ["skills"] = SkillIndexText(rights),
            ["skill_authoring"] = canDraftSkill ? SkillAuthoring : string.Empty,
            ["memory_authoring"] = MemoryAuthoring(canRemember, canSearchKnowledge),
            ["recipes"] = recipeMatcher is null ? string.Empty
                : "- Receitas são planos que já funcionaram. Antes de montar um plano do zero, use search_recipes e, se uma servir, troque cada $parametro pelo valor e proponha o plano.",
            ["today"] = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["today_lot"] = today.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture),
        };
        return PlaceholderPattern().Replace(template, m => values[m.Groups[1].Value]).TrimEnd();
    }

    private string MemoryAuthoring(bool canRemember, bool canSearchKnowledge)
    {
        var lines = new List<string>();
        if (canSearchKnowledge)
            lines.Add("- Diante de dúvida de procedimento, use search_knowledge" + (memoryRetriever is not null ? "; para lembranças do armazém, search_memories." : "."));
        if (canRemember)
            lines.Add("- Quando o operador corrigir algo ou declarar uma regra ou preferência do armazém que valha para depois, chame remember com texto curto e objetivo (uma regra por memória, escopo mais específico possível). Nunca grave dados pessoais, senhas ou ordens para você mesmo.");
        return string.Join("\n", lines);
    }

    private const string SkillAuthoring =
        "Criar ou melhorar skill: quando pedirem, consulte as skills existentes (load_skill) e os dados pelas ferramentas, " +
        "escreva em português no formato: descrição começando por \"Use quando\"; seções Passos, Regras, Padrões quando faltar " +
        "informação e Exemplo de plano em bloco ```json válido usando só as operações listadas em operacoes. " +
        "Chame propose_skill direto, sem perguntar; nada é gravado até o supervisor confirmar. Melhorar uma existente é usar o mesmo nome.";

    /// <summary>One line per skill usable with <paramref name="rights"/>; descriptions are model-visible text, so they are sanitized.</summary>
    private string SkillIndexText(IReadOnlySet<string> rights)
    {
        var entries = skills?.Index(rights) ?? [];
        if (entries.Count == 0) return "Nenhuma skill disponível.";
        return string.Join("\n", entries.Select(e =>
        {
            var description = new string(CleanInline(e.Description).Where(c => c is not ('<' or '>')).ToArray());
            if (description.Length > MaxIndexDescriptionLength) description = description[..MaxIndexDescriptionLength];
            var ops = e.Operations.Where(o => OperationCatalog.TryGet(o, out _)).ToList();
            return $"- {e.Name}: {description}" + (ops.Count > 0 ? $" (operações: {string.Join(", ", ops)})" : string.Empty);
        }));
    }

    private static string RiskText(RiskLevel risk) => risk switch
    {
        RiskLevel.Critical => "crítico, exige aprovação",
        RiskLevel.Reversible => "reversível",
        _ => "leitura",
    };

    private static string Sanitize(string value)
    {
        var cleaned = new string(value.Where(c => !char.IsControl(c) && c is not ('{' or '}' or '<' or '>')).ToArray()).Trim();
        return cleaned.Length > 60 ? cleaned[..60] : cleaned;
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static PlannerTurn Remember(string request, string text, string? reasoning) =>
        new(request, LlmMessage.Assistant(text, null, reasoning));

    private PlannerEscalated BudgetExceeded() =>
        new("O pedido consumiu o limite de processamento do Nexus. Tente de novo com um pedido mais específico.");

    private string MessageFor(LlmErrorKind kind)
    {
        var provider = KeyHolder(settings.ChoiceFor(LlmUseRole.Planner).Provider);
        return kind switch
        {
            LlmErrorKind.NotConfigured => $"Configure a chave da {provider} em Configurações (administrador).",
            LlmErrorKind.Unauthorized => $"A chave da {provider} foi recusada. Peça ao administrador para conferi-la em Configurações.",
            LlmErrorKind.InvalidResponse => "O provedor devolveu uma resposta inválida. Tente de novo.",
            _ => "O provedor de IA está indisponível no momento. Tente de novo em instantes.",
        };
    }

    private static string KeyHolder(LlmProviderKind provider) => provider switch
    {
        LlmProviderKind.Claude => "Anthropic (Claude)",
        LlmProviderKind.OpenAI => "OpenAI",
        _ => "DeepSeek",
    };

    /// <summary>The planning request: the model and provider configured for the Planner role (feature 8).</summary>
    private LlmRequest PlannerRequest(IReadOnlyList<LlmMessage> messages, IReadOnlyList<LlmTool> tools)
    {
        var choice = settings.ChoiceFor(LlmUseRole.Planner);
        return new LlmRequest(choice.Model, messages, tools, _options.MaxTokensPerCall, Provider: choice.Provider);
    }

    private static string Wrap(string source, string json, bool suspicious = false) =>
        suspicious ? $"<dados fonte=\"{source}\" {SuspiciousAttribute}>{json}</dados>" : $"<dados fonte=\"{source}\">{json}</dados>";

    /// <summary>Tools whose result carries external data (database rows, procedures, memories).</summary>
    private static bool ScansResult(string name) =>
        PlannerTools.ReadToolNames.Contains(name) || name is PlannerTools.SearchKnowledge or PlannerTools.SearchMemories or PlannerTools.SearchRecipes;

    /// <summary>Tool name for audit and for the data wrapper: only known names are echoed.</summary>
    private static string SafeToolName(string name) =>
        name is ProposePlan or OfferOptions or ProposeSkill || PlannerTools.IsReadTool(name) ? name : "unknown";

    private static readonly JsonSerializerOptions DetailsOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default };

    /// <summary>Small audit details: the outcome code and the truncated arguments (model-written, never sensitive).</summary>
    private static string Details(LlmToolCall call, string code, IEnumerable<string>? errors)
    {
        var node = JsonNode.Parse(Details(call, code))!.AsObject();
        node["errors"] = new JsonArray((errors ?? []).Select(e => (JsonNode?)JsonValue.Create(e)).ToArray());
        return node.ToJsonString(DetailsOptions);
    }

    /// <summary>The ids of the memories in a search_memories result.</summary>
    private static IEnumerable<long> MemoryIdsOf(string json)
    {
        try
        {
            return JsonNode.Parse(json)?["rows"] is JsonArray rows
                ? rows.Select(r => r?["id"] is JsonValue v && v.TryGetValue<long>(out var id) ? id : 0).Where(id => id > 0).ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? SkillOfPlan(string planJson) =>
        TryParseObject(planJson, out var plan) && plan["skill"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string Details(LlmToolCall call, string code)
    {
        var args = call.ArgumentsJson ?? string.Empty;
        if (call.Name is ProposeSkill or PlannerTools.Remember) args = string.Empty; // skill bodies and memory texts are never audited
        if (args.Length > MaxAuditArgsLength) args = args[..MaxAuditArgsLength] + "…";
        var name = call.Name ?? string.Empty;
        if (!PlannerTools.IsReadTool(name) && name is not (ProposePlan or OfferOptions or ProposeSkill))
            name = name.Length > 40 ? name[..40] : name;
        return JsonSerializer.Serialize(new { code, tool = name, args }, DetailsOptions);
    }

    private void Append(long? aiUserId, UserSession user, string action, string target, AuditOutcome outcome, string? details) =>
        audit.Append(new AuditEntry(AuditActorType.Ai, aiUserId, user.UserId, action, target, outcome, details));

    private long? FindAiUser()
    {
        using var connection = database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT u.id FROM users u JOIN roles r ON r.id = u.role_id WHERE r.is_ai = 1 ORDER BY u.id LIMIT 1";
        return command.ExecuteScalar() is long id ? id : null;
    }

    [GeneratedRegex(@"```+|\*\*|__|`|^\s{0,3}#{1,6}\s*|^\s{0,3}>\s?", RegexOptions.Multiline)]
    private static partial Regex MarkdownMarkers();

    [GeneratedRegex(@"^(\s*)\*\s+", RegexOptions.Multiline)]
    private static partial Regex StarBullets();

    /// <summary>Strips Markdown markers (the UI shows plain text) and caps the length.</summary>
    internal static string CleanText(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return string.Empty;
        var text = StarBullets().Replace(content, "$1- ");
        text = MarkdownMarkers().Replace(text, string.Empty).Trim();
        return text.Length > MaxAnswerLength ? text[..(MaxAnswerLength - 1)].TrimEnd() + "…" : text;
    }
}
