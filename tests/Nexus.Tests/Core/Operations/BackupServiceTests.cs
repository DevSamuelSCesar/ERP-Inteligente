using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Operations;
using erp_inteligente.Core.Policy;
using erp_inteligente.Models;
using Microsoft.Data.Sqlite;
using Nexus.Tests.Core.Policy;

namespace Nexus.Tests.Core.Operations;

/// <summary>Feature 9: encrypted backups through the SQLite backup API, the daily job, pruning and restore.</summary>
public sealed class BackupServiceTests : IDisposable
{
    private readonly PolicyTestFixture _f = new();
    private readonly BackupService _backups;
    private readonly UserSession _admin;

    public BackupServiceTests()
    {
        _backups = new BackupService(_f.Db.Database, _f.Policy, _f.Audit, _f.Time, keep: 3);
        _admin = _f.SessionFor("adm.carlos");
    }

    public void Dispose() => _f.Dispose();

    private long Count(string sql) => _f.Scalar(sql);

    [Fact]
    public void A_backup_is_encrypted_with_the_same_key_and_complete()
    {
        var info = _backups.Create(_admin);
        var path = Path.Combine(_backups.Directory, info.FileName);
        Assert.True(File.Exists(path));
        Assert.Matches(@"^nexus-[0-9]{8}-[0-9]{6}\.db$", info.FileName);

        // Without the key the file is unreadable.
        using (var raw = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"))
        {
            raw.Open();
            using var probe = raw.CreateCommand();
            probe.CommandText = "SELECT count(*) FROM sqlite_master";
            Assert.Throws<SqliteException>(() => probe.ExecuteScalar());
        }
        // With it, the copy holds the same data and a valid audit chain.
        using (var copy = _f.Db.Database.OpenWithSameKey(path, create: false))
        {
            using var command = copy.CreateCommand();
            command.CommandText = "SELECT count(*) FROM products";
            Assert.Equal(Count("SELECT count(*) FROM products"), (long)command.ExecuteScalar()!);
            Assert.True(AuditVerifier.Verify(copy).IsValid);
        }
        Assert.Equal(1, Count("SELECT COUNT(*) FROM audit_log WHERE action = 'backup.create' AND target = 'backup:" + info.FileName + "'"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM audit_log WHERE details_json LIKE '%" + _backups.Directory.Replace("'", "''").Replace("\\", "\\\\") + "%'"));
    }

    [Fact]
    public void Only_the_administrator_makes_lists_and_restores_backups()
    {
        var maria = _f.SessionFor("sup.maria");
        Assert.Throws<PolicyDeniedException>(() => _backups.Create(maria));
        Assert.Throws<PolicyDeniedException>(() => _backups.List(maria));
        Assert.Throws<PolicyDeniedException>(() => _backups.Restore(maria, "nexus-20261107-120000.db"));
    }

    [Fact]
    public void The_daily_job_runs_once_a_day_and_keeps_the_newest()
    {
        Assert.NotNull(_backups.CreateScheduled());
        Assert.Null(_backups.CreateScheduled()); // same day
        for (var day = 1; day <= 4; day++)
        {
            _f.Time.Advance(TimeSpan.FromHours(25));
            Assert.NotNull(_backups.CreateScheduled());
        }
        var list = _backups.List(_admin);
        Assert.Equal(3, list.Count); // keep: 3
        Assert.True(list[0].CreatedAt > list[1].CreatedAt);
        Assert.Equal(5, Count("SELECT COUNT(*) FROM audit_log WHERE action = 'backup.create' AND actor_type = 'system'"));
    }

    [Fact]
    public void Restore_brings_the_data_back_keeps_a_safety_copy_and_audits()
    {
        var before = _backups.Create(_admin);
        var products = Count("SELECT count(*) FROM products");
        _f.Exec("INSERT INTO products (sku, name, unit) VALUES ('NEW-001', 'Depois do backup', 'un')");
        _f.Time.Advance(TimeSpan.FromMinutes(5));

        _backups.Restore(_admin, before.FileName);

        Assert.Equal(products, Count("SELECT count(*) FROM products"));
        Assert.Equal(2, _backups.List(_admin).Count); // the backup and the safety copy taken just before
        Assert.Equal(1, Count("SELECT COUNT(*) FROM audit_log WHERE action = 'backup.restore'"));
        using var connection = _f.Open();
        Assert.True(AuditVerifier.Verify(connection).IsValid);
    }

    [Theory]
    [InlineData("../nexus.db")]
    [InlineData("nexus-20261107-120000.db")] // does not exist
    [InlineData("qualquer.db")]
    public void Restore_refuses_unknown_files(string name)
    {
        Assert.Throws<BackupException>(() => _backups.Restore(_admin, name));
    }

    [Fact]
    public void Restore_refuses_a_backup_made_with_another_key()
    {
        using var other = new PolicyTestFixture();
        var foreign = new BackupService(other.Db.Database, other.Policy, other.Audit, other.Time).Create(other.SessionFor("adm.carlos"));
        Directory.CreateDirectory(_backups.Directory);
        File.Copy(Path.Combine(Path.GetDirectoryName(other.Db.Path)!, "backups", foreign.FileName),
            Path.Combine(_backups.Directory, foreign.FileName));

        var e = Assert.Throws<BackupException>(() => _backups.Restore(_admin, foreign.FileName));
        Assert.Contains("chave", e.Message);
    }

    [Fact]
    public void Restore_refuses_a_backup_with_a_broken_audit_chain()
    {
        var info = _backups.Create(_admin);
        using (var copy = _f.Db.Database.OpenWithSameKey(Path.Combine(_backups.Directory, info.FileName), create: false))
        {
            using var drop = copy.CreateCommand();
            drop.CommandText = "DROP TRIGGER audit_log_deny_update; UPDATE audit_log SET target = 'x' WHERE id = 1;";
            drop.ExecuteNonQuery();
        }
        var e = Assert.Throws<BackupException>(() => _backups.Restore(_admin, info.FileName));
        Assert.Contains("auditoria", e.Message);
    }
}
