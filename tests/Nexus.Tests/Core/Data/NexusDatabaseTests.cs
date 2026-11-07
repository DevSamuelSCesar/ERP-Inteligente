using System.Text;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Security;
using Microsoft.Data.Sqlite;

namespace Nexus.Tests.Core.Data;

public class NexusDatabaseTests : IDisposable
{
    private readonly TestDatabase _t = new();

    public void Dispose() => _t.Dispose();

    private static long Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public void Creation_ProducesEncryptedFileWithVersion()
    {
        Assert.True(File.Exists(_t.Path));
        var header = new byte[16];
        using (var fs = File.OpenRead(_t.Path)) fs.ReadExactly(header);
        Assert.NotEqual(Encoding.ASCII.GetBytes("SQLite format 3\0"), header);

        using var c = _t.Database.OpenApplication();
        Assert.Equal(DatabaseBootstrapper.SchemaVersion, Scalar(c, "PRAGMA user_version"));
    }

    [Fact]
    public void Creation_LoadsSeed()
    {
        using var c = _t.Database.OpenApplication();
        Assert.Equal(5, Scalar(c, "SELECT count(*) FROM roles"));
        Assert.Equal(5, Scalar(c, "SELECT count(*) FROM users"));
        Assert.Equal(10, Scalar(c, "SELECT count(*) FROM products"));
        Assert.Equal(13, Scalar(c, "SELECT count(*) FROM stock_balances"));
        Assert.Equal(4, Scalar(c, "SELECT count(*) FROM memories"));
    }

    [Fact]
    public void WriteConnections_EnableForeignKeys()
    {
        using var app = _t.Database.OpenApplication();
        using var exe = _t.Database.OpenExecutor();
        Assert.Equal(1, Scalar(app, "PRAGMA foreign_keys"));
        Assert.Equal(1, Scalar(exe, "PRAGMA foreign_keys"));
    }

    [Fact]
    public void DatabaseFile_HasOwnerOnlyMode()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_t.Path));
    }

    [Fact]
    public void PlainSqlite_WithoutKey_CannotRead()
    {
        using var c = new SqliteConnection($"Data Source={_t.Path};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master";
        var ex = Assert.Throws<SqliteException>(() => cmd.ExecuteScalar());
        Assert.Equal(26, ex.SqliteErrorCode);
    }

    [Fact]
    public void WrongKey_ThrowsDatabaseUnavailable()
    {
        using var wrong = new NexusDatabase(_t.Path, DatabaseKeys.Generate());
        var ex = Assert.Throws<DatabaseUnavailableException>(() => wrong.OpenApplication());
        Assert.DoesNotContain("x'", ex.Message);
    }

    [Fact]
    public void Reopen_WithSameStore_Works()
    {
        using var again = DatabaseBootstrapper.OpenOrCreate(_t.Path, new KeyManager(_t.Store));
        using var c = again.OpenApplication();
        Assert.Equal(5, Scalar(c, "SELECT count(*) FROM users"));
    }

    [Fact]
    public void Reopen_WithEmptyStore_ThrowsMissingKey()
    {
        Assert.Throws<MissingDatabaseKeyException>(
            () => DatabaseBootstrapper.OpenOrCreate(_t.Path, new KeyManager(new InMemorySecretStore())));
    }

    [Fact]
    public void UnknownUserVersion_ThrowsDatabaseUnavailable()
    {
        using (var c = _t.Database.OpenApplication())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 99";
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<DatabaseUnavailableException>(() => _t.Database.OpenApplication());
    }

    [Fact]
    public void Triggers_AreActiveOnApplicationConnection()
    {
        using var c = _t.Database.OpenApplication();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE audit_log SET outcome = 'allowed' WHERE id = 4";
        var ex = Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
        Assert.Contains("DENY:", ex.Message);
    }

    [Fact]
    public void ColumnCipher_RoundTrips()
    {
        using var cipher = _t.Database.CreateColumnCipher();
        var enc = cipher.Encrypt("secret", "users", "cpf_enc", 1);
        Assert.Equal("secret", cipher.Decrypt(enc, "users", "cpf_enc", 1));
    }

    [Fact]
    public void CreateNew_OnExistingFile_ThrowsAndKeepsFile()
    {
        using var keys = DatabaseKeys.Generate();
        Assert.Throws<IOException>(() => DatabaseBootstrapper.CreateNew(_t.Path, keys));
        Assert.True(File.Exists(_t.Path));
    }

    [Fact]
    public void CreateNew_Failure_LeavesNoPartialFile()
    {
        // The parent "directory" is a regular file, so the database cannot be created.
        var blocker = Path.Combine(Path.GetDirectoryName(_t.Path)!, "blocker");
        File.WriteAllText(blocker, "x");
        var target = Path.Combine(blocker, "nexus.db");
        using var keys = DatabaseKeys.Generate();
        Assert.Throws<DatabaseUnavailableException>(() => DatabaseBootstrapper.CreateNew(target, keys));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void OpenOrCreate_Failure_RemovesKeysGeneratedForTheMissingFile()
    {
        var blocker = Path.Combine(Path.GetDirectoryName(_t.Path)!, "blocker2");
        File.WriteAllText(blocker, "x");
        var target = Path.Combine(blocker, "nexus.db");
        var store = new InMemorySecretStore();

        Assert.ThrowsAny<Exception>(() => DatabaseBootstrapper.OpenOrCreate(target, new KeyManager(store)));

        var id = Path.GetFullPath(target);
        Assert.Null(store.Get(id, KeyManager.SqlCipherKeyName));
        Assert.Null(store.Get(id, KeyManager.ColumnKeyName));
    }

    [Fact]
    public void Forget_RefusesAnExistingDatabase() =>
        Assert.Throws<InvalidOperationException>(() => new KeyManager(_t.Store).Forget(_t.Path));

    [Fact]
    public void NewDatabase_InFreshDirectory_CreatesIt()
    {
        var path = Path.Combine(Path.GetDirectoryName(_t.Path)!, "sub", "n.db");
        using var db = DatabaseBootstrapper.OpenOrCreate(path, new KeyManager(_t.Store));
        Assert.True(File.Exists(path));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(path)!));
    }
}
