using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Security;
using Microsoft.Data.Sqlite;

namespace Nexus.Tests.Core.Data;

/// <summary>
/// A database created by schema version 1 (scripts copied from git history into Fixtures/) is upgraded to the
/// current version on open, keeping its data and gaining the plan-decision columns and triggers.
/// </summary>
public sealed class MigrationTests : IDisposable
{
    private const string OldOperatorHash =
        "$argon2id$v=19$m=65536,t=3,p=4$4H3UK/Bmc+aoDlJBaqyenA$qVxvHSzAzpt4OnIntR3nD14T/SSD/TSRo8ARXm5bBeU";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-tests-" + Guid.NewGuid().ToString("N"));
    private readonly InMemorySecretStore _store = new();
    private readonly string _path;

    public MigrationTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "v1.db");
        CreateVersion1Database();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void CreateVersion1Database()
    {
        using var keys = DatabaseKeys.Generate();
        NexusDatabase.EnsureInitialized();
        using (var connection = new SqliteConnection($"Data Source={_path};Mode=ReadWriteCreate;Pooling=False"))
        {
            connection.Open();
            NexusDatabase.ApplyKey(connection, keys.SqlCipherKey);
            foreach (var script in new[] { "schema_v1.sql", "security_v1.sql", "seed_v1.sql" })
                Exec(connection, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Core", "Data", "Fixtures", script)));
            Exec(connection, "PRAGMA user_version = 1;");
        }
        _store.Set(_path, KeyManager.SqlCipherKeyName, keys.SqlCipherKey.ToArray());
        _store.Set(_path, KeyManager.ColumnKeyName, keys.ColumnKey.ToArray());
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private NexusDatabase Open() => DatabaseBootstrapper.OpenOrCreate(_path, new KeyManager(_store));

    [Fact]
    public void Version_1_database_is_upgraded_and_keeps_its_data()
    {
        using var db = Open();
        using var c = db.OpenApplication();

        Assert.Equal((long)DatabaseBootstrapper.SchemaVersion, Scalar(c, "PRAGMA user_version"));
        Assert.Equal(2L, Scalar(c, "SELECT count(*) FROM plans"));
        Assert.Equal(13L, Scalar(c, "SELECT count(*) FROM stock_balances"));
        foreach (var column in new[] { "rejected_by", "rejection_reason", "failure_reason" })
            Assert.Equal(1L, Scalar(c, $"SELECT count(*) FROM pragma_table_info('plans') WHERE name = '{column}'"));
        Assert.True(AuditVerifier.Verify(c).IsValid);
    }

    [Fact]
    public void Migrated_database_enforces_the_rejection_rules()
    {
        using var db = Open();
        using var c = db.OpenApplication();

        // Plan 2 was requested by op.joao (id 1): he cannot reject it, and a rejection needs a reason.
        var self = Assert.Throws<SqliteException>(() =>
            Exec(c, "UPDATE plans SET status = 'rejected', rejected_by = 1, rejection_reason = 'x' WHERE id = 2"));
        Assert.Contains("DENY", self.Message);
        var noReason = Assert.Throws<SqliteException>(() =>
            Exec(c, "UPDATE plans SET status = 'rejected', rejected_by = 2 WHERE id = 2"));
        Assert.Contains("rejection_reason", noReason.Message);

        Exec(c, "UPDATE plans SET status = 'rejected', rejected_by = 2, rejection_reason = 'Contagem errada' WHERE id = 2");
        Assert.Equal("rejected", Scalar(c, "SELECT status FROM plans WHERE id = 2"));
    }

    [Fact]
    public void Untouched_demo_accounts_get_the_new_demo_password_and_changed_ones_are_kept()
    {
        using (var setup = new SqliteConnection($"Data Source={_path};Mode=ReadWrite;Pooling=False"))
        {
            setup.Open();
            NexusDatabase.ApplyKey(setup, _store.Get(_path, KeyManager.SqlCipherKeyName)!);
            Exec(setup, "UPDATE users SET password_hash = 'changed-by-someone' WHERE username = 'adm.carlos'");
        }

        using var db = Open();
        using var c = db.OpenApplication();
        var hasher = new PasswordHasher(Argon2Parameters.Default);

        var operatorHash = (string)Scalar(c, "SELECT password_hash FROM users WHERE username = 'op.joao'")!;
        Assert.NotEqual(OldOperatorHash, operatorHash);
        Assert.True(hasher.Verify("demo", operatorHash));
        Assert.Equal("changed-by-someone", Scalar(c, "SELECT password_hash FROM users WHERE username = 'adm.carlos'"));
    }

    [Fact]
    public void Opening_again_does_not_migrate_twice()
    {
        using (Open()) { }
        using var db = Open();
        using var c = db.OpenApplication();

        Assert.Equal((long)DatabaseBootstrapper.SchemaVersion, Scalar(c, "PRAGMA user_version"));
    }

    [Fact]
    public void Newer_unknown_version_is_refused()
    {
        using (var setup = new SqliteConnection($"Data Source={_path};Mode=ReadWrite;Pooling=False"))
        {
            setup.Open();
            NexusDatabase.ApplyKey(setup, _store.Get(_path, KeyManager.SqlCipherKeyName)!);
            Exec(setup, "PRAGMA user_version = 99;");
        }

        Assert.Throws<DatabaseUnavailableException>(Open);
    }
}
