using System;
using System.IO;
using System.Security.Cryptography;

namespace erp_inteligente.Core.Security;

/// <summary>
/// Loads the keys of an existing database from the OS key store, or creates and stores them for a new one.
/// </summary>
public sealed class KeyManager(ISecretStore store)
{
    public const string SqlCipherKeyName = "sqlcipher";
    public const string ColumnKeyName = "column";

    /// <summary>
    /// Existing file: both keys must already be in the store, otherwise <see cref="MissingDatabaseKeyException"/>
    /// (new keys would orphan the data). New file: generates, stores and returns fresh keys.
    /// </summary>
    public DatabaseKeys LoadOrCreate(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var id = Path.GetFullPath(databasePath);

        if (File.Exists(id))
        {
            byte[]? sql = null, col = null;
            try
            {
                sql = store.Get(id, SqlCipherKeyName);
                col = store.Get(id, ColumnKeyName);
                if (sql is not { Length: DatabaseKeys.KeyLength } || col is not { Length: DatabaseKeys.KeyLength })
                    throw new MissingDatabaseKeyException(id);
                var keys = new DatabaseKeys(sql, col);
                sql = col = null; // ownership moved to keys
                return keys;
            }
            finally
            {
                if (sql is not null) CryptographicOperations.ZeroMemory(sql);
                if (col is not null) CryptographicOperations.ZeroMemory(col);
            }
        }

        var created = DatabaseKeys.Generate();
        try
        {
            store.Set(id, SqlCipherKeyName, created.SqlCipherKey);
            store.Set(id, ColumnKeyName, created.ColumnKey);
            return created;
        }
        catch
        {
            created.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Removes both keys of a database from the store. Only for a database file that does not exist
    /// (e.g. its creation failed); removing the keys of an existing file would make it unreadable.
    /// </summary>
    public void Forget(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var id = Path.GetFullPath(databasePath);
        if (File.Exists(id))
            throw new InvalidOperationException("Refusing to remove the keys of an existing database.");
        store.Delete(id, SqlCipherKeyName);
        store.Delete(id, ColumnKeyName);
    }

    /// <summary>Uppercase hex of the key, for <c>PRAGMA key = "x'&lt;hex&gt;'"</c>.</summary>
    public static string ToSqlCipherHex(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToHexString(key);
    }
}
