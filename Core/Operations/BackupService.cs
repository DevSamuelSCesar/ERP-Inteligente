using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Policy;
using erp_inteligente.Models;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Core.Operations;

/// <summary>A backup file: name, when it was made (UTC) and size.</summary>
public sealed record BackupInfo(string FileName, DateTimeOffset CreatedAt, long SizeBytes);

/// <summary>A business error of backup or restore (pt-BR message, safe to show).</summary>
public sealed class BackupException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Encrypted backups (references/ARQUITETURA.md, section 12.3, item 5; feature 9). The SQLite backup API copies the
/// open database page by page into a file keyed with the same SQLCipher key, so a backup is as unreadable as the database
/// without this machine's key store. Backups live in <c>backups/</c> next to the database:
/// <list type="bullet">
/// <item>made by an administrator (<c>user.manage</c>) or by the daily job, keeping the newest <see cref="Keep"/>;</item>
/// <item>checked after writing (integrity) and before a restore (key, schema version, integrity, audit chain);</item>
/// <item>a restore first backs up the current state, then copies the backup over the live database with the same API, and
/// records the restore in the restored audit chain.</item>
/// </list>
/// The audit rows carry the file name only, never the full path.
/// </summary>
public sealed partial class BackupService(NexusDatabase database, PolicyEngine policy, AuditLog audit, TimeProvider time, int keep = 7)
{
    public int Keep { get; } = Math.Max(1, keep);

    public static readonly TimeSpan ScheduleInterval = TimeSpan.FromHours(24);

    [GeneratedRegex(@"^nexus-[0-9]{8}-[0-9]{6}(-[0-9]{1,3})?\.db\z")]
    private static partial Regex FileNamePattern();

    public string Directory => Path.Combine(Path.GetDirectoryName(database.Path)!, "backups");

    /// <exception cref="PolicyDeniedException">The user is not an administrator.</exception>
    public IReadOnlyList<BackupInfo> List(UserSession user)
    {
        policy.Require(user, Permissions.UserManage);
        return ListFiles();
    }

    /// <exception cref="PolicyDeniedException">The user is not an administrator.</exception>
    /// <exception cref="BackupException">The copy failed its integrity check (nothing is kept).</exception>
    public BackupInfo Create(UserSession user)
    {
        policy.Require(user, Permissions.UserManage);
        var info = Write();
        Audit(new AuditEntry(AuditActorType.User, user.UserId, null, "backup.create", "backup:" + info.FileName, AuditOutcome.Allowed,
            new JsonObject { ["bytes"] = info.SizeBytes }.ToJsonString()));
        Prune();
        return info;
    }

    /// <summary>The daily job: a backup when the newest one is older than <see cref="ScheduleInterval"/>. Returns it, or null.</summary>
    public BackupInfo? CreateScheduled()
    {
        var newest = ListFiles().FirstOrDefault();
        if (newest is not null && time.GetUtcNow() - newest.CreatedAt < ScheduleInterval) return null;
        var info = Write();
        Audit(new AuditEntry(AuditActorType.System, null, null, "backup.create", "backup:" + info.FileName, AuditOutcome.Allowed,
            new JsonObject { ["bytes"] = info.SizeBytes, ["scheduled"] = true }.ToJsonString()));
        Prune();
        return info;
    }

    /// <summary>
    /// Replaces the live database with a backup of this installation. The current state is backed up first, so a restore
    /// can itself be undone.
    /// </summary>
    /// <exception cref="PolicyDeniedException">The user is not an administrator.</exception>
    /// <exception cref="BackupException">Unknown file, other key, other schema version, damaged file or broken audit chain.</exception>
    public void Restore(UserSession user, string fileName)
    {
        policy.Require(user, Permissions.UserManage);
        if (fileName is null || !FileNamePattern().IsMatch(fileName))
            throw new BackupException("Backup não encontrado.");
        var path = Path.Combine(Directory, fileName);
        if (!File.Exists(path))
            throw new BackupException("Backup não encontrado.");

        using (var check = OpenBackup(path))
        {
            using (var version = check.CreateCommand())
            {
                version.CommandText = "PRAGMA user_version;";
                var found = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (found != DatabaseBootstrapper.SchemaVersion)
                    throw new BackupException($"O backup é da versão {found} do banco e este app usa a {DatabaseBootstrapper.SchemaVersion}. Ele não pode ser restaurado aqui.");
            }
            if (!IntegrityOk(check))
                throw new BackupException("O arquivo de backup está danificado.");
            var chain = AuditVerifier.Verify(check);
            if (!chain.IsValid)
                throw new BackupException($"A cadeia de auditoria do backup está quebrada na linha {chain.FirstBrokenId}. Ele não foi restaurado.");
        }

        var safety = Write();
        using (var source = OpenBackup(path))
        using (var live = database.OpenApplication())
            source.BackupDatabase(live);

        Audit(new AuditEntry(AuditActorType.User, user.UserId, null, "backup.restore", "backup:" + fileName, AuditOutcome.Allowed,
            new JsonObject { ["safety_copy"] = safety.FileName }.ToJsonString()));
        Prune();
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private BackupInfo Write()
    {
        CreateDirectory();
        var now = time.GetUtcNow();
        var name = $"nexus-{now.UtcDateTime:yyyyMMdd-HHmmss}.db";
        for (var n = 1; File.Exists(Path.Combine(Directory, name)); n++)
            name = $"nexus-{now.UtcDateTime:yyyyMMdd-HHmmss}-{n}.db";
        var path = Path.Combine(Directory, name);
        try
        {
            using (var source = database.OpenApplication())
            using (var target = database.OpenWithSameKey(path, create: true))
            {
                source.BackupDatabase(target);
                if (!IntegrityOk(target))
                    throw new BackupException("A cópia não passou na verificação de integridade.");
            }
            Restrict(path);
            File.SetLastWriteTimeUtc(path, now.UtcDateTime);
            return new BackupInfo(name, now, new FileInfo(path).Length);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or DatabaseUnavailableException or BackupException)
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw ex as BackupException ?? new BackupException("Não foi possível gravar o backup.", ex);
        }
    }

    private SqliteConnection OpenBackup(string path)
    {
        try
        {
            return database.OpenWithSameKey(path, create: false);
        }
        catch (Exception ex) when (ex is DatabaseUnavailableException or SqliteException)
        {
            throw new BackupException("O backup não abre com a chave deste computador.", ex);
        }
    }

    private IReadOnlyList<BackupInfo> ListFiles()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        return new DirectoryInfo(Directory).GetFiles("nexus-*.db")
            .Where(f => FileNamePattern().IsMatch(f.Name))
            .Select(f => new BackupInfo(f.Name, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero), f.Length))
            .OrderByDescending(b => b.CreatedAt).ThenByDescending(b => b.FileName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Keeps the newest <see cref="Keep"/> backups; older ones (only files this service wrote) are deleted.</summary>
    private void Prune()
    {
        foreach (var old in ListFiles().Skip(Keep))
        {
            try { File.Delete(Path.Combine(Directory, old.FileName)); }
            catch (IOException ex) { System.Diagnostics.Trace.TraceWarning("Old backup not deleted: " + ex.GetType().Name); }
        }
    }

    private void CreateDirectory()
    {
        if (System.IO.Directory.Exists(Directory)) return;
        if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(Directory);
        else System.IO.Directory.CreateDirectory(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void Restrict(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static bool IntegrityOk(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        return command.ExecuteScalar() is string result && result == "ok";
    }

    private void Audit(AuditEntry entry) => audit.Append(entry);
}
