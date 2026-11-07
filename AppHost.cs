using System;
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Auth;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Policy;
using erp_inteligente.Core.Security;
using erp_inteligente.Services;
using erp_inteligente.Services.Sqlite;
using erp_inteligente.ViewModels;

namespace erp_inteligente;

/// <summary>
/// Composition root of the core (ROADMAP.md, feature 1): the encrypted database, audit log, policy engine,
/// authentication and session. One instance per running app; disposing it zeroes the database keys.
/// </summary>
public sealed class AppHost : IDisposable
{
    public AppHost(NexusDatabase database, TimeProvider time, Argon2Parameters passwordParameters, AuthOptions authOptions)
    {
        Database = database;
        Time = time;
        AuditLog = new AuditLog(database, time);
        Policy = new PolicyEngine(database, AuditLog);
        ApplicationDb = new ApplicationDb(database, Policy);
        Auth = new AuthService(database, new PasswordHasher(passwordParameters), Policy, AuditLog, time, authOptions);
        Session = new SessionService(Auth, time, authOptions);
        AuditStatus = VerifyAudit();
    }

    public NexusDatabase Database { get; }
    public TimeProvider Time { get; }
    public AuditLog AuditLog { get; }
    public PolicyEngine Policy { get; }
    public ApplicationDb ApplicationDb { get; }
    public AuthService Auth { get; }
    public SessionService Session { get; }

    /// <summary>Last verification of the audit hash chain (at startup and at every login).</summary>
    public AuditVerification AuditStatus { get; private set; }

    /// <summary>Opens (or creates on first run) the database at <paramref name="databasePath"/> with keys from <paramref name="store"/>.</summary>
    public static AppHost Open(string databasePath, ISecretStore store) =>
        new(DatabaseBootstrapper.OpenOrCreate(databasePath, new KeyManager(store)),
            TimeProvider.System, Argon2Parameters.Default, AuthOptions.Default);

    /// <summary>Re-checks the whole audit chain and keeps the result for the next shell.</summary>
    public AuditVerification VerifyAudit()
    {
        using var connection = Database.OpenApplication();
        return AuditStatus = AuditVerifier.Verify(connection);
    }

    /// <summary>Builds the main window view model for the session that has just started.</summary>
    public ShellViewModel CreateShell(IDialogService dialogs) =>
        new(Session,
            new SqliteQueryService(ApplicationDb, Session),
            new SqlitePlanService(ApplicationDb, Session),
            new ReadOnlyNexusService(Database, Policy, AuditLog),
            dialogs,
            Time,
            AuditStatus,
            $"{Database.Path} (criptografado com SQLCipher)");

    public void Dispose() => Database.Dispose();
}
