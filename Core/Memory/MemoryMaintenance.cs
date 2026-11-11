using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Policy;
using erp_inteligente.Models;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Core.Memory;

/// <summary>What one maintenance run archived.</summary>
public sealed record MaintenanceReport(int Duplicates, int Merged, int Stale)
{
    public int Total => Duplicates + Merged + Stale;
}

/// <summary>
/// Memory maintenance job (references/ARQUITETURA.md, section 8; feature 9): deduplicates, merges similar memories and
/// discards the stale ones. Nothing is deleted: the losers are archived and the supervisor can reactivate them.
/// <list type="bullet">
/// <item><b>Duplicates:</b> the same text (case, accents and punctuation ignored) with the same kind and scope.</item>
/// <item><b>Similar:</b> word overlap (Jaccard) of at least <see cref="SimilarityThreshold"/> with the same kind and scope.</item>
/// <item>The kept memory is the one with the highest confidence, then the most used, then the oldest; it inherits the uses
/// and the highest confidence of the archived ones.</item>
/// <item><b>Stale:</b> AI memories never used and still at the initial confidence after <see cref="UnusedAiDays"/> days, and
/// AI or system memories not used for <see cref="IdleDays"/> days. The supervisor's own memories are never archived here.</item>
/// </list>
/// Runs daily at startup (system) or on demand by the supervisor (<c>memory.manage</c>). One audit row per run, with the
/// counts and ids, never the text.
/// </summary>
public sealed partial class MemoryMaintenance(NexusDatabase database, AuditLog audit, TimeProvider time, PolicyEngine? policy = null)
{
    public const double SimilarityThreshold = 0.8;
    public const int UnusedAiDays = 30;
    public const int IdleDays = 180;
    public static readonly TimeSpan ScheduleInterval = TimeSpan.FromHours(24);

    private const double MaxConfidence = 0.9;

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonWord();

    private sealed record Row(long Id, long Warehouse, string Kind, string Scope, string? ScopeRef, string Content, double Confidence,
        long UseCount, string Origin, string CreatedAt, string? LastUsedAt)
    {
        public string Group => $"{Warehouse}|{Kind}|{Scope}|{TextFold.Fold(ScopeRef ?? "")}";
    }

    /// <summary>Runs now for every warehouse, as <paramref name="user"/> (supervisor).</summary>
    /// <exception cref="PolicyDeniedException">The user lacks <c>memory.manage</c>.</exception>
    public MaintenanceReport Run(UserSession user)
    {
        ArgumentNullException.ThrowIfNull(user);
        policy?.Require(user, Permissions.MemoryManage);
        return RunCore(user);
    }

