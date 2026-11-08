#pragma warning disable xUnit1051 // the CancellationToken parameters under test are optional and irrelevant here
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Auth;
using erp_inteligente.Core.Security;
using Nexus.Tests.Core.Policy;

namespace Nexus.Tests.Core.Auth;

public class AuthServiceTests : IDisposable
{
    private const string Password = "correct horse battery";
    private const string Wrong = "wrong-Password-xyz";

    private readonly PolicyTestFixture _f = new();
    private readonly PasswordHasher _cheap = new(new Argon2Parameters(1024, 1, 1));
    private readonly AuthService _auth;

    public AuthServiceTests()
    {
        var hash = _cheap.Hash(Password);
        _f.Exec("UPDATE users SET password_hash = $h WHERE id BETWEEN 1 AND 4", ("$h", hash));
        _auth = new AuthService(_f.Db.Database, _cheap, _f.Policy, _f.Audit, _f.Time, AuthOptions.Default);
    }

    public void Dispose() => _f.Dispose();

    private long Count(string action) =>
        _f.Scalar("SELECT count(*) FROM audit_log WHERE action = $a", ("$a", action));

    private long Attempts(string user) =>
        _f.Scalar("SELECT failed_attempts FROM users WHERE username = $u", ("$u", user));

    private async Task<LoginResult> Fail(string user, int times)
    {
        LoginResult last = LoginResult.Failed.Instance;
        for (var i = 0; i < times; i++) last = await _auth.LoginAsync(user, Wrong);
        return last;
    }

    [Fact]
    public async Task Success_returns_session_and_audits()
    {
        var result = await _auth.LoginAsync("sup.maria", Password);
        var ok = Assert.IsType<LoginResult.Succeeded>(result);
        Assert.Equal(2, ok.Session.UserId);
        Assert.Equal("sup.maria", ok.Session.Username);
        Assert.Equal(2, ok.Session.RoleId);
        Assert.Equal("supervisor", ok.Session.RoleName);
        Assert.Equal(1, ok.Session.WarehouseId);
        Assert.Equal("CD-01", ok.Session.WarehouseCode);
        Assert.Equal(_f.Policy.PermissionsForRole(2), ok.Session.Permissions);
        Assert.Equal(1, Count("auth.login"));
        Assert.Equal("2026-11-07T12:00:00Z", _f.Db.Database.OpenApplication().CreateCommandScalar(
            "SELECT last_login_at FROM users WHERE id = 2"));
    }

    [Fact]
    public async Task Username_is_normalized()
    {
        Assert.IsType<LoginResult.Succeeded>(await _auth.LoginAsync("  SUP.MARIA ", Password));
    }

    [Fact]
    public async Task Wrong_password_fails_and_counts()
    {
        Assert.IsType<LoginResult.Failed>(await _auth.LoginAsync("op.joao", Wrong));
        Assert.Equal(1, Attempts("op.joao"));
        Assert.Equal(1, Count("auth.login_failed"));
        Assert.Equal("Usuário ou senha inválidos", LoginResult.Failed.Instance.Message);
    }

    [Fact]
    public async Task Five_failures_lock_and_right_password_is_refused_until_expiry()
    {
        var result = await Fail("op.joao", 5);
        var locked = Assert.IsType<LoginResult.Locked>(result);
        Assert.Equal(_f.Time.UtcNow.AddMinutes(15), locked.Until);
        Assert.Equal("2026-11-07T12:15:00Z", _f.Db.Database.OpenApplication().CreateCommandScalar(
            "SELECT locked_until FROM users WHERE id = 1"));
        Assert.Equal(1, Count("auth.locked"));

        Assert.IsType<LoginResult.Locked>(await _auth.LoginAsync("op.joao", Password));
        Assert.Equal(1, Count("auth.login_locked"));

        _f.Time.Advance(TimeSpan.FromMinutes(15));
        Assert.IsType<LoginResult.Succeeded>(await _auth.LoginAsync("op.joao", Password));
        Assert.Equal(0, Attempts("op.joao"));
        Assert.Equal(0, _f.Scalar("SELECT count(*) FROM users WHERE id = 1 AND locked_until IS NOT NULL"));
    }

