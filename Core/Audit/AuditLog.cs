using System;
using erp_inteligente.Core.Data;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Core.Audit;

/// <summary>
/// Writer of the hash-chained <c>audit_log</c> (references/ARQUITETURA.md, section 12.7). The table is append-only
/// by trigger, so this class is the only path that adds rows and computes their hashes.
/// </summary>
public sealed class AuditLog(NexusDatabase database, TimeProvider time) : IAuditLog
{
    /// <summary>Opens an application connection, appends one row in a <c>BEGIN IMMEDIATE</c> transaction and commits.</summary>
    public long Append(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        using var connection = database.OpenApplication();
        using var transaction = connection.BeginTransaction(deferred: false);
        var id = AppendWithin(connection, transaction, entry);
        transaction.Commit();
        return id;
    }

    /// <summary>
    /// Appends one row inside a transaction owned by the caller, so that the caller's own rows and the audit row
    /// commit or roll back together. The transaction must be an <c>IMMEDIATE</c> one. Returns the new row id.
    /// </summary>
    public long AppendWithin(SqliteConnection connection, SqliteTransaction transaction, AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(entry);
        entry.EnsureValid();

        long lastId = 0;
        string prevHash = AuditHasher.Genesis;
        using (var last = connection.CreateCommand())
        {
            last.Transaction = transaction;
            last.CommandText = "SELECT id, hash FROM audit_log ORDER BY id DESC LIMIT 1";
            using var reader = last.ExecuteReader();
            if (reader.Read())
            {
                lastId = reader.GetInt64(0);
                prevHash = reader.GetString(1);
            }
        }

        var id = lastId + 1;
        var occurredAt = AuditHasher.FormatTimestamp(time.GetUtcNow());
        var actorType = entry.ActorType.ToDbValue();
        var outcome = entry.Outcome.ToDbValue();
        var hash = AuditHasher.ComputeV1(
            prevHash, id, occurredAt, actorType, entry.ActorId, entry.OnBehalfOf,
            entry.Action, entry.Target, outcome, entry.DetailsJson);

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT INTO audit_log (id, occurred_at, actor_type, actor_id, on_behalf_of, action, target, " +
            "outcome, details_json, prev_hash, hash) " +
            "VALUES ($id, $occurred_at, $actor_type, $actor_id, $on_behalf_of, $action, $target, " +
            "$outcome, $details_json, $prev_hash, $hash)";
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$occurred_at", occurredAt);
        insert.Parameters.AddWithValue("$actor_type", actorType);
        insert.Parameters.AddWithValue("$actor_id", (object?)entry.ActorId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$on_behalf_of", (object?)entry.OnBehalfOf ?? DBNull.Value);
        insert.Parameters.AddWithValue("$action", entry.Action);
        insert.Parameters.AddWithValue("$target", (object?)entry.Target ?? DBNull.Value);
        insert.Parameters.AddWithValue("$outcome", outcome);
        insert.Parameters.AddWithValue("$details_json", (object?)entry.DetailsJson ?? DBNull.Value);
        insert.Parameters.AddWithValue("$prev_hash", prevHash);
        insert.Parameters.AddWithValue("$hash", hash);
        insert.ExecuteNonQuery();

        return id;
    }
}
