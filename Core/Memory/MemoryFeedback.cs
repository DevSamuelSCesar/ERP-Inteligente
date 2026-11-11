using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Engine;
using erp_inteligente.Models;

namespace erp_inteligente.Core.Memory;

/// <summary>
/// Learning from what happened to the plans (references/ARQUITETURA.md, section 8):
/// <list type="bullet">
/// <item><b>Confidence follows the results:</b> the memories that fed an AI plan gain <see cref="UsefulStep"/> when the plan
/// is executed and lose <see cref="ContradictedStep"/> when a supervisor rejects it. Below 0.3 a memory is no longer injected
/// in the context, so one rejection takes a fresh AI memory out of use until it is reinforced.</item>
/// <item><b>Recurring errors become memories:</b> when the same validation or execution error code shows up
/// <see cref="RecurrenceThreshold"/> times in <see cref="RecurrenceDays"/> days in a warehouse (plans rejected by the
/// validator in the Planner, or failed plans), a system memory of kind <c>pattern</c> records it, at most one per code.</item>
/// </list>
/// System actions: every change is audited with ids and codes, never with text written by the model. Never throws: learning
/// must not undo or hide a decision.
/// </summary>
public sealed class MemoryFeedback(NexusDatabase database, MemoryStore memories, AuditLog audit, TimeProvider time)
{
    public const double UsefulStep = 0.05;
    public const double ContradictedStep = 0.15;
    public const int RecurrenceThreshold = 3;
    public const int RecurrenceDays = 30;
    public const int MaxMemoriesPerPlan = 20;
    private const int MaxMessageLength = 300;

    /// <summary>The memories behind a plan executed (<paramref name="useful"/>) or rejected by a supervisor.</summary>
    public void PlanOutcome(long planId, IReadOnlyCollection<long> memoryIds, bool useful, long? onBehalfOf)
    {
        if (memoryIds.Count == 0) return;
        try
        {
            var ids = memoryIds.Distinct().Take(MaxMemoriesPerPlan).ToList();
            var now = AuditHasher.FormatTimestamp(time.GetUtcNow());
            using var connection = database.OpenApplication();
            using var transaction = connection.BeginTransaction(deferred: false);
            var changed = new List<long>();
            foreach (var id in ids)
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = useful
                    ? "UPDATE memories SET confidence = MIN($max, confidence + $step), updated_at = $at WHERE id = $id AND status = 'active' AND confidence < $max"
                    : "UPDATE memories SET confidence = MAX(0, confidence - $step), updated_at = $at WHERE id = $id AND status = 'active'";
                update.Parameters.AddWithValue("$max", MemoryStore.MaxReinforcedConfidence);
                update.Parameters.AddWithValue("$step", useful ? UsefulStep : ContradictedStep);
                update.Parameters.AddWithValue("$at", now);
                update.Parameters.AddWithValue("$id", id);
                if (update.ExecuteNonQuery() == 1) changed.Add(id);
            }
            if (changed.Count > 0)
                audit.AppendWithin(connection, transaction, new AuditEntry(AuditActorType.System, null, onBehalfOf,
                    useful ? "memory.useful" : "memory.contradicted", "plan:" + planId.ToString(CultureInfo.InvariantCulture),
                    AuditOutcome.Allowed, new JsonObject
                    {
                        ["memories"] = new JsonArray(changed.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
                        ["step"] = useful ? UsefulStep : -ContradictedStep,
                    }.ToJsonString()));
            transaction.Commit();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning("Memory feedback failed: " + ex.GetType().Name);
        }
    }

    /// <summary>
    /// Validation or execution errors of a plan just seen (already audited with their codes by the caller). A code that
    /// reached <see cref="RecurrenceThreshold"/> occurrences in the warehouse becomes a pattern memory, once.
    /// Returns the ids of the memories created.
    /// </summary>
    public IReadOnlyList<long> ObserveErrors(UserSession user, string? skill, IEnumerable<PlanError> errors)
    {
        var created = new List<long>();
        try
        {
            foreach (var error in errors.Where(e => !string.IsNullOrEmpty(e.Code)).GroupBy(e => e.Code).Select(g => g.First()))
            {
                if (Occurrences(user.WarehouseId, error.Code) < RecurrenceThreshold) continue;
                var marker = $"Erro recorrente ({error.Code})";
                if (HasActiveMemory(user.WarehouseId, marker)) continue;

                var message = Planner.Planner.CleanInline(error.Message);
                if (message.Length > MaxMessageLength) message = message[..(MaxMessageLength - 1)] + "…";
                var scope = SkillDocumentName(skill) is not null ? MemoryScope.Skill : MemoryScope.Global;
                var result = memories.Remember(user, MemoryKind.Pattern, scope, scope == MemoryScope.Skill ? skill : null,
                    $"{marker} em planos: {message} Confira isso antes de propor.", MemoryOrigin.System);
                if (result.Saved && !result.Reinforced) created.Add(result.Id);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning("Recurring error learning failed: " + ex.GetType().Name);
        }
        return created;
    }

    /// <summary>
    /// Occurrences of a code in the warehouse's audit trail in the window: plans the validator refused in the Planner
    /// (<c>ai.tool</c> with <c>errors</c>) and failed plans (<c>plan.failed</c>).
    /// </summary>
    private long Occurrences(long warehouseId, string code)
    {
        using var connection = database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM audit_log a JOIN users u ON u.id = a.on_behalf_of, json_each(a.details_json, '$.errors') e " +
            "WHERE a.action IN ('ai.tool', 'plan.failed') AND u.warehouse_id = $w AND e.value = $code AND a.occurred_at >= $since " +
            "AND json_valid(a.details_json)";
        command.Parameters.AddWithValue("$w", warehouseId);
        command.Parameters.AddWithValue("$code", code);
        command.Parameters.AddWithValue("$since", AuditHasher.FormatTimestamp(time.GetUtcNow().AddDays(-RecurrenceDays)));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private bool HasActiveMemory(long warehouseId, string marker)
    {
        using var connection = database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM memories WHERE warehouse_id = $w AND status = 'active' AND substr(content, 1, length($m)) = $m";
        command.Parameters.AddWithValue("$w", warehouseId);
        command.Parameters.AddWithValue("$m", marker);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static string? SkillDocumentName(string? skill) => Skills.SkillDocument.IsValidName(skill) ? skill : null;

    /// <summary>The ids recorded with an AI plan when it was submitted (the <c>plan.submit</c> audit row).</summary>
    public IReadOnlyList<long> MemoriesOfPlan(long planId)
    {
        try
        {
            using var connection = database.OpenApplication();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT e.value FROM audit_log a, json_each(a.details_json, '$.memories') e " +
                "WHERE a.action = 'plan.submit' AND a.target = $target AND json_valid(a.details_json)";
            command.Parameters.AddWithValue("$target", "plan:" + planId.ToString(CultureInfo.InvariantCulture));
            using var reader = command.ExecuteReader();
            var ids = new List<long>();
            while (reader.Read())
                if (!reader.IsDBNull(0) && reader.GetValue(0) is long id) ids.Add(id);
            return ids;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning("Reading plan memories failed: " + ex.GetType().Name);
            return [];
        }
    }
}
