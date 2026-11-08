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
/// The Planner (references/ARQUITETURA.md, sections 1, 4 and 12.8): talks to the LLM with read tools and
/// <c>submit_plan</c>. The model reads data through validated, parameterized queries on the read-only AI connection and
/// can only propose a plan, which the deterministic engine validates and simulates (<c>Preview</c>, nothing is written).
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
    PlannerOptions? options = null)
{
    internal const string SubmitPlan = "submit_plan";
    internal const int MaxRequestLength = 2000;
    internal const int MaxAnswerLength = 1500;
    private const int MaxToolCallsPerRound = 8;
    private const int MaxReportedErrors = 10;
    private const int MaxAuditArgsLength = 200;
    private const string PromptResource = "Nexus.Planner.system.md";

    private readonly PlannerOptions _options = options ?? PlannerOptions.Default;

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
        var canSubmit = rights.Contains(Permissions.PlanSubmit) && operations.Count > 0;
        var tools = PlannerTools.ReadTools().ToList();
        if (canSubmit) tools.Add(BuildSubmitTool(operations));

        var messages = new List<LlmMessage> { LlmMessage.System(BuildSystemPrompt(user, operations)) };
        foreach (var turn in history.TakeLast(_options.MaxHistoryTurns))
        {
            if (turn.Reply.Role != LlmRole.Assistant) continue;
            messages.Add(LlmMessage.User(turn.Request));
            messages.Add(LlmMessage.Assistant(turn.Reply.Content, null, turn.Reply.ReasoningContent));
        }
        messages.Add(LlmMessage.User(request));

        var tokens = 0;
        var invalidPlans = 0;
        status.Report("Planejando");

        for (var round = 1; round <= _options.MaxRounds; round++)
        {
            if (tokens >= _options.MaxTokensPerRequest)
                return (BudgetExceeded(), null);

            LlmResponse response;
            try
            {
                response = await llm.CompleteAsync(
                    new LlmRequest(settings.Model, messages.ToArray(), tools, _options.MaxTokensPerCall), ct);
            }
            catch (LlmException ex)
            {
                return (new PlannerFailed(MessageFor(ex.Kind)), null);
            }
            tokens += Math.Max(0, response.Usage.PromptTokens) + Math.Max(0, response.Usage.CompletionTokens);

            if (response.ToolCalls.Count == 0)
            {
                var text = CleanText(response.Content);
                if (text.Length == 0)
                    return response.FinishReason == LlmFinishReason.Length
                        ? (new PlannerEscalated("A resposta ficou longa demais para ser concluída. Faça um pedido mais específico."), null)
                        : (new PlannerFailed("O Nexus não devolveu uma resposta válida. Tente de novo."), null);
                return (new PlannerAnswer(text), Remember(request, text, response));
            }

            messages.Add(LlmMessage.Assistant(response));
            var index = 0;
            foreach (var call in response.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();
                if (++index > MaxToolCallsPerRound)
                {
                    messages.Add(LlmMessage.ToolResult(call.Id, Wrap("tool:" + SubmitPlan, PlannerTools.Error("too_many_calls", "Chamadas demais de uma vez.").Json)));
                    continue;
                }

                var (outcome, proposal) = await Task.Run(() => RunTool(user, aiUserId, rights, canSubmit, call, status), ct);
                if (proposal is not null)
                {
                    var text = CleanText(response.Content);
                    var shown = text.Length > 0 ? text : null;
                    return (proposal with { Text = shown }, Remember(request, shown ?? "Montei um plano para a sua confirmação.", response));
                }
                if (call.Name == SubmitPlan && !outcome.Allowed) invalidPlans++;
                messages.Add(LlmMessage.ToolResult(call.Id, Wrap("tool:" + SafeToolName(call.Name), outcome.Json)));
            }

            if (invalidPlans >= _options.MaxInvalidPlans)
                return (new PlannerEscalated(
                    $"Não consegui montar um plano válido em {_options.MaxInvalidPlans} tentativas. " +
                    "Descreva o pedido com mais detalhes (SKU, quantidade e endereços) ou use a tela \"Novo plano\"."), null);
        }

        return (new PlannerEscalated(
            "O pedido exigiu consultas demais e não foi concluído. Tente dividi-lo em partes menores."), null);
    }

    // ---- tools ---------------------------------------------------------------------------------------------

    private (ToolOutcome Outcome, PlannerProposal? Proposal) RunTool(
        UserSession user, long? aiUserId, IReadOnlySet<string> rights, bool canSubmit, LlmToolCall call, IProgress<string> status)
    {
        var name = call.Name ?? string.Empty;
        try
        {
            if (name == SubmitPlan)
            {
                if (!canSubmit)
                    return Finish(PlannerTools.Error("not_allowed", "Você não tem permissão para propor planos."));
                return SubmitPlanTool(user, aiUserId, call, status);
            }
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
            return (failed, null);
        }

        (ToolOutcome, PlannerProposal?) Finish(ToolOutcome outcome, string? targetName = null)
        {
            Append(aiUserId, user, "ai.tool", "tool:" + (targetName ?? SafeToolName(name)),
                outcome.Allowed ? AuditOutcome.Allowed : AuditOutcome.Denied, Details(call, outcome.Code));
            return (outcome, null);
        }
    }

    private (ToolOutcome Outcome, PlannerProposal? Proposal) SubmitPlanTool(
        UserSession user, long? aiUserId, LlmToolCall call, IProgress<string> status)
    {
        (ToolOutcome, PlannerProposal?) Reject(ToolOutcome outcome)
        {
            Append(aiUserId, user, "ai.tool", "tool:" + SubmitPlan, AuditOutcome.Denied, Details(call, outcome.Code));
            return (outcome, null);
        }

        var planJson = ExtractPlan(call.ArgumentsJson);
        if (planJson is null)
            return Reject(PlannerTools.Error("invalid_arguments", "Envie {\"plan\": {dsl_version, intent, steps}} com o documento do plano."));

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

        Append(aiUserId, user, "ai.tool", "tool:" + SubmitPlan, AuditOutcome.Allowed,
            Details(call, "plan_valid"));
        var ok = new ToolOutcome("""{"status":"valid"}""", true, "plan_valid");
        return (ok, new PlannerProposal(PlanDraft.FromJson(planJson, PlanOrigin.Ai), preview, null));
    }

    /// <summary>Gets the plan document from the arguments: <c>{"plan": {..}}</c>, <c>{"plan": "{..}"}</c> or the bare document.</summary>
    private static string? ExtractPlan(string argumentsJson)
    {
        try
        {
            var root = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson,
                null, new JsonDocumentOptions { MaxDepth = 24 });
            if (root is not JsonObject obj) return null;
            JsonNode? plan = obj.TryGetPropertyValue("plan", out var p) ? p : obj.ContainsKey("steps") ? obj : null;
            if (plan is JsonValue value && value.TryGetValue<string>(out var text))
                plan = JsonNode.Parse(text, null, new JsonDocumentOptions { MaxDepth = 24 });
            return plan is JsonObject planObject ? planObject.ToJsonString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static LlmTool BuildSubmitTool(IReadOnlyList<OperationDefinition> operations)
    {
        var ops = new JsonArray(operations.Select(o => (JsonNode?)JsonValue.Create(o.Name)).ToArray());
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("plan"),
            ["properties"] = new JsonObject
            {
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
        var sb = new StringBuilder("Propõe um plano de alterações (DSL v1). O plano só é simulado; nada é gravado até o operador confirmar. Argumentos por operação (* = obrigatório): ");
        sb.AppendJoin("; ", operations.Select(DescribeArgs));
        return new LlmTool(SubmitPlan, sb.ToString(), schema.ToJsonString());
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

    [GeneratedRegex(@"\{(warehouse_code|operator|operations|unavailable)\}")]
    private static partial Regex PlaceholderPattern();

    private static string BuildSystemPrompt(UserSession user, IReadOnlyList<OperationDefinition> operations)
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
              ". Se o pedido exigir isso, diga ao operador que é feito pela tela \"Novo plano\" (ajustes e cadastros de endereço exigem a aprovação ou a permissão dele).";

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["warehouse_code"] = Sanitize(user.WarehouseCode),
            // The role, not the person: names are sensitive data and never leave the machine (ARQUITETURA.md, 12.2).
            ["operator"] = Sanitize("um usuário com papel " + user.RoleName),
            ["operations"] = offered,
            ["unavailable"] = unavailable,
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

    private static PlannerTurn Remember(string request, string text, LlmResponse response) =>
        new(request, LlmMessage.Assistant(text, null, response.ReasoningContent));

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
        name == SubmitPlan || PlannerTools.IsReadTool(name) ? name : "unknown";

    private static readonly JsonSerializerOptions DetailsOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default };

    /// <summary>Small audit details: the outcome code and the truncated arguments (model-written, never sensitive).</summary>
    private static string Details(LlmToolCall call, string code)
    {
        var args = call.ArgumentsJson ?? string.Empty;
        if (args.Length > MaxAuditArgsLength) args = args[..MaxAuditArgsLength] + "…";
        var name = call.Name ?? string.Empty;
        if (!PlannerTools.IsReadTool(name) && name != SubmitPlan)
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