    [Fact]
    public async Task Success_resets_counter()
    {
        await Fail("op.joao", 3);
        Assert.Equal(3, Attempts("op.joao"));
        Assert.IsType<LoginResult.Succeeded>(await _auth.LoginAsync("op.joao", Password));
        Assert.Equal(0, Attempts("op.joao"));
    }

    [Fact]
    public async Task Unknown_user_behaves_like_real_user()
    {
        for (var i = 0; i < 4; i++)
            Assert.IsType<LoginResult.Failed>(await _auth.LoginAsync("ghost.user", Wrong));
        var locked = Assert.IsType<LoginResult.Locked>(await _auth.LoginAsync("ghost.user", Wrong));
        Assert.Equal(_f.Time.UtcNow.AddMinutes(15), locked.Until);
        Assert.IsType<LoginResult.Locked>(await _auth.LoginAsync("ghost.user", Wrong));

        _f.Time.Advance(TimeSpan.FromMinutes(15));
        Assert.IsType<LoginResult.Failed>(await _auth.LoginAsync("ghost.user", Wrong));
        Assert.DoesNotContain("ghost", AllAuditText());
    }

    [Fact]
    public async Task Inactive_ai_and_invalid_input_fail_uniformly()
    {
        _f.Exec("UPDATE users SET is_active = 0 WHERE id = 4");
        Assert.IsType<LoginResult.Failed>(await _auth.LoginAsync("aud.lucia", Password));
        Assert.IsType<LoginResult.Failed>(await _auth.LoginAsync("svc.ia", "service-account:no-password"));
        Assert.IsType<LoginResult.Failed>(await _auth.LoginAsync("", Password));
        Assert.IsType<LoginResult.Failed>(await _auth.LoginAsync("bad name!", Password));
        Assert.IsType<LoginResult.Failed>(await _auth.LoginAsync(new string('a', 65), Password));
        Assert.IsType<LoginResult.Failed>(await _auth.LoginAsync("sup.maria", new string('x', 1025)));
        Assert.IsType<LoginResult.Failed>(await _auth.LoginAsync("sup.maria", null!));
        Assert.Equal(0, Attempts("aud.lucia"));
    }

    [Fact]
    public async Task Audit_chain_valid_and_contains_no_password()
    {
        await Fail("op.joao", 2);
        await _auth.LoginAsync("op.joao", Password);
        await _auth.LoginAsync("ghost", Wrong);
        await _auth.LoginAsync("svc.ia", Wrong);
        await Fail("sup.maria", 5);

        using var connection = _f.Open();
        Assert.True(AuditVerifier.Verify(connection).IsValid);
        var text = AllAuditText();
        Assert.DoesNotContain(Password, text);
        Assert.DoesNotContain(Wrong, text);
    }

    [Fact]
    public async Task Real_seed_hash_and_default_parameters_work()
    {
        // Fresh database: users keep the seed hash (the constructor of this class replaced them, so restore one).
        using var db = new TestDatabase();
        var audit = new AuditLog(db.Database, _f.Time);
        var policy = new erp_inteligente.Core.Policy.PolicyEngine(db.Database, audit);
        var auth = new AuthService(db.Database, new PasswordHasher(Argon2Parameters.Default), policy, audit, _f.Time, AuthOptions.Default);

        Assert.IsType<LoginResult.Succeeded>(await auth.LoginAsync("sup.maria", "demo"));
        Assert.IsType<LoginResult.Failed>(await auth.LoginAsync("sup.maria", "demo2"));
    }

    [Fact]
    public void Logout_and_expiry_are_audited()
    {
        var s = _f.SessionFor("sup.maria");
        _auth.Logout(s);
        _auth.RecordSessionExpired(s);
        Assert.Equal(1, Count("auth.logout"));
        Assert.Equal(1, _f.Scalar(
            "SELECT count(*) FROM audit_log WHERE action = 'auth.session_expired' AND actor_type = 'system' AND on_behalf_of = 2"));
    }

    private string AllAuditText()
    {
        using var connection = _f.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT coalesce(target,'') || coalesce(details_json,'') || action FROM audit_log";
        using var reader = command.ExecuteReader();
        var sb = new System.Text.StringBuilder();
        while (reader.Read()) sb.Append(reader.GetString(0));
        return sb.ToString();
    }
}

internal static class SqliteTestExtensions
{
    /// <summary>Runs a scalar query and disposes the connection.</summary>
    public static string? CreateCommandScalar(this Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using (connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
