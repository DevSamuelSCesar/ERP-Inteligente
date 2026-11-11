using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Operations;
using erp_inteligente.Core.Policy;
using erp_inteligente.Models;

namespace erp_inteligente.Services.Sqlite;

/// <summary>Bridges the audit, cost and backup screens to the core, always as the logged-in user and off the UI thread.</summary>
public sealed class SqliteOperationsService(
    NexusDatabase database, PolicyEngine policy, LlmUsageLog usage, BackupService backups, ISessionService session, TimeProvider time)
    : IOperationsService
{
    public Task<AuditVerification> VerifyAuditAsync() => Task.Run(() =>
    {
        policy.Require(session.Current, Permissions.AuditRead);
        using var connection = database.OpenApplication();
        return AuditVerifier.Verify(connection);
    });

    public Task<UsageReport> GetUsageAsync(int days) =>
        Task.Run(() => usage.Report(session.Current, time.GetUtcNow().AddDays(-Math.Clamp(days, 1, 3650))));

    public string BackupDirectory => backups.Directory;

    public Task<IReadOnlyList<BackupInfo>> ListBackupsAsync() => Task.Run(() => backups.List(session.Current));

    public Task<BackupInfo> CreateBackupAsync() => Task.Run(() => backups.Create(session.Current));

    public Task RestoreBackupAsync(string fileName) => Task.Run(() => backups.Restore(session.Current, fileName));
}
