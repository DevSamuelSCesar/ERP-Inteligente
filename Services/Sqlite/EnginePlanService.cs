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
        var preview = await Task.Run(() => engine.Preview(user, draft.Json, PlannedBy(draft)));

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
        var submission = await Task.Run(() => engine.Submit(user, draft.Json, PlannedBy(draft)));
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

    private static string PlannedBy(PlanDraft draft) => draft.Origin == PlanOrigin.Ai ? "ai" : "user";

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

            case "create_zone":
                if (ReadString(args, "code") is not { } zCode || ReadString(args, "name") is not { } zName) return null;
                return new PlanStep(PlanOperation.CreateZone, 0, string.Empty, string.Empty, zCode, null, zName);

            case "update_zone":
                if (ReadString(args, "code") is not { } uzCode) return null;
                var zoneChanges = new List<string>();
                if (ReadString(args, "name") is { } newZoneName) zoneChanges.Add($"novo nome: {newZoneName}");
                if (ReadBool(args, "is_active") is { } zoneActive) zoneChanges.Add(zoneActive ? "ativar" : "desativar");
                return new PlanStep(PlanOperation.UpdateZone, 0, string.Empty, string.Empty, uzCode, null, string.Join("; ", zoneChanges));

            case "create_location_range":
                if (ReadString(args, "zone") is not { } rZone || ReadInt(args, "aisle") is not { } aisle
                    || ReadInt(args, "level_from") is not { } levelFrom || ReadInt(args, "level_to") is not { } levelTo
                    || ReadInt(args, "capacity") is not { } rCapacity) return null;
                var prefix = rZone.StartsWith('$') ? "zona do passo anterior" : rZone;
                var range = rZone.StartsWith('$') ? $"corredor {aisle:00}, níveis {levelFrom:00} a {levelTo:00}"
                    : $"{rZone}-{aisle:00}-{levelFrom:00} a {rZone}-{aisle:00}-{levelTo:00}";
                return new PlanStep(PlanOperation.CreateLocationRange, rCapacity, string.Empty, string.Empty, prefix, range);

            case "update_location":
                if (ReadString(args, "code") is not { } ulCode) return null;
                var locationChanges = new List<string>();
                if (ReadInt(args, "capacity") is { } newCapacity) locationChanges.Add($"capacidade {newCapacity}");
                if (ReadBool(args, "is_active") is { } locationActive) locationChanges.Add(locationActive ? "ativar" : "desativar");
                return new PlanStep(PlanOperation.UpdateLocation, 0, string.Empty, string.Empty, ulCode, null, string.Join("; ", locationChanges));

            case "update_product":
                if (ReadString(args, "sku") is not { } upSku) return null;
                var fields = new List<string>();
                foreach (var (field, label) in ProductFieldLabels)
                    if (args.ContainsKey(field)) fields.Add(label);
                return new PlanStep(PlanOperation.UpdateProduct, 0, string.Empty, upSku, null, null, string.Join(", ", fields));

            case "create_supplier":
            case "create_customer":
                if (ReadString(args, "name") is not { } partnerName) return null;
                return new PlanStep(op == "create_supplier" ? PlanOperation.CreateSupplier : PlanOperation.CreateCustomer,
                    0, string.Empty, string.Empty, null, ReadString(args, "document_masked"), partnerName);

            case "create_inbound_order":
                if (ReadString(args, "supplier") is not { } ibSupplier || ReadString(args, "document_number") is not { } ibDocument
                    || args["lines"] is not JsonArray ibLines) return null;
                var ibDue = ReadString(args, "expected_on");
                return new PlanStep(PlanOperation.CreateInboundOrder, ibLines.Count, string.Empty, string.Empty, ibSupplier, ibDocument,
                    ibDue is null ? null : $"previsto para {ibDue}");

            case "cancel_inbound_order":
                if (ReadString(args, "document_number") is not { } cbDocument || ReadString(args, "reason") is not { } cbReason) return null;
                return new PlanStep(PlanOperation.CancelInboundOrder, 0, string.Empty, string.Empty, cbDocument, null, cbReason);

            case "create_outbound_order":
                if (ReadString(args, "customer") is not { } obCustomer || ReadString(args, "order_number") is not { } obNumber
                    || args["lines"] is not JsonArray obLines) return null;
                var obDue = ReadString(args, "due_on");
                return new PlanStep(PlanOperation.CreateOutboundOrder, obLines.Count, string.Empty, string.Empty, obCustomer, obNumber,
                    obDue is null ? null : $"entrega até {obDue}");

            case "reserve_stock":
            case "release_reservation":
            case "pick_items":
            case "ship_order":
            case "cancel_outbound_order":
                return MapOrderStep(op, args);

            default:
                return null;
        }
    }

    private static PlanStep? MapOrderStep(string op, JsonObject args)
    {
        if (ReadString(args, "order_number") is not { } number) return null;
        var order = number.StartsWith('$') ? "pedido do passo anterior" : number;
        var lines = args["lines"] as JsonArray;
        var scope = lines is null ? "todas as linhas" : lines.Count == 1 ? "1 linha" : $"{lines.Count} linhas";
        switch (op)
        {
            case "reserve_stock":
                return new PlanStep(PlanOperation.ReserveStock, lines?.Count ?? 0, string.Empty, string.Empty, order, null, scope);
            case "release_reservation":
                return new PlanStep(PlanOperation.ReleaseReservation, lines?.Count ?? 0, string.Empty, string.Empty, order, null, scope);
            case "pick_items":
                return ReadString(args, "to") is { } dock
                    ? new PlanStep(PlanOperation.PickItems, lines?.Count ?? 0, string.Empty, string.Empty, order, dock, scope)
                    : null;
            case "ship_order":
                return new PlanStep(PlanOperation.ShipOrder, 0, string.Empty, string.Empty, order, null);
            default:
                return ReadString(args, "reason") is { } reason
                    ? new PlanStep(PlanOperation.CancelOutboundOrder, 0, string.Empty, string.Empty, order, null, reason)
                    : null;
        }
    }

    // Product fields shown in the business-language line of update_product (the unit cost is named, never its value).
    private static readonly (string Field, string Label)[] ProductFieldLabels =
    [
        ("name", "nome"), ("description", "descrição"), ("unit", "unidade"), ("weight_kg", "peso"),
        ("tracks_expiry", "controle de validade"), ("unit_cost", "custo unitário"),
    ];

    private static bool? ReadBool(JsonObject args, string name) =>
        args[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

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
