using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Policy;
using erp_inteligente.Models;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Core.Skills;

/// <summary>
/// Management of skills for the screens (application connection). Reads need <c>stock.read</c>, writes need
/// <c>skill.manage</c>; every write is audited in the same transaction, without the skill body. A database skill
/// with the name of a built-in one overrides it ("Personalizar"); built-ins themselves are never written.
/// </summary>
public sealed class SkillStore
{
    private readonly NexusDatabase _database;
    private readonly PolicyEngine _policy;
    private readonly AuditLog _audit;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<SkillDocument> _builtIns;

    /// <param name="builtIns">Built-in skills; <see cref="BuiltInSkills.All"/> when null (tests pass their own).</param>
    public SkillStore(NexusDatabase database, PolicyEngine policy, AuditLog audit, TimeProvider time,
        IReadOnlyList<SkillDocument>? builtIns = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _builtIns = builtIns ?? BuiltInSkills.All;
    }

    /// <summary>Lists built-in and database skills, sorted by name. Requires <c>stock.read</c>.</summary>
    public IReadOnlyList<SkillRecord> List(UserSession user)
    {
        ArgumentNullException.ThrowIfNull(user);
        _policy.Require(user, Permissions.StockRead);

        var rows = new List<SkillRecord>();
        using (var connection = _database.OpenApplication())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT s.name, s.description, s.operations_json, s.body, s.version, s.status, s.origin, s.updated_at, u.display_name " +
                "FROM skills s LEFT JOIN users u ON u.id = s.updated_by ORDER BY s.name";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var operations = SkillJson.ReadOperations(reader.GetString(2)) ?? [];
                var name = reader.GetString(0);
                rows.Add(new SkillRecord(
                    name, reader.GetString(1), operations, reader.GetString(3),
                    (int)reader.GetInt64(4),
                    reader.GetString(5) == "archived" ? SkillStatus.Archived : SkillStatus.Active,
                    SkillJson.OriginFromDb(reader.GetString(6)),
                    reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
                    IsBuiltIn: false, OverridesBuiltIn: _builtIns.Any(b => b.Name == name)));
            }
        }

        var result = new List<SkillRecord>(rows);
        foreach (var builtIn in _builtIns)
        {
            var overriding = rows.FirstOrDefault(r => r.Name == builtIn.Name);
            if (overriding is { Status: SkillStatus.Active }) continue; // the database skill replaces it
            result.Add(new SkillRecord(
                builtIn.Name, builtIn.Description, builtIn.Operations, builtIn.Body, 1,
                SkillStatus.Active, SkillOrigin.BuiltIn, null, null, IsBuiltIn: true, OverridesBuiltIn: false));
        }
        return result.OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Status).ToList();
    }

    /// <summary>
    /// Creates (version 1) or updates (version + 1) the database skill named like the document. Throws
    /// <see cref="PolicyDeniedException"/>, <see cref="SkillValidationException"/>, <see cref="SkillConflictException"/>
    /// or <see cref="SkillStoreException"/>. The status is kept; see <see cref="Archive"/> and <see cref="Reactivate"/>.
    /// </summary>
    /// <param name="expectedVersion">Version the caller edited; a different current version is a conflict.</param>
    public SkillRecord Save(UserSession user, SkillDocument doc, SkillOrigin origin, int? expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(doc);
        if (origin == SkillOrigin.BuiltIn)
            throw new ArgumentException("Built-in is not a valid origin for a saved skill.", nameof(origin));

        _policy.Require(user, Permissions.SkillManage);
        var clean = doc.Normalized();
        var issues = clean.Validate();
        if (issues.Count > 0) throw new SkillValidationException(issues);

        var now = AuditHasher.FormatTimestamp(_time.GetUtcNow());
        try
        {
            using var connection = _database.OpenApplication();
            using var transaction = connection.BeginTransaction(deferred: false);

            long? id = null;
            int current = 0;
            using (var select = Cmd(connection, transaction, "SELECT id, version FROM skills WHERE name = $name"))
            {
                select.Parameters.AddWithValue("$name", clean.Name);
                using var reader = select.ExecuteReader();
                if (reader.Read()) { id = reader.GetInt64(0); current = (int)reader.GetInt64(1); }
            }

            var opsJson = SkillJson.WriteOperations(clean.Operations);
            int version;
            string action;
            if (id is null)
            {
                version = 1;
                action = "skill.create";
                using var insert = Cmd(connection, transaction,
                    "INSERT INTO skills (name, description, operations_json, body, version, status, origin, created_by, created_at, updated_by, updated_at) " +
                    "VALUES ($name, $description, $ops, $body, 1, 'active', $origin, $user, $at, $user, $at) RETURNING id");
                insert.Parameters.AddWithValue("$name", clean.Name);
                insert.Parameters.AddWithValue("$description", clean.Description);
                insert.Parameters.AddWithValue("$ops", opsJson);
                insert.Parameters.AddWithValue("$body", clean.Body);
                insert.Parameters.AddWithValue("$origin", origin.DbValue());
                insert.Parameters.AddWithValue("$user", user.UserId);
                insert.Parameters.AddWithValue("$at", now);
                id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            else
            {
                if (expectedVersion is { } expected && expected != current) throw new SkillConflictException();
                version = current + 1;
                action = "skill.update";
                using var update = Cmd(connection, transaction,
                    "UPDATE skills SET description = $description, operations_json = $ops, body = $body, version = $version, " +
                    "origin = $origin, updated_by = $user, updated_at = $at WHERE id = $id AND version = $current");
                update.Parameters.AddWithValue("$description", clean.Description);
                update.Parameters.AddWithValue("$ops", opsJson);
                update.Parameters.AddWithValue("$body", clean.Body);
                update.Parameters.AddWithValue("$version", version);
                update.Parameters.AddWithValue("$origin", origin.DbValue());
                update.Parameters.AddWithValue("$user", user.UserId);
                update.Parameters.AddWithValue("$at", now);
                update.Parameters.AddWithValue("$id", id.Value);
                update.Parameters.AddWithValue("$current", current);
                if (update.ExecuteNonQuery() != 1) throw new SkillConflictException();
            }

            using (var history = Cmd(connection, transaction,
                "INSERT INTO skill_versions (skill_id, version, description, operations_json, body, origin, saved_by, saved_at) " +
                "VALUES ($id, $version, $description, $ops, $body, $origin, $user, $at)"))
            {
                history.Parameters.AddWithValue("$id", id.Value);
                history.Parameters.AddWithValue("$version", version);
                history.Parameters.AddWithValue("$description", clean.Description);
                history.Parameters.AddWithValue("$ops", opsJson);
                history.Parameters.AddWithValue("$body", clean.Body);
                history.Parameters.AddWithValue("$origin", origin.DbValue());
                history.Parameters.AddWithValue("$user", user.UserId);
                history.Parameters.AddWithValue("$at", now);
                history.ExecuteNonQuery();
            }

            AppendAudit(connection, transaction, user, action, clean.Name, version, origin);
            transaction.Commit();
        }
        catch (SqliteException ex)
        {
            throw Map(ex);
        }

        return Get(clean.Name)!;
    }

    /// <summary>Archives a database skill (a built-in cannot be archived). Requires <c>skill.manage</c>.</summary>
    public void Archive(UserSession user, string name) => SetStatus(user, name, archive: true);

    /// <summary>Reactivates an archived database skill. Requires <c>skill.manage</c>.</summary>
    public void Reactivate(UserSession user, string name) => SetStatus(user, name, archive: false);

    /// <summary>Versions of a database skill, newest first. Requires <c>stock.read</c>.</summary>
    public IReadOnlyList<SkillVersionRecord> History(UserSession user, string name)
    {
        ArgumentNullException.ThrowIfNull(user);
        _policy.Require(user, Permissions.StockRead);
        if (!SkillDocument.IsValidName(name)) return [];

        var list = new List<SkillVersionRecord>();
        using var connection = _database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT v.version, v.description, v.operations_json, v.body, v.origin, u.display_name, v.saved_at " +
            "FROM skill_versions v JOIN skills s ON s.id = v.skill_id LEFT JOIN users u ON u.id = v.saved_by " +
            "WHERE s.name = $name ORDER BY v.version DESC";
        command.Parameters.AddWithValue("$name", name);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            list.Add(new SkillVersionRecord(
                (int)reader.GetInt64(0), reader.GetString(1), SkillJson.ReadOperations(reader.GetString(2)) ?? [],
                reader.GetString(3), SkillJson.OriginFromDb(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6)));
        return list;
    }

    private SkillRecord? Get(string name)
    {
        using var connection = _database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT s.name, s.description, s.operations_json, s.body, s.version, s.status, s.origin, s.updated_at, u.display_name " +
            "FROM skills s LEFT JOIN users u ON u.id = s.updated_by WHERE s.name = $name";
        command.Parameters.AddWithValue("$name", name);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new SkillRecord(
            reader.GetString(0), reader.GetString(1), SkillJson.ReadOperations(reader.GetString(2)) ?? [], reader.GetString(3),
            (int)reader.GetInt64(4), reader.GetString(5) == "archived" ? SkillStatus.Archived : SkillStatus.Active,
            SkillJson.OriginFromDb(reader.GetString(6)), reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8), false, _builtIns.Any(b => b.Name == name));
    }

    private void SetStatus(UserSession user, string name, bool archive)
    {
        ArgumentNullException.ThrowIfNull(user);
        _policy.Require(user, Permissions.SkillManage);
        if (!SkillDocument.IsValidName(name)) throw new SkillStoreException("Nome de skill inválido.");

        try
        {
            using var connection = _database.OpenApplication();
            using var transaction = connection.BeginTransaction(deferred: false);
            long? id = null;
            int version = 0;
            string status = "";
            SkillOrigin origin = SkillOrigin.User;
            using (var select = Cmd(connection, transaction, "SELECT id, version, status, origin FROM skills WHERE name = $name"))
            {
                select.Parameters.AddWithValue("$name", name);
                using var reader = select.ExecuteReader();
                if (reader.Read())
                {
                    id = reader.GetInt64(0);
                    version = (int)reader.GetInt64(1);
                    status = reader.GetString(2);
                    origin = SkillJson.OriginFromDb(reader.GetString(3));
                }
            }

            if (id is null)
                throw new SkillStoreException(_builtIns.Any(b => b.Name == name)
                    ? "Skills nativas não podem ser arquivadas. Personalize a skill para poder alterá-la."
                    : "Skill não encontrada.");
            var target = archive ? "archived" : "active";
            if (status == target)
                throw new SkillStoreException(archive ? "A skill já está arquivada." : "A skill já está ativa.");

            using (var update = Cmd(connection, transaction,
                "UPDATE skills SET status = $status, updated_by = $user, updated_at = $at WHERE id = $id"))
            {
                update.Parameters.AddWithValue("$status", target);
                update.Parameters.AddWithValue("$user", user.UserId);
                update.Parameters.AddWithValue("$at", AuditHasher.FormatTimestamp(_time.GetUtcNow()));
                update.Parameters.AddWithValue("$id", id.Value);
                update.ExecuteNonQuery();
            }
            AppendAudit(connection, transaction, user, archive ? "skill.archive" : "skill.reactivate", name, version, origin);
            transaction.Commit();
        }
        catch (SqliteException ex)
        {
            throw Map(ex);
        }
    }

    private void AppendAudit(SqliteConnection connection, SqliteTransaction transaction, UserSession user,
        string action, string name, int version, SkillOrigin origin)
    {
        var details = new JsonObject { ["version"] = version, ["origin"] = origin.DbValue() };
        _audit.AppendWithin(connection, transaction, new AuditEntry(
            AuditActorType.User, user.UserId, null, action, "skill:" + name, AuditOutcome.Allowed, details.ToJsonString()));
    }

    private static SqliteCommand Cmd(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    /// <summary>Maps a SQLite error to a Portuguese message; the SQL text is never exposed.</summary>
    private static SkillStoreException Map(SqliteException ex) => ex.SqliteExtendedErrorCode switch
    {
        2067 or 1555 => new SkillStoreException("Já existe uma skill com esse nome.", ex),
        275 or 19 => new SkillStoreException("Os dados da skill violam uma regra do banco de dados.", ex),
        5 or 6 => new SkillStoreException("O banco de dados está ocupado. Tente novamente.", ex),
        _ when ex.SqliteErrorCode == 19 => new SkillStoreException("Os dados da skill violam uma regra do banco de dados.", ex),
        _ => new SkillStoreException("Não foi possível salvar a skill.", ex),
    };
}
