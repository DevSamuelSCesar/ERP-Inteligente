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
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Policy;
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
    TimeProvider? time = null)
{
    internal const string ProposePlan = "propose_plan";
    internal const string OfferOptions = "offer_options";
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
        if (canPropose) tools.Add(BuildProposeTool(operations));
        tools.Add(BuildOfferOptionsTool());

        var messages = new List<LlmMessage> { LlmMessage.System(BuildSystemPrompt(user, operations)) };
        foreach (var turn in history.TakeLast(_options.MaxHistoryTurns))
        {
            if (turn.Reply.Role != LlmRole.Assistant) continue;
            messages.Add(LlmMessage.User(turn.Request));
            messages.Add(LlmMessage.Assistant(turn.Reply.Content, null, turn.Reply.ReasoningContent));
        }
        messages.Add(LlmMessage.User(request));

        var state = new RunState();
        var tokens = 0;
        status.Report("Planejando");

        // With valid proposals in hand a limit never throws them away: the operator gets what was prepared.
        (PlannerResult, PlannerTurn?) Limit(PlannerEscalated escalated) =>
            state.Proposals.Count > 0 ? Conclude(request, state, null, null) : (escalated, null);

        for (var round = 1; round <= _options.MaxRounds; round++)
        {
            if (tokens >= _options.MaxTokensPerRequest)
                return Limit(BudgetExceeded());

            LlmResponse response;
            try
            {
                response = await llm.CompleteAsync(
                    new LlmRequest(settings.Model, messages.ToArray(), tools, _options.MaxTokensPerCall), ct);
            }
            catch (LlmException ex)
            {
                if (state.Proposals.Count > 0) return Conclude(request, state, null, null);
                return (new PlannerFailed(MessageFor(ex.Kind)), null);
            }
            tokens += Math.Max(0, response.Usage.PromptTokens) + Math.Max(0, response.Usage.CompletionTokens);

            if (response.ToolCalls.Count == 0)
            {
                var text = CleanText(response.Content);
                if (state.Proposals.Count > 0)
                    return Conclude(request, state, text.Length > 0 ? text : null, response.ReasoningContent);
                if (text.Length == 0 && state.Options.Count > 0)
                    text = "Como prefere seguir?";
                if (text.Length == 0)
                    return response.FinishReason == LlmFinishReason.Length
                        ? (new PlannerEscalated("A resposta ficou longa demais para ser concluída. Faça um pedido mais específico."), null)
                        : (new PlannerFailed("O Nexus não devolveu uma resposta válida. Tente de novo."), null);
                return (new PlannerAnswer(text, state.Options.ToArray()), Remember(request, text, response.ReasoningContent));
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

                var outcome = await Task.Run(() => RunTool(user, aiUserId, rights, canPropose, state, call, status), ct);
                if (call.Name == ProposePlan && !outcome.Allowed && outcome.Code != "too_many_proposals") state.Invalid++;
                messages.Add(LlmMessage.ToolResult(call.Id, Wrap("tool:" + SafeToolName(call.Name), outcome.Json)));
            }

            if (state.Proposals.Count >= _options.MaxProposals)
                return await FinalTextAsync(user, request, state, messages, tokens, ct);

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
                var response = await llm.CompleteAsync(
                    new LlmRequest(settings.Model, messages.ToArray(), [], _options.MaxTokensPerCall), ct);
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
        var shown = text ?? (state.Proposals.Count == 1
            ? "Montei um plano para a sua confirmação."
            : $"Montei {state.Proposals.Count} alternativas de plano para a sua escolha.");
        var result = new PlannerProposals(state.Proposals.ToArray(), shown, state.Options.ToArray());
        var memory = shown + " Planos: " + string.Join("; ", state.Proposals.Select(p => p.Title)) + ".";
        return (result, Remember(request, memory.Length > MaxAnswerLength ? memory[..MaxAnswerLength] : memory, reasoning));
    }

    // ---- tools ---------------------------------------------------------------------------------------------

    private ToolOutcome RunTool(
        UserSession user, long? aiUserId, IReadOnlySet<string> rights, bool canPropose, RunState state, LlmToolCall call, IProgress<string> status)
    {
        var name = call.Name ?? string.Empty;
        try
        {
            if (name == ProposePlan)
            {
                if (!canPropose)
                    return Finish(PlannerTools.Error("not_allowed", "Você não tem permissão para propor planos."));
                return ProposePlanTool(user, aiUserId, state, call, status);
            }
            if (name == OfferOptions)
                return Finish(OfferOptionsTool(state, call));
            if (!PlannerTools.IsReadTool(name))
                return Finish(PlannerTools.Error("unknown_tool", "Ferramenta desconhecida."), "unknown");
            if (!rights.Contains(Permissions.StockRead))
                return Finish(PlannerTools.Error("not_allowed", "Sem permissão de consulta."));

            var invalid = PlannerTools.Validate(name, call.ArgumentsJson, out var args);
            if (invalid is not null) return Finish(invalid);

            status.Report(PlannerTools.StatusFor(name, args));
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

    private ToolOutcome ProposePlanTool(UserSession user, long? aiUserId, RunState state, LlmToolCall call, IProgress<string> status)
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
            return Reject(new ToolOutcome(json, false, "plan_invalid"));
        }

        Append(aiUserId, user, "ai.tool", "tool:" + ProposePlan, AuditOutcome.Allowed, Details(call, "plan_valid"));
        state.Proposals.Add(new ProposalItem(title, rationale, assumptions, PlanDraft.FromJson(planJson, PlanOrigin.Ai), preview));
        var summary = new JsonObject { ["status"] = "valid", ["proposals"] = state.Proposals.Count, ["risk"] = preview.Risk.ToString().ToLowerInvariant() };
        return new ToolOutcome(PlannerTools.Serialize(summary), true, "plan_valid");
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

    [GeneratedRegex(@"\{(warehouse_code|operator|operations|unavailable|today|today_lot)\}")]
    private static partial Regex PlaceholderPattern();

    private string BuildSystemPrompt(UserSession user, IReadOnlyList<OperationDefinition> operations)
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
            ["today"] = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["today_lot"] = today.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture),
        };
        return PlaceholderPattern().Replace(template, m => values[m.Groups[1].Value]).TrimEnd();
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

    private static string MessageFor(LlmErrorKind kind) => kind switch
    {
        LlmErrorKind.NotConfigured => "Configure a chave da DeepSeek em Configurações (administrador).",
        LlmErrorKind.Unauthorized => "A chave da DeepSeek foi recusada. Peça ao administrador para conferi-la em Configurações.",
        LlmErrorKind.InvalidResponse => "O provedor devolveu uma resposta inválida. Tente de novo.",
        _ => "O provedor de IA está indisponível no momento. Tente de novo em instantes.",
    };

    private static string Wrap(string source, string json) => $"<dados fonte=\"{source}\">{json}</dados>";

    /// <summary>Tool name for audit and for the data wrapper: only known names are echoed.</summary>
    private static string SafeToolName(string name) =>
        name is ProposePlan or OfferOptions || PlannerTools.IsReadTool(name) ? name : "unknown";

    private static readonly JsonSerializerOptions DetailsOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default };

    /// <summary>Small audit details: the outcome code and the truncated arguments (model-written, never sensitive).</summary>
    private static string Details(LlmToolCall call, string code)
    {
        var args = call.ArgumentsJson ?? string.Empty;
        if (args.Length > MaxAuditArgsLength) args = args[..MaxAuditArgsLength] + "…";
        var name = call.Name ?? string.Empty;
        if (!PlannerTools.IsReadTool(name) && name is not (ProposePlan or OfferOptions))
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
