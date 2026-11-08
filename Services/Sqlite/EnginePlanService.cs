using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using erp_inteligente.Core.Engine;
using erp_inteligente.Core.Policy;
using erp_inteligente.Models;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Services.Sqlite;

/// <summary>
/// Plan service backed by the deterministic <see cref="PlanEngine"/>. Plans are listed from <c>plans</c> for the
/// current warehouse; the engine runs on a background thread and every state change is followed by a refresh.
/// One <see cref="Plan"/> instance is kept per id and updated in place, because view models hold references to it.
/// </summary>
public sealed class EnginePlanService(PlanEngine engine, ApplicationDb db, ISessionService session) : IPlanService
{
    /// <summary>The first of these the user holds decides which plans are visible.</summary>
    private static readonly string[] VisibilityPermissions = [Permissions.PlanSubmit, Permissions.PlanApprove, Permissions.AuditRead];

    private const string AutomaticExecution = "Execução automática";
    private const string DefaultUnit = "un";

    private const string PlansSql = """
        SELECT p.id, COALESCE(p.skill, p.intent), p.plan_json, p.risk, p.status, p.created_at, p.executed_at,
               u.username, u.display_name, approver.display_name, rejecter.display_name,
               p.rejection_reason, p.failure_reason, p.simulated_diff_json
        FROM plans p
        JOIN users u ON u.id = p.requested_by AND u.warehouse_id = $warehouse_id
        LEFT JOIN users approver ON approver.id = p.approved_by
        LEFT JOIN users rejecter ON rejecter.id = p.rejected_by
        ORDER BY p.id
        """;

    private const string ProductsSql = "SELECT sku, unit FROM products";

    private readonly Dictionary<int, Plan> _known = [];
    private IReadOnlyList<Plan> _plans = [];

    public IReadOnlyList<Plan> Plans => _plans;

    public bool IsEngineAvailable => true;

    public event EventHandler<Plan>? PlanChanged;

    public async Task RefreshAsync()
    {
        var user = session.Current;
        var permission = VisibilityPermissions.FirstOrDefault(p => user.Has(p));
        if (permission is null)
        {
            _known.Clear();
            _plans = [];
            return;
        }

        var rows = await db.QueryScopedAsync(user, permission, PlansSql, ReadPlanRow);
        if (rows.Count == 0)
        {
            _known.Clear();
            _plans = [];
            return;
        }

        var units = await LoadUnitsAsync(user, permission);
        var current = new List<Plan>(rows.Count);
        var seen = new HashSet<int>();
        foreach (var row in rows)
        {
            var plan = Apply(row, units);
            if (plan is null) continue;
            seen.Add(plan.Id);
            current.Add(plan);
        }
        foreach (var gone in _known.Keys.Where(id => !seen.Contains(id)).ToList())
            _known.Remove(gone);
        _plans = current;
    }

    public async Task<PlanPreview> PreviewAsync(PlanDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var user = session.Current;
        var preview = await Task.Run(() => engine.Preview(user, draft.Json));

        var issues = preview.Errors.Select(e => new PlanIssue(e.StepIndex is { } i ? i + 1 : null, e.Message)).ToList();
        var steps = new List<PlanStep>();
        if (preview.Document is { } document)
        {
            var units = await LoadUnitsAsync(user, Permissions.PlanSubmit);
            foreach (var step in document.Steps)
                if (MapStep(step.Op, step.Args, units) is { } mapped)
                    steps.Add(mapped);
        }
        var risk = preview.Risk == RiskLevel.Critical ? PlanRisk.Critical : PlanRisk.Reversible;
        var effect = preview.IsValid && preview.Diff is { } diff ? diff.ToSummary(preview.Risk) : null;
        return new PlanPreview(issues, steps, risk, effect);
    }

    /// <exception cref="ArgumentException">The draft is not a storable JSON document (nothing was stored); the message lists the problems.</exception>
    public async Task<Plan> SubmitAsync(PlanDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var user = session.Current;
        var submission = await Task.Run(() => engine.Submit(user, draft.Json, "user"));
        if (submission.PlanId is not { } id)
            throw new ArgumentException(Messages(submission.Errors), nameof(draft));

        await RefreshAsync();
        // A stored plan whose steps cannot be shown (e.g. arguments rejected by the schema) is not listed: report why it failed.
        var plan = _plans.FirstOrDefault(p => p.Id == id)
            ?? throw new ArgumentException(Messages(submission.Errors), nameof(draft));
        PlanChanged?.Invoke(this, plan);
        return plan;
    }

