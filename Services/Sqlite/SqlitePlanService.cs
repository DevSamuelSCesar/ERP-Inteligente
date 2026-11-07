using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using erp_inteligente.Core.Policy;
using erp_inteligente.Models;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Services.Sqlite;

/// <summary>
/// Read-only view of the plans of the current warehouse, until the deterministic engine exists (ROADMAP.md, feature 2).
/// Plans are read from <c>plans</c>; the steps are parsed from <c>plan_json</c> defensively, and rows that cannot be
/// shown are skipped rather than failing the whole list.
/// </summary>
public sealed class SqlitePlanService(ApplicationDb db, ISessionService session) : IPlanService
{
    /// <summary>The first of these the user holds decides which plans are visible.</summary>
    private static readonly string[] VisibilityPermissions = [Permissions.PlanSubmit, Permissions.PlanApprove, Permissions.AuditRead];

    private const string NotAvailableMessage = "O motor de planos chega na feature 2 do roadmap.";
    private const string AutomaticExecution = "Execução automática";
    private const string DefaultUnit = "un";

    private const string PlansSql = """
        SELECT p.id, COALESCE(p.skill, p.intent), p.plan_json, p.risk, p.status, p.created_at, p.executed_at,
               u.username, u.display_name, approver.display_name
        FROM plans p
        JOIN users u ON u.id = p.requested_by AND u.warehouse_id = $warehouse_id
        LEFT JOIN users approver ON approver.id = p.approved_by
        ORDER BY p.id
        """;

    private const string ProductsSql = "SELECT sku, unit FROM products";

    private IReadOnlyList<Plan> _plans = [];

    public IReadOnlyList<Plan> Plans => _plans;

    public bool IsEngineAvailable => false;

    /// <summary>Never raised: nothing changes a plan until the engine exists.</summary>
    public event EventHandler<Plan>? PlanChanged
    {
        add { }
        remove { }
    }

    public async Task RefreshAsync()
    {
        var user = session.Current;
        var permission = VisibilityPermissions.FirstOrDefault(p => user.Has(p));
        if (permission is null)
        {
            _plans = [];
            return;
        }

        var rows = await db.QueryScopedAsync(user, permission, PlansSql, ReadPlanRow);
        if (rows.Count == 0)
        {
            _plans = [];
            return;
        }

        var units = (await db.QueryGlobalAsync(user, permission, ProductsSql, r => (Sku: r.GetString(0), Unit: r.GetString(1))))
            .ToDictionary(p => p.Sku, p => p.Unit, StringComparer.Ordinal);
        _plans = rows.Select(row => ToPlan(row, units)).OfType<Plan>().ToList();
    }

    public Task SubmitAsync(Plan plan) => Task.FromException(NotAvailable());

    public Task ApproveAsync(Plan plan, UserSession approver) => Task.FromException(NotAvailable());

    public Task RejectAsync(Plan plan, UserSession approver, string reason) => Task.FromException(NotAvailable());

    private static InvalidOperationException NotAvailable() => new(NotAvailableMessage);

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
        reader.IsDBNull(9) ? null : reader.GetString(9));

    /// <summary>Builds the domain object, or returns null when the row has an unknown status or no known step.</summary>
    private static Plan? ToPlan(PlanRow row, IReadOnlyDictionary<string, string> units)
    {
        var status = StatusOf(row.Status);
        if (status is null) return null;

        var steps = ParseSteps(row.PlanJson, units);
        if (steps.Count == 0) return null;

        var plan = new Plan(row.Id, row.Skill, row.Username, row.DisplayName, SqliteFormat.ParseUtc(row.CreatedAt), steps);
        var automatic = row.ApproverName is null && row.Status == "executed" && row.Risk == "reversible";
        plan.Status = status.Value;
        plan.DecidedByName = row.ApproverName ?? (automatic ? AutomaticExecution : null);
        plan.DecidedAt = row.ExecutedAt is null ? null : SqliteFormat.ParseUtc(row.ExecutedAt);
        return plan;
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
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("steps", out var elements)
                || elements.ValueKind != JsonValueKind.Array)
                return steps;

            foreach (var element in elements.EnumerateArray())
            {
                if (ParseStep(element, units) is not { } step) return [];
                steps.Add(step);
            }
        }
        catch (JsonException)
        {
            steps.Clear();
        }
        return steps;
    }

    private static PlanStep? ParseStep(JsonElement element, IReadOnlyDictionary<string, string> units)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String) return null;
        if (!element.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Object) return null;
        if (ReadString(args, "sku") is not { } sku || ReadInt(args, "qty") is not { } quantity) return null;

        var unit = units.TryGetValue(sku, out var known) ? known : DefaultUnit;
        switch (op.GetString())
        {
            case "move_stock":
                if (ReadString(args, "from") is not { } source || ReadString(args, "to") is not { } destination) return null;
                return new PlanStep(PlanOperation.MoveStock, quantity, unit, sku, source, destination);

            case "adjust_stock":
                if (ReadString(args, "location") is not { } location) return null;
                return new PlanStep(PlanOperation.AdjustStock, quantity, unit, sku, location, null, ReadString(args, "reason"));

            default:
                return null;
        }
    }

    private static string? ReadString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    /// <summary>One <c>plans</c> row joined with its requester and approver.</summary>
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
        string? ApproverName);
}
