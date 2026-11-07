using System;
using System.IO;
using erp_inteligente.Core.Security;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace erp_inteligente.Core.Data;

/// <summary>
/// Opens the three kinds of connection to the encrypted Nexus database (references/ARQUITETURA.md, 12.6).
/// Connections are never pooled. The SQLCipher key is the first statement on every connection and is
/// validated before use. Takes ownership of the keys; <see cref="Dispose"/> zeroes them.
/// </summary>
public sealed class NexusDatabase : IDisposable
{
    private static readonly object InitLock = new();
    private static bool _initialized;

    private readonly DatabaseKeys _keys;
    private bool _disposed;

    public NexusDatabase(string path, DatabaseKeys keys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(keys);
        Path = System.IO.Path.GetFullPath(path);
        _keys = keys;
    }

    /// <summary>Full path of the database file.</summary>
    public string Path { get; }

    /// <summary>Read-write connection for screens and the policy engine.</summary>
    public SqliteConnection OpenApplication() => Open(SqliteOpenMode.ReadWrite, foreignKeys: true, ai: false);

    /// <summary>Read-write connection for the deterministic plan executor.</summary>
    public SqliteConnection OpenExecutor() => Open(SqliteOpenMode.ReadWrite, foreignKeys: true, ai: false);

    /// <summary>Read-only connection for the AI, restricted by <see cref="AiAuthorizer"/>. Never begin transactions on it.</summary>
    public SqliteConnection OpenAiReadOnly() => Open(SqliteOpenMode.ReadOnly, foreignKeys: false, ai: true);

    public ColumnCipher CreateColumnCipher()
    {
        ThrowIfDisposed();
        return new ColumnCipher(_keys.ColumnKey);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _keys.Dispose();
    }

    /// <summary>Ensures SQLitePCL uses the SQLCipher bundle; safe to call repeatedly.</summary>
    internal static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (InitLock)
        {
            if (_initialized) return;
            Batteries_V2.Init();
            _initialized = true;
        }
    }

    /// <summary>Applies the key as the first statement and validates it. Throws <see cref="DatabaseUnavailableException"/>.</summary>
    internal static void ApplyKey(SqliteConnection connection, byte[] sqlCipherKey)
    {
        var hex = KeyManager.ToSqlCipherHex(sqlCipherKey); // [0-9A-F] only, so no injection is possible
        try
        {
            using (var key = connection.CreateCommand())
            {
                key.CommandText = $"PRAGMA key = \"x'{hex}'\";";
                key.ExecuteNonQuery();
            }
            using var check = connection.CreateCommand();
            check.CommandText = "SELECT count(*) FROM sqlite_master;";
            check.ExecuteScalar();
        }
        catch (SqliteException ex)
        {
            throw new DatabaseUnavailableException(
                "Não foi possível abrir o banco de dados: chave incorreta ou arquivo corrompido.", ex);
        }
    }

    private SqliteConnection Open(SqliteOpenMode mode, bool foreignKeys, bool ai)
    {
        ThrowIfDisposed();
        EnsureInitialized();

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = mode,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            ApplyKey(connection, _keys.SqlCipherKey);
            CheckSchemaVersion(connection);
            if (foreignKeys)
            {
                using var fk = connection.CreateCommand();
                fk.CommandText = "PRAGMA foreign_keys = ON;";
                fk.ExecuteNonQuery();
            }
            if (ai) AiAuthorizer.Install(connection); // last: it denies PRAGMA and transactions
            return connection;
        }
        catch (SqliteException ex)
        {
            connection.Dispose();
            throw new DatabaseUnavailableException("Não foi possível abrir o banco de dados.", ex);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void CheckSchemaVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        if (version != DatabaseBootstrapper.SchemaVersion)
            throw new DatabaseUnavailableException(
                "O banco de dados está incompleto ou tem uma versão de esquema desconhecida.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
