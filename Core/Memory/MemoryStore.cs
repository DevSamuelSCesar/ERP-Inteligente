using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Policy;
using erp_inteligente.Models;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Core.Memory;

/// <summary>
/// Writes and manages memories (application connection). Every write passes <see cref="MemoryFilter"/> and
/// is audited in the same transaction, never with the memory text (only ids and short codes). Memories belong
/// to the user's warehouse; another warehouse's ids are reported as "not found". Reads need
/// <c>stock.read</c>. Rules by origin for <see cref="Remember"/>:
/// <list type="bullet">
/// <item><c>Ai</c>: needs <c>memory.write</c> in the AI's effective rights (AI role intersected with the user's).</item>
/// <item><c>System</c>: no permission check. Used by trusted engine code (for example when a plan is rejected),
/// which has no model-controlled text; the filter still applies.</item>
/// <item><c>User</c>: needs <c>memory.manage</c> (supervisor).</item>
/// </list>
/// </summary>
public sealed class MemoryStore
{
    public const double AiConfidence = 0.3;
    public const double SystemConfidence = 0.6;
    public const double UserConfidence = 0.8;
    public const double MaxReinforcedConfidence = 0.9;
    private const double ReinforceStep = 0.1;

    // \z instead of $: "$" would also accept a trailing newline.
    private static readonly Regex ScopeRefPattern = new(@"^[A-Za-z0-9_\-]{1,60}\z", RegexOptions.CultureInvariant);

    private readonly NexusDatabase _database;
    private readonly PolicyEngine _policy;
    private readonly AuditLog _audit;
    private readonly TimeProvider _time;