    public async Task ApproveAsync(Plan plan, UserSession approver)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(approver);
        await Task.Run(() => engine.Approve(approver, plan.Id));
        await RefreshAsync();
        PlanChanged?.Invoke(this, Tracked(plan));
    }

    public async Task RejectAsync(Plan plan, UserSession approver, string reason)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(approver);
        await Task.Run(() => engine.Reject(approver, plan.Id, reason));
        await RefreshAsync();
        PlanChanged?.Invoke(this, Tracked(plan));
    }

    private Plan Tracked(Plan plan) => _known.TryGetValue(plan.Id, out var known) ? known : plan;

    private static string Messages(IReadOnlyList<PlanError> errors) =>
        errors.Count == 0 ? "O plano não é válido." : string.Join(" ", errors.Select(e => e.Message));

    private async Task<IReadOnlyDictionary<string, string>> LoadUnitsAsync(UserSession user, string permission) =>
        (await db.QueryGlobalAsync(user, permission, ProductsSql, r => (Sku: r.GetString(0), Unit: r.GetString(1))))
            .ToDictionary(p => p.Sku, p => p.Unit, StringComparer.Ordinal);

    private static PlanRow ReadPlanRow(SqliteDataReader reader) => new(
        reader.GetInt32(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetString(7),
        reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetString(13));

    /// <summary>Creates or updates the tracked plan of the row; null when it has an unknown status or an unmappable step.</summary>
    private Plan? Apply(PlanRow row, IReadOnlyDictionary<string, string> units)
    {
        var status = StatusOf(row.Status);
        if (status is null) return null;

        var steps = ParseSteps(row.PlanJson, units);
        if (steps.Count == 0) return null;

        if (!_known.TryGetValue(row.Id, out var plan))
        {
            plan = new Plan(row.Id, row.Skill, row.Username, row.DisplayName, SqliteFormat.ParseUtc(row.CreatedAt), steps);
            _known[row.Id] = plan;
        }

        var automatic = row.ApproverName is null && row.RejecterName is null && row.Status == "executed" && row.Risk == "reversible";
        plan.Status = status.Value;
        plan.DecidedByName = row.ApproverName ?? row.RejecterName ?? (automatic ? AutomaticExecution : null);
        plan.DecidedAt = row.ExecutedAt is null ? null : SqliteFormat.ParseUtc(row.ExecutedAt);
        plan.RejectionReason = row.RejectionReason;
        plan.FailureMessage = row.FailureReason;
        plan.Summary = status == PlanStatus.Executed ? SummaryOf(row) : null;
        return plan;
    }

    private static ExecutionSummary? SummaryOf(PlanRow row)
    {
        var diff = PlanDiffJson.Deserialize(row.DiffJson);
        if (diff.IsEmpty) return null;
        var risk = row.Risk == "critical" ? RiskLevel.Critical : RiskLevel.Reversible;
        return diff.ToSummary(risk);
    }

    private static PlanStatus? StatusOf(string status) => status switch
    {
        "validating" => PlanStatus.Validating,
        "pending_approval" => PlanStatus.PendingApproval,
        "rejected" => PlanStatus.Rejected,
        "executed" => PlanStatus.Executed,
        "failed" => PlanStatus.Failed,
        _ => null,
    };

    /// <summary>
    /// Parses <c>{"steps":[{"op":...,"args":{...}}]}</c>. Malformed JSON, or any step this build does not
    /// understand, yields no steps: a plan shown with only part of its steps would misinform the approver.
    /// </summary>
    private static List<PlanStep> ParseSteps(string json, IReadOnlyDictionary<string, string> units)
    {
        var steps = new List<PlanStep>();
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["steps"] is not JsonArray elements)
                return steps;

            foreach (var element in elements)
            {
                if (element is not JsonObject obj
                    || obj["op"] is not JsonValue opValue || !opValue.TryGetValue<string>(out var op)
                    || obj["args"] is not JsonObject args
                    || MapStep(op, args, units) is not { } step)
                    return [];
                steps.Add(step);
            }
        }
        catch (JsonException)
        {
            steps.Clear();
        }
        return steps;
    }

    /// <summary>Maps one DSL step to its business-language form; null when the op or its arguments are not understood.</summary>
    private static PlanStep? MapStep(string op, JsonObject args, IReadOnlyDictionary<string, string> units)
    {
        string UnitOf(string sku) => units.TryGetValue(sku, out var known) ? known : DefaultUnit;

        switch (op)
        {
            case "receive_goods":
                if (ReadString(args, "sku") is not { } rSku || ReadInt(args, "qty") is not { } rQty
                    || ReadString(args, "location") is not { } rLocation) return null;
                return new PlanStep(PlanOperation.ReceiveGoods, rQty, UnitOf(rSku), rSku, null, rLocation, ReadString(args, "lot"));

            case "move_stock":
                if (ReadString(args, "sku") is not { } mSku || ReadInt(args, "qty") is not { } mQty
                    || ReadString(args, "from") is not { } source || ReadString(args, "to") is not { } destination) return null;
                return new PlanStep(PlanOperation.MoveStock, mQty, UnitOf(mSku), mSku, source, destination, ReadString(args, "lot"));

            case "adjust_stock":
                if (ReadString(args, "sku") is not { } aSku || ReadInt(args, "qty") is not { } aQty
                    || ReadString(args, "location") is not { } aLocation) return null;
                return new PlanStep(PlanOperation.AdjustStock, aQty, UnitOf(aSku), aSku, aLocation, null, ReadString(args, "reason"));

            case "create_product":
                if (ReadString(args, "sku") is not { } pSku || ReadString(args, "unit") is not { } pUnit
                    || ReadString(args, "name") is not { } name) return null;
                return new PlanStep(PlanOperation.CreateProduct, 0, pUnit, pSku, null, null, name);

            case "create_location":
                if (ReadString(args, "code") is not { } code || ReadString(args, "zone") is not { } zone
                    || ReadInt(args, "capacity") is not { } capacity) return null;
                return new PlanStep(PlanOperation.CreateLocation, capacity, string.Empty, string.Empty, zone, code);

            default:
                return null;
        }
    }

    private static string? ReadString(JsonObject args, string name) =>
        args[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static int? ReadInt(JsonObject args, string name) =>
        args[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    /// <summary>One <c>plans</c> row joined with its requester, approver and rejecter.</summary>
    private sealed record PlanRow(
        int Id,
        string Skill,
        string PlanJson,
        string Risk,
        string Status,
        string CreatedAt,
        string? ExecutedAt,
        string Username,
        string DisplayName,
        string? ApproverName,
        string? RejecterName,
        string? RejectionReason,
        string? FailureReason,
        string? DiffJson);
}
