using erp_inteligente.Core.Security;

namespace Nexus.Tests.Core.Security;

public class KeyManagerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-keys-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void NewDatabase_GeneratesAndStoresKeys()
    {
        var store = new InMemorySecretStore();
        var path = Path.Combine(_dir, "new.db");
        using var keys = new KeyManager(store).LoadOrCreate(path);
        var id = Path.GetFullPath(path);
        Assert.Equal(keys.SqlCipherKey, store.Get(id, "sqlcipher"));
        Assert.Equal(keys.ColumnKey, store.Get(id, "column"));
        Assert.NotEqual(keys.SqlCipherKey, keys.ColumnKey);
    }

    [Fact]
    public void NewDatabase_OverwritesLeftovers()
    {
        var store = new InMemorySecretStore();
        var path = Path.Combine(_dir, "left.db");
        store.Set(Path.GetFullPath(path), "sqlcipher", new byte[32]);
        using var keys = new KeyManager(store).LoadOrCreate(path);
        Assert.Equal(keys.SqlCipherKey, store.Get(Path.GetFullPath(path), "sqlcipher"));
    }

    [Fact]
    public void ExistingDatabase_MissingKeys_Throws()
    {
        var path = Path.Combine(_dir, "old.db");
        File.WriteAllText(path, "x");
        var store = new InMemorySecretStore();
        Assert.Throws<MissingDatabaseKeyException>(() => new KeyManager(store).LoadOrCreate(path));
        Assert.Null(store.Get(Path.GetFullPath(path), "sqlcipher")); // nothing generated
    }

    [Fact]
    public void ExistingDatabase_OneKeyMissing_Throws()
    {
        var path = Path.Combine(_dir, "half.db");
        File.WriteAllText(path, "x");
        var store = new InMemorySecretStore();
        store.Set(Path.GetFullPath(path), "sqlcipher", new byte[32]);
        Assert.Throws<MissingDatabaseKeyException>(() => new KeyManager(store).LoadOrCreate(path));
    }

    [Fact]
    public void ExistingDatabase_WrongLength_Throws()
    {
        var path = Path.Combine(_dir, "bad.db");
        File.WriteAllText(path, "x");
        var store = new InMemorySecretStore();
        store.Set(Path.GetFullPath(path), "sqlcipher", new byte[32]);
        store.Set(Path.GetFullPath(path), "column", new byte[5]);
        Assert.Throws<MissingDatabaseKeyException>(() => new KeyManager(store).LoadOrCreate(path));
    }

    [Fact]
    public void ExistingDatabase_ReturnsStoredKeys()
    {
        var path = Path.Combine(_dir, "ok.db");
        File.WriteAllText(path, "x");
        var store = new InMemorySecretStore();
        var a = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var b = Enumerable.Range(0, 32).Select(i => (byte)(i + 100)).ToArray();
        store.Set(Path.GetFullPath(path), "sqlcipher", a);
        store.Set(Path.GetFullPath(path), "column", b);
        using var keys = new KeyManager(store).LoadOrCreate(path);
        Assert.Equal(a, keys.SqlCipherKey);
        Assert.Equal(b, keys.ColumnKey);
    }

    [Fact]
    public void ToSqlCipherHex_IsUppercaseHex() =>
        Assert.Equal("00FFAB", KeyManager.ToSqlCipherHex([0x00, 0xFF, 0xAB]));

    [Fact]
    public void InMemoryStore_ReturnsCopies()
    {
        var s = new InMemorySecretStore();
        s.Set("d", "k", [1, 2, 3]);
        s.Get("d", "k")![0] = 9;
        Assert.Equal(new byte[] { 1, 2, 3 }, s.Get("d", "k"));
        s.Delete("d", "k");
        Assert.Null(s.Get("d", "k"));
    }
}