    public MemoryStore(NexusDatabase database, PolicyEngine policy, AuditLog audit, TimeProvider time)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>
    /// Saves a memory, or reinforces the active one with the same warehouse, scope, reference and (folded)
    /// content. A refused content or scope reference returns a result with <c>Saved = false</c> and a pt-BR
    /// reason. Throws <see cref="PolicyDeniedException"/> when the origin's permission is missing.
    /// </summary>
    public MemoryWriteResult Remember(UserSession user, MemoryKind kind, MemoryScope scope, string? scopeRef,
        string content, MemoryOrigin origin, long? sourcePlanId = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        Authorize(user, origin, Permissions.MemoryWrite, Permissions.MemoryManage);

        var target = "warehouse:" + user.WarehouseId.ToString(CultureInfo.InvariantCulture);
        var filtered = MemoryFilter.Check(content);
        if (!filtered.Accepted)
        {
            AuditRefusal(user, origin, "memory.create", target, filtered.Code!);
            return MemoryWriteResult.Refused(filtered.Reason!);
        }
        var refCheck = NormalizeScopeRef(scope, scopeRef, out var cleanRef);
        if (refCheck is not null)
        {
            AuditRefusal(user, origin, "memory.create", target, "scope_ref");
            return MemoryWriteResult.Refused(refCheck);
        }

        var now = AuditHasher.FormatTimestamp(_time.GetUtcNow());
        var folded = TextFold.Fold(filtered.Text);
        try
        {
            using var connection = _database.OpenApplication();
            using var transaction = connection.BeginTransaction(deferred: false);

            long? existingId = null;
            double existingConfidence = 0;
            using (var select = Cmd(connection, transaction,
                "SELECT id, content, confidence FROM memories " +
                "WHERE warehouse_id = $w AND scope = $scope AND scope_ref IS $ref AND status = 'active'"))
            {
                select.Parameters.AddWithValue("$w", user.WarehouseId);
                select.Parameters.AddWithValue("$scope", scope.DbValue());
                select.Parameters.AddWithValue("$ref", (object?)cleanRef ?? DBNull.Value);
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    if (TextFold.Fold(reader.GetString(1)) != folded) continue;
                    existingId = reader.GetInt64(0);
                    existingConfidence = reader.GetDouble(2);
                    break;
                }
            }

            long id;
            bool reinforced;
            string action;
            if (existingId is { } found)
            {
                id = found;
                reinforced = true;
                action = "memory.reinforce";
                using var update = Cmd(connection, transaction,
                    "UPDATE memories SET confidence = $confidence, updated_at = $at WHERE id = $id AND warehouse_id = $w");
                update.Parameters.AddWithValue("$confidence", Math.Min(MaxReinforcedConfidence, existingConfidence + ReinforceStep));
                update.Parameters.AddWithValue("$at", now);
                update.Parameters.AddWithValue("$id", id);
                update.Parameters.AddWithValue("$w", user.WarehouseId);
                update.ExecuteNonQuery();
            }
            else
            {
                reinforced = false;
                action = "memory.create";
                using var insert = Cmd(connection, transaction,
                    "INSERT INTO memories (warehouse_id, kind, scope, scope_ref, content, confidence, use_count, status, origin, " +
                    "created_by, source_plan_id, created_at) " +
                    "VALUES ($w, $kind, $scope, $ref, $content, $confidence, 0, 'active', $origin, $user, $plan, $at) RETURNING id");
                insert.Parameters.AddWithValue("$w", user.WarehouseId);
                insert.Parameters.AddWithValue("$kind", kind.DbValue());
                insert.Parameters.AddWithValue("$scope", scope.DbValue());
                insert.Parameters.AddWithValue("$ref", (object?)cleanRef ?? DBNull.Value);
                insert.Parameters.AddWithValue("$content", filtered.Text);
                insert.Parameters.AddWithValue("$confidence", InitialConfidence(origin));
                insert.Parameters.AddWithValue("$origin", origin.DbValue());
                insert.Parameters.AddWithValue("$user", origin == MemoryOrigin.System ? DBNull.Value : user.UserId);
                insert.Parameters.AddWithValue("$plan", (object?)sourcePlanId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$at", now);
                id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            var details = new JsonObject { ["id"] = id, ["origin"] = origin.DbValue(), ["scope"] = scope.DbValue() };
            AppendAudit(connection, transaction, user, origin, action, "memory:" + id.ToString(CultureInfo.InvariantCulture),
                AuditOutcome.Allowed, details);
            transaction.Commit();
            return new MemoryWriteResult(true, id, filtered.Text, reinforced, null);
        }
        catch (SqliteException ex)
        {
            throw Map(ex);
        }
    }

    /// <summary>Creates a memory written by a person (origin <c>user</c>). Requires <c>memory.manage</c>.</summary>
    public MemoryWriteResult Create(UserSession user, MemoryKind kind, MemoryScope scope, string? scopeRef, string content) =>
        Remember(user, kind, scope, scopeRef, content, MemoryOrigin.User);

    /// <summary>All memories of the user's warehouse, archived included, newest first. Requires <c>stock.read</c>.</summary>
    public IReadOnlyList<MemoryRecord> List(UserSession user)
    {
        ArgumentNullException.ThrowIfNull(user);
        _policy.Require(user, Permissions.StockRead);

        var rows = new List<MemoryRecord>();
        using var connection = _database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT m.id, m.kind, m.scope, m.scope_ref, m.content, m.confidence, m.use_count, m.status, m.origin, " +
            "u.display_name, m.source_plan_id, m.created_at, m.updated_at, m.last_used_at " +
            "FROM memories m LEFT JOIN users u ON u.id = m.created_by " +
            "WHERE m.warehouse_id = $w ORDER BY m.created_at DESC, m.id DESC";
        command.Parameters.AddWithValue("$w", user.WarehouseId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add(new MemoryRecord(
                reader.GetInt64(0), MemoryDb.KindFromDb(reader.GetString(1)), MemoryDb.ScopeFromDb(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetDouble(5), reader.GetInt64(6),
                reader.GetString(7) == "archived" ? MemoryStatus.Archived : MemoryStatus.Active,
                MemoryDb.OriginFromDb(reader.GetString(8)), reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetInt64(10), reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13)));
        return rows;
    }

    /// <summary>
    /// Corrects the text of a memory (the filter runs again). Requires <c>memory.manage</c>. A refused text
    /// returns <c>Saved = false</c>; an unknown id (or one of another warehouse) throws <see cref="MemoryStoreException"/>.
    /// </summary>
    public MemoryWriteResult Update(UserSession user, long id, string content)
    {
        ArgumentNullException.ThrowIfNull(user);
        _policy.Require(user, Permissions.MemoryManage);

        var target = "memory:" + id.ToString(CultureInfo.InvariantCulture);
        var filtered = MemoryFilter.Check(content);
        if (!filtered.Accepted)
        {
            AuditRefusal(user, MemoryOrigin.User, "memory.update", target, filtered.Code!);
            return MemoryWriteResult.Refused(filtered.Reason!);
        }

        try
        {
            using var connection = _database.OpenApplication();
            using var transaction = connection.BeginTransaction(deferred: false);
            using (var update = Cmd(connection, transaction,
                "UPDATE memories SET content = $content, updated_at = $at WHERE id = $id AND warehouse_id = $w"))
            {
                update.Parameters.AddWithValue("$content", filtered.Text);
                update.Parameters.AddWithValue("$at", AuditHasher.FormatTimestamp(_time.GetUtcNow()));
                update.Parameters.AddWithValue("$id", id);
                update.Parameters.AddWithValue("$w", user.WarehouseId);
                if (update.ExecuteNonQuery() != 1) throw NotFound();
            }
            AppendAudit(connection, transaction, user, MemoryOrigin.User, "memory.update", target, AuditOutcome.Allowed,
                new JsonObject { ["id"] = id });
            transaction.Commit();
            return new MemoryWriteResult(true, id, filtered.Text, false, null);
        }
        catch (SqliteException ex)
        {
            throw Map(ex);
        }
    }

    /// <summary>Archives an active memory (it stops being retrieved). Requires <c>memory.manage</c>.</summary>
    public void Archive(UserSession user, long id) => SetStatus(user, id, archive: true);

    /// <summary>Reactivates an archived memory. Requires <c>memory.manage</c>.</summary>
    public void Reactivate(UserSession user, long id) => SetStatus(user, id, archive: false);

    /// <summary>
    /// Counts a use for each id (use_count + 1, last_used_at). Only rows of the user's warehouse are touched;
    /// other ids are ignored. Requires <c>stock.read</c>. Not audited: it is usage telemetry, not a decision.
    /// </summary>
    public void MarkUsed(UserSession user, IEnumerable<long> ids)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(ids);
        _policy.Require(user, Permissions.StockRead);

        try
        {
            using var connection = _database.OpenApplication();
            using var transaction = connection.BeginTransaction(deferred: false);
            var at = AuditHasher.FormatTimestamp(_time.GetUtcNow());
            foreach (var id in new HashSet<long>(ids))
            {
                using var command = Cmd(connection, transaction,
                    "UPDATE memories SET use_count = use_count + 1, last_used_at = $at WHERE id = $id AND warehouse_id = $w");
                command.Parameters.AddWithValue("$at", at);
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$w", user.WarehouseId);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        catch (SqliteException ex)
        {
            throw Map(ex);
        }
    }

    private void SetStatus(UserSession user, long id, bool archive)
    {
        ArgumentNullException.ThrowIfNull(user);
        _policy.Require(user, Permissions.MemoryManage);

        try
        {
            using var connection = _database.OpenApplication();
            using var transaction = connection.BeginTransaction(deferred: false);
            string? status = null;
            using (var select = Cmd(connection, transaction, "SELECT status FROM memories WHERE id = $id AND warehouse_id = $w"))
            {
                select.Parameters.AddWithValue("$id", id);
                select.Parameters.AddWithValue("$w", user.WarehouseId);
                status = select.ExecuteScalar() as string;
            }
            if (status is null) throw NotFound();
            var target = archive ? "archived" : "active";
            if (status == target)
                throw new MemoryStoreException(archive ? "A memória já está arquivada." : "A memória já está ativa.");

            using (var update = Cmd(connection, transaction,
                "UPDATE memories SET status = $status, updated_at = $at WHERE id = $id AND warehouse_id = $w"))
            {
                update.Parameters.AddWithValue("$status", target);
                update.Parameters.AddWithValue("$at", AuditHasher.FormatTimestamp(_time.GetUtcNow()));
                update.Parameters.AddWithValue("$id", id);
                update.Parameters.AddWithValue("$w", user.WarehouseId);
                update.ExecuteNonQuery();
            }
            AppendAudit(connection, transaction, user, MemoryOrigin.User, archive ? "memory.archive" : "memory.reactivate",
                "memory:" + id.ToString(CultureInfo.InvariantCulture), AuditOutcome.Allowed, new JsonObject { ["id"] = id });
            transaction.Commit();
        }
        catch (SqliteException ex)
        {
            throw Map(ex);
        }
    }

    private void Authorize(UserSession user, MemoryOrigin origin, string writePermission, string managePermission)
    {
        switch (origin)
        {
            case MemoryOrigin.System:
                return; // trusted engine code; see the class remarks
            case MemoryOrigin.User:
                _policy.Require(user, managePermission);
                return;
            case MemoryOrigin.Ai:
                if (_policy.EffectiveForAi(user).Contains(writePermission)) return;
                _audit.Append(new AuditEntry(AuditActorType.Ai, user.UserId, user.UserId, "policy.denied",
                    "permission:" + writePermission, AuditOutcome.Denied));
                throw new PolicyDeniedException(writePermission);
            default:
                throw new ArgumentOutOfRangeException(nameof(origin), origin, null);
        }
    }

    private static double InitialConfidence(MemoryOrigin origin) => origin switch
    {
        MemoryOrigin.Ai => AiConfidence,
        MemoryOrigin.System => SystemConfidence,
        _ => UserConfidence,
    };

    /// <summary>Validates the scope reference; returns a pt-BR message or null. Sku, location and zone references are upper-cased.</summary>
    private static string? NormalizeScopeRef(MemoryScope scope, string? scopeRef, out string? clean)
    {
        clean = null;
        var value = scopeRef?.Trim();
        if (scope == MemoryScope.Global)
            return string.IsNullOrEmpty(value) ? null : "Memórias globais não têm referência de escopo.";
        if (string.IsNullOrEmpty(value))
            return "Informe a referência do escopo (SKU, endereço, zona, skill, fornecedor ou cliente).";
        if (!ScopeRefPattern.IsMatch(value))
            return "A referência do escopo deve ter de 1 a 60 caracteres: letras, números, hífen e sublinhado.";
        clean = scope is MemoryScope.Sku or MemoryScope.Location or MemoryScope.Zone
            ? value.ToUpperInvariant()
            : value;
        return null;
    }

    private void AuditRefusal(UserSession user, MemoryOrigin origin, string action, string target, string code) =>
        _audit.Append(AuditEntryFor(user, origin, action, target, AuditOutcome.Denied, new JsonObject { ["code"] = code }));

    private void AppendAudit(SqliteConnection connection, SqliteTransaction transaction, UserSession user,
        MemoryOrigin origin, string action, string target, AuditOutcome outcome, JsonObject details) =>
        _audit.AppendWithin(connection, transaction, AuditEntryFor(user, origin, action, target, outcome, details));

    private static AuditEntry AuditEntryFor(UserSession user, MemoryOrigin origin, string action, string target,
        AuditOutcome outcome, JsonObject details) => origin switch
    {
        MemoryOrigin.Ai => new AuditEntry(AuditActorType.Ai, user.UserId, user.UserId, action, target, outcome, details.ToJsonString()),
        MemoryOrigin.System => new AuditEntry(AuditActorType.System, null, user.UserId, action, target, outcome, details.ToJsonString()),
        _ => new AuditEntry(AuditActorType.User, user.UserId, null, action, target, outcome, details.ToJsonString()),
    };

    private static MemoryStoreException NotFound() => new("Memória não encontrada.");

    private static SqliteCommand Cmd(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    /// <summary>Maps a SQLite error to a Portuguese message; the SQL text is never exposed.</summary>
    private static MemoryStoreException Map(SqliteException ex) => ex.SqliteErrorCode switch
    {
        19 => new MemoryStoreException("Os dados da memória violam uma regra do banco de dados.", ex),
        5 or 6 => new MemoryStoreException("O banco de dados está ocupado. Tente novamente.", ex),
        _ => new MemoryStoreException("Não foi possível salvar a memória.", ex),
    };
}
