using System.Collections.Generic;
using System.Threading.Tasks;
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Operations;

namespace erp_inteligente.Services;

/// <summary>
/// Operation of the installation (ROADMAP.md, feature 9) seen from the screens: the audit chain check and the token usage
/// report (<c>audit.read</c>), and the encrypted backups (<c>user.manage</c>). The core checks the permission again.
/// </summary>
public interface IOperationsService
{
    /// <summary>Walks the whole audit hash chain now.</summary>
    Task<AuditVerification> VerifyAuditAsync();

    /// <summary>Token usage and estimated cost of the last <paramref name="days"/> days.</summary>
    Task<UsageReport> GetUsageAsync(int days);

    /// <summary>Where the backups are kept (a folder next to the database).</summary>
    string BackupDirectory { get; }

    Task<IReadOnlyList<BackupInfo>> ListBackupsAsync();

    /// <exception cref="BackupException">The copy failed its integrity check.</exception>
    Task<BackupInfo> CreateBackupAsync();

    /// <exception cref="BackupException">The backup cannot be restored here (key, version, damage, audit chain).</exception>
    Task RestoreBackupAsync(string fileName);
}
