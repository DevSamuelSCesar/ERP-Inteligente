using System;
using System.Collections.Generic;
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Data;
using erp_inteligente.Models;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Core.Policy;

/// <summary>
/// Permission checks (references/ARQUITETURA.md, sections 12.5 and 12.6). Every check reads
/// <c>v_effective_permissions</c> (ALLOW minus DENY) live, so a permission change takes effect immediately and
/// the permission set cached in a <see cref="UserSession"/> is never trusted for an authorization decision.
/// </summary>
public sealed class PolicyEngine(NexusDatabase database, IAuditLog audit)
{
    /// <summary>Effective permissions of a role (deny-overrides applied).</summary>
    public IReadOnlySet<string> PermissionsForRole(long roleId)
    {
        using var connection = database.OpenApplication();
        return ReadPermissions(connection, roleId);
    }

    /// <summary>
    /// Effective rights of the AI acting for <paramref name="user"/>: the permissions of the AI role intersected
    /// with the user's live permissions. The AI never has more than the person who asked.
    /// </summary>
    public IReadOnlySet<string> EffectiveForAi(UserSession user)
    {
        ArgumentNullException.ThrowIfNull(user);
        using var connection = database.OpenApplication();

        var ai = new HashSet<string>(StringComparer.Ordinal);
        using (var roles = connection.CreateCommand())
        {
            roles.CommandText = "SELECT id FROM roles WHERE is_ai = 1";
            using var reader = roles.ExecuteReader();
            var ids = new List<long>();
            while (reader.Read()) ids.Add(reader.GetInt64(0));
            reader.Close();
            foreach (var id in ids) ai.UnionWith(ReadPermissions(connection, id));
        }

        ai.IntersectWith(ReadPermissions(connection, user.RoleId));
        return ai;
    }

    /// <summary>Live check of <paramref name="permission"/> for the session's role.</summary>
    public bool IsAllowed(UserSession session, string permission)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrEmpty(permission);

        using var connection = database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM v_effective_permissions WHERE role_id = $role_id AND permission = $permission LIMIT 1";
        command.Parameters.AddWithValue("$role_id", session.RoleId);
        command.Parameters.AddWithValue("$permission", permission);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Throws <see cref="PolicyDeniedException"/> unless the permission is allowed. A denial is audited
    /// (<c>policy.denied</c>, outcome denied) before the exception is thrown.
    /// </summary>
    public void Require(UserSession session, string permission)
    {
        if (IsAllowed(session, permission)) return;

        var target = permission.Length > AuditEntry.MaxTargetLength - "permission:".Length
            ? permission[..(AuditEntry.MaxTargetLength - "permission:".Length)]
            : permission;
        audit.Append(new AuditEntry(
            AuditActorType.User, session.UserId, null, "policy.denied", "permission:" + target, AuditOutcome.Denied));
        throw new PolicyDeniedException(permission);
    }

    private static HashSet<string> ReadPermissions(SqliteConnection connection, long roleId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT permission FROM v_effective_permissions WHERE role_id = $role_id";
        command.Parameters.AddWithValue("$role_id", roleId);
        var result = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }
}
