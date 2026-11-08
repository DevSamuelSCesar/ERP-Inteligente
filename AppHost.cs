using System;
using System.Threading.Tasks;
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Auth;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Engine;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Planner;
using erp_inteligente.Core.Policy;
using erp_inteligente.Core.Security;
using erp_inteligente.Services;
using erp_inteligente.Services.Sqlite;
using erp_inteligente.ViewModels;

namespace erp_inteligente;

/// <summary>
/// Composition root (ROADMAP.md, features 1 to 3): the encrypted database, audit log, policy engine, authentication,
/// session, plan engine and the LLM provider. One instance per running app; disposing it zeroes the database keys.
/// </summary>
public sealed class AppHost : IDisposable
{
    /// <param name="secrets">The OS key store; it also holds the DeepSeek key (references/ARQUITETURA.md, section 12.10).</param>
    /// <param name="llm">The provider; null builds the real DeepSeek client (tests pass a scripted one).</param>
    public AppHost(NexusDatabase database, TimeProvider time, Argon2Parameters passwordParameters, AuthOptions authOptions,
        ISecretStore secrets, ILlmProvider? llm = null)
    {
        Database = database;
        Time = time;
        AuditLog = new AuditLog(database, time);
        Policy = new PolicyEngine(database, AuditLog);
        ApplicationDb = new ApplicationDb(database, Policy);
        Auth = new AuthService(database, new PasswordHasher(passwordParameters), Policy, AuditLog, time, authOptions);
        Session = new SessionService(Auth, time, authOptions);
        Engine = new PlanEngine(database, Policy, AuditLog, time);
        LlmSettings = new LlmSettings(secrets, database.Path);
        var deepSeek = llm is null ? new DeepSeekProvider(DeepSeekProvider.CreateDefaultHandler(), LlmSettings.ReadKey) : null;
        Llm = llm ?? deepSeek!;
        LlmSettingsService = new LlmSettingsService(LlmSettings, Policy, AuditLog,
            ct => deepSeek?.TestConnectionAsync(ct) ?? Task.CompletedTask);
        Planner = new Planner(Llm, LlmSettings, database, Policy, AuditLog, Engine);
        AuditStatus = VerifyAudit();
    }

    public NexusDatabase Database { get; }
    public TimeProvider Time { get; }
    public AuditLog AuditLog { get; }
    public PolicyEngine Policy { get; }
    public ApplicationDb ApplicationDb { get; }
    public AuthService Auth { get; }
    public SessionService Session { get; }
    public PlanEngine Engine { get; }
    public LlmSettings LlmSettings { get; }
    public ILlmProvider Llm { get; }
    public LlmSettingsService LlmSettingsService { get; }
    public Planner Planner { get; }

    /// <summary>Last verification of the audit hash chain (at startup and at every login).</summary>
    public AuditVerification AuditStatus { get; private set; }

    /// <summary>Opens (or creates on first run) the database at <paramref name="databasePath"/> with keys from <paramref name="store"/>.</summary>
    public static AppHost Open(string databasePath, ISecretStore store) =>
        new(DatabaseBootstrapper.OpenOrCreate(databasePath, new KeyManager(store)),
            TimeProvider.System, Argon2Parameters.Default, AuthOptions.Default, store);

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
            new EnginePlanService(Engine, ApplicationDb, Session),
            new PlannerNexusService(Planner, PlannerOptions.Default.MaxHistoryTurns),
            dialogs,
            Time,
            AuditStatus,
            $"{Database.Path} (criptografado com SQLCipher)",
            LlmSettingsService);

    public void Dispose() => Database.Dispose();
}