    /// <summary>The daily job: runs when the last run (of anyone) is older than <see cref="ScheduleInterval"/>. Null when skipped.</summary>
    public MaintenanceReport? RunScheduled()
    {
        using (var connection = database.OpenApplication())
        using (var last = connection.CreateCommand())
        {
            last.CommandText = "SELECT occurred_at FROM audit_log WHERE action = 'memory.maintenance' ORDER BY id DESC LIMIT 1";
            if (last.ExecuteScalar() is string at
                && time.GetUtcNow() - DateTimeOffset.Parse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) < ScheduleInterval)
                return null;
        }
        return RunCore(null);
    }

    private MaintenanceReport RunCore(UserSession? user)
    {
        var now = time.GetUtcNow();
        using var connection = database.OpenApplication();
        using var transaction = connection.BeginTransaction(deferred: false);
        var rows = Load(connection, transaction);
        var archived = new Dictionary<long, string>();
        var duplicates = 0;
        var merged = 0;

        foreach (var group in rows.GroupBy(r => r.Group))
        {
            // Best first: highest confidence, most used, oldest.
            var ordered = group.OrderByDescending(r => r.Confidence).ThenByDescending(r => r.UseCount).ThenBy(r => r.Id).ToList();
            var kept = new List<(Row Row, string Key, HashSet<string> Words)>();
            foreach (var row in ordered)
            {
                var key = Normalize(row.Content);
                var words = Words(key);
                var twin = kept.FirstOrDefault(k => k.Key == key);
                var reason = "duplicate";
                if (twin.Row is null)
                {
                    twin = kept.FirstOrDefault(k => Jaccard(k.Words, words) >= SimilarityThreshold);
                    reason = "similar";
                }
                if (twin.Row is null)
                {
                    kept.Add((row, key, words));
                    continue;
                }
                Absorb(connection, transaction, twin.Row, row, now);
                Archive(connection, transaction, row.Id, now);
                archived[row.Id] = reason;
                if (reason == "duplicate") duplicates++; else merged++;
            }
        }

        var stale = 0;
        foreach (var row in rows.Where(r => !archived.ContainsKey(r.Id) && IsStale(r, now)))
        {
            Archive(connection, transaction, row.Id, now);
            archived[row.Id] = "stale";
            stale++;
        }

        var report = new MaintenanceReport(duplicates, merged, stale);
        var details = new JsonObject
        {
            ["duplicates"] = duplicates,
            ["merged"] = merged,
            ["stale"] = stale,
            ["archived"] = new JsonArray(archived.Keys.Take(100).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
        };
        audit.AppendWithin(connection, transaction, user is null
            ? new AuditEntry(AuditActorType.System, null, null, "memory.maintenance", "memories", AuditOutcome.Allowed, details.ToJsonString())
            : new AuditEntry(AuditActorType.User, user.UserId, null, "memory.maintenance", "memories", AuditOutcome.Allowed, details.ToJsonString()));
        transaction.Commit();
        return report;
    }

    private static bool IsStale(Row row, DateTimeOffset now)
    {
        if (row.Origin == "user") return false;
        var created = Parse(row.CreatedAt);
        if (row.Origin == "ai" && row.UseCount == 0 && row.Confidence <= 0.3 + 1e-9 && now - created >= TimeSpan.FromDays(UnusedAiDays))
            return true;
        var lastUse = row.LastUsedAt is null ? created : Parse(row.LastUsedAt);
        return now - lastUse >= TimeSpan.FromDays(IdleDays);
    }

    /// <summary>The kept memory takes the uses and the best confidence of the archived one.</summary>
    private static void Absorb(SqliteConnection connection, SqliteTransaction transaction, Row kept, Row archived, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE memories SET use_count = use_count + $uses, confidence = MIN($max, MAX(confidence, $confidence)), updated_at = $at " +
            "WHERE id = $id AND status = 'active'";
        command.Parameters.AddWithValue("$uses", archived.UseCount);
        command.Parameters.AddWithValue("$max", MaxConfidence);
        command.Parameters.AddWithValue("$confidence", archived.Confidence);
        command.Parameters.AddWithValue("$at", AuditHasher.FormatTimestamp(now));
        command.Parameters.AddWithValue("$id", kept.Id);
        command.ExecuteNonQuery();
    }

    private static void Archive(SqliteConnection connection, SqliteTransaction transaction, long id, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE memories SET status = 'archived', updated_at = $at WHERE id = $id AND status = 'active'";
        command.Parameters.AddWithValue("$at", AuditHasher.FormatTimestamp(now));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static List<Row> Load(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT id, warehouse_id, kind, scope, scope_ref, content, confidence, use_count, origin, created_at, last_used_at " +
            "FROM memories WHERE status = 'active' ORDER BY id";
        using var reader = command.ExecuteReader();
        var rows = new List<Row>();
        while (reader.Read())
            rows.Add(new Row(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetDouble(6), reader.GetInt64(7),
                reader.GetString(8), reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10)));
        return rows;
    }

    internal static string Normalize(string content) => NonWord().Replace(TextFold.Fold(content), " ").Trim();

    private static HashSet<string> Words(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3).ToHashSet(StringComparer.Ordinal);

    internal static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var shared = a.Count(b.Contains);
        return (double)shared / (a.Count + b.Count - shared);
    }

    private static DateTimeOffset Parse(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
