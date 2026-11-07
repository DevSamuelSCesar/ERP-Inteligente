using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Policy;
using erp_inteligente.Core.Security;
using erp_inteligente.Models;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Core.Auth;

/// <summary>
/// Local username/password authentication (references/ARQUITETURA.md, section 12.5): Argon2id verification,
/// temporary lockout, a single failure message and an audit event for every outcome. Passwords and attempted
/// usernames of unknown accounts are never written to the audit log.
/// </summary>
public sealed class AuthService(
    NexusDatabase database,
    PasswordHasher hasher,
    PolicyEngine policy,
    AuditLog audit,
    TimeProvider time,
    AuthOptions options)
{
    private const int MaxUsernameLength = 64;
    private const int MaxTrackedUnknownUsers = 10_000;
    private static readonly Regex UsernamePattern = new(@"^[a-z0-9._-]+\z", RegexOptions.CultureInvariant);

    private readonly object _throttleGate = new();
    private readonly Dictionary<string, (int Count, DateTimeOffset? LockedUntil)> _unknown = new(StringComparer.Ordinal);

    /// <summary>Authenticates a user. Argon2 runs on the thread pool so the UI thread is never blocked.</summary>
    public Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default) =>
        Task.Run(() => Login(username, password), ct);

    /// <summary>Audits a sign-out.</summary>
    public void Logout(UserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        audit.Append(new AuditEntry(AuditActorType.User, session.UserId, null, "auth.logout", UserTarget(session.UserId), AuditOutcome.Allowed));
    }

    /// <summary>Audits an idle-timeout expiry (actor: system, on behalf of the user).</summary>
    public void RecordSessionExpired(UserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        audit.Append(new AuditEntry(AuditActorType.System, null, session.UserId, "auth.session_expired", UserTarget(session.UserId), AuditOutcome.Allowed));
    }

    private LoginResult Login(string username, string password)
    {
        var name = Normalize(username);
        if (name is null || password is null || password.Length > PasswordHasher.MaxPasswordLength)
        {
            hasher.VerifyDummy("invalid-input");
            AuditAnonymous("auth.login_failed", AuditOutcome.Failed, "invalid_input");
            return LoginResult.Failed.Instance;
        }

        var user = LoadUser(name);
        if (user is null) return UnknownUser(name, password);

        var now = time.GetUtcNow();
        if (user.LockedUntil is { } until && until > now)
        {
            hasher.VerifyDummy(password);
            audit.Append(new AuditEntry(AuditActorType.User, user.Id, null, "auth.login_locked", UserTarget(user.Id), AuditOutcome.Denied));
            return new LoginResult.Locked(until);
        }

        if (!user.IsActive || user.IsAi || user.WarehouseId is null || user.WarehouseCode is null)
        {
            hasher.VerifyDummy(password);
            var reason = !user.IsActive ? "inactive" : user.IsAi ? "service_account" : "no_warehouse";
            AuditAnonymous("auth.login_failed", AuditOutcome.Denied, reason, user.Id);
            return LoginResult.Failed.Instance;
        }

        return hasher.Verify(password, user.PasswordHash)
            ? CompleteLogin(user)
            : RegisterFailure(user);
    }

    private LoginResult UnknownUser(string name, string password)
    {
        hasher.VerifyDummy(password);
        var now = time.GetUtcNow();
        DateTimeOffset? lockedUntil = null;
        lock (_throttleGate)
        {
            _unknown.TryGetValue(name, out var state);
            if (state.LockedUntil is { } active && active > now)
            {
                lockedUntil = active;
            }
            else
            {
                if (_unknown.Count >= MaxTrackedUnknownUsers && !_unknown.ContainsKey(name)) PruneUnknown(now);
                var count = state.LockedUntil is null ? state.Count + 1 : 1; // an expired lock starts a new window
                if (count >= options.MaxFailedAttempts)
                {
                    lockedUntil = TruncateToSecond(now + options.LockoutDuration);
                    _unknown[name] = (0, lockedUntil);
                }
                else
                {
                    _unknown[name] = (count, null);
                }
            }
        }

        if (lockedUntil is { } until)
        {
            AuditAnonymous("auth.login_locked", AuditOutcome.Denied, "unknown_user");
            return new LoginResult.Locked(until);
        }
        AuditAnonymous("auth.login_failed", AuditOutcome.Failed, "unknown_user");
        return LoginResult.Failed.Instance;
    }

    private void PruneUnknown(DateTimeOffset now)
    {
        // Called under _throttleGate. Drop expired locks first; if still full, drop everything (bounded memory).
        var expired = new List<string>();
        foreach (var (key, state) in _unknown)
            if (state.LockedUntil is { } until && until <= now) expired.Add(key);
        foreach (var key in expired) _unknown.Remove(key);
        if (_unknown.Count >= MaxTrackedUnknownUsers) _unknown.Clear();
    }

    private LoginResult RegisterFailure(UserRow user)
    {
        var now = time.GetUtcNow();
        using var connection = database.OpenApplication();
        using var transaction = connection.BeginTransaction(deferred: false);

        long attempts;
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE users SET failed_attempts = failed_attempts + 1 WHERE id = $id RETURNING failed_attempts";
            update.Parameters.AddWithValue("$id", user.Id);
            attempts = Convert.ToInt64(update.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        LoginResult result;
        if (attempts >= options.MaxFailedAttempts)
        {
            var until = now + options.LockoutDuration;
            using var lockCommand = connection.CreateCommand();
            lockCommand.Transaction = transaction;
            lockCommand.CommandText = "UPDATE users SET failed_attempts = 0, locked_until = $until WHERE id = $id";
            lockCommand.Parameters.AddWithValue("$until", AuditHasher.FormatTimestamp(until));
            lockCommand.Parameters.AddWithValue("$id", user.Id);
            lockCommand.ExecuteNonQuery();
            audit.AppendWithin(connection, transaction, new AuditEntry(
                AuditActorType.User, user.Id, null, "auth.locked", UserTarget(user.Id), AuditOutcome.Denied));
            result = new LoginResult.Locked(TruncateToSecond(until));
        }
        else
        {
            audit.AppendWithin(connection, transaction, new AuditEntry(
                AuditActorType.User, user.Id, null, "auth.login_failed", UserTarget(user.Id), AuditOutcome.Denied,
                """{"reason":"bad_password"}"""));
            result = LoginResult.Failed.Instance;
        }
        transaction.Commit();
        return result;
    }

    private LoginResult CompleteLogin(UserRow user)
    {
        var now = time.GetUtcNow();
        using var connection = database.OpenApplication();
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE users SET failed_attempts = 0, locked_until = NULL, last_login_at = $now WHERE id = $id";
            update.Parameters.AddWithValue("$now", AuditHasher.FormatTimestamp(now));
            update.Parameters.AddWithValue("$id", user.Id);
            update.ExecuteNonQuery();
        }
        audit.AppendWithin(connection, transaction, new AuditEntry(
            AuditActorType.User, user.Id, null, "auth.login", UserTarget(user.Id), AuditOutcome.Allowed));
        transaction.Commit();

        var session = new UserSession(
            user.Id, user.Username, user.DisplayName, user.RoleId, user.RoleName,
            user.WarehouseId!.Value, user.WarehouseCode!, policy.PermissionsForRole(user.RoleId));
        return new LoginResult.Succeeded(session);
    }

    private UserRow? LoadUser(string username)
    {
        using var connection = database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT u.id, u.username, u.display_name, u.password_hash, u.role_id, r.name, r.is_ai, " +
            "u.warehouse_id, w.code, u.is_active, u.failed_attempts, u.locked_until " +
            "FROM users u JOIN roles r ON r.id = u.role_id LEFT JOIN warehouses w ON w.id = u.warehouse_id " +
            "WHERE u.username = $username";
        command.Parameters.AddWithValue("$username", username);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new UserRow(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt64(4), reader.GetString(5), reader.GetInt64(6) == 1,
            reader.IsDBNull(7) ? null : reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetInt64(9) == 1, reader.GetInt64(10), ParseTimestamp(reader.IsDBNull(11) ? null : reader.GetString(11)));
    }

    private void AuditAnonymous(string action, AuditOutcome outcome, string reason, long? userId = null) =>
        audit.Append(new AuditEntry(
            AuditActorType.User, userId, null, action, userId is { } id ? UserTarget(id) : null, outcome,
            "{\"reason\":\"" + reason + "\"}"));

    private static string? Normalize(string? username)
    {
        if (username is null) return null;
        var name = username.Trim().ToLowerInvariant();
        return name.Length is > 0 and <= MaxUsernameLength && UsernamePattern.IsMatch(name) ? name : null;
    }

    private static string UserTarget(long id) => "user:" + id.ToString(CultureInfo.InvariantCulture);

    private static DateTimeOffset TruncateToSecond(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private static DateTimeOffset? ParseTimestamp(string? text) =>
        text is not null
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? value
            : null;

    private sealed record UserRow(
        long Id, string Username, string DisplayName, string PasswordHash, long RoleId, string RoleName, bool IsAi,
        long? WarehouseId, string? WarehouseCode, bool IsActive, long FailedAttempts, DateTimeOffset? LockedUntil);
}
