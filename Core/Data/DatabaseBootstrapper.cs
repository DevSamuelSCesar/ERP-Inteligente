using System;
using System.IO;
using System.Reflection;
using erp_inteligente.Core.Security;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Core.Data;

/// <summary>Creates the encrypted database from the embedded SQL scripts, or opens an existing one.</summary>
public static class DatabaseBootstrapper
{
    /// <summary>Value of <c>PRAGMA user_version</c> of a completely initialised database.</summary>
    public const int SchemaVersion = 1;

    private static readonly string[] Scripts = ["schema.sql", "security.sql", "seed.sql", "optimize.sql"];

    /// <summary>
    /// Creates a new database. Throws if the file exists. On failure the partial file is deleted and a
    /// <see cref="DatabaseUnavailableException"/> is thrown.
    /// </summary>
    public static void CreateNew(string path, DatabaseKeys keys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(keys);
        var full = Path.GetFullPath(path);
        if (File.Exists(full))
            throw new IOException($"The database file '{full}' already exists.");

        NexusDatabase.EnsureInitialized();
        var created = false;
        try
        {
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
                else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = full,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString()))
            {
                connection.Open();
                created = true;
                NexusDatabase.ApplyKey(connection, keys.SqlCipherKey);
                // The file is only written by the first statements after the key; restrict it right away.
                RestrictFile(full);

                foreach (var name in Scripts)
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = ReadScript(name);
                    cmd.ExecuteNonQuery();
                }
                using var version = connection.CreateCommand();
                version.CommandText = $"PRAGMA user_version = {SchemaVersion};"; // constant, no input
                version.ExecuteNonQuery();
            }
            RestrictFile(full);
        }
        catch (Exception ex)
        {
            if (created || File.Exists(full)) DeleteDatabaseFiles(full);
            if (ex is DatabaseUnavailableException) throw;
            throw new DatabaseUnavailableException("Não foi possível criar o banco de dados.", ex);
        }
    }

    /// <summary>
    /// Creates the database (and its keys) when the file is missing; otherwise loads the keys from the key store
    /// (<see cref="MissingDatabaseKeyException"/> when absent) and verifies the file opens with the expected version.
    /// </summary>
    public static NexusDatabase OpenOrCreate(string path, KeyManager keyManager)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(keyManager);
        var full = Path.GetFullPath(path);
        var isNew = !File.Exists(full);

        var keys = keyManager.LoadOrCreate(full);
        NexusDatabase? database = null;
        try
        {
            if (isNew) CreateNew(full, keys);
            database = new NexusDatabase(full, keys); // takes ownership of keys
            using var probe = database.OpenApplication();
            return database;
        }
        catch
        {
            if (database is not null) database.Dispose(); else keys.Dispose();
            // Keys generated for a database that was never created would be orphans in the OS store.
            if (isNew && !File.Exists(full)) keyManager.Forget(full);
            throw;
        }
    }

    private static string ReadScript(string name)
    {
        using var stream = typeof(DatabaseBootstrapper).Assembly.GetManifestResourceStream($"Nexus.Db.{name}")
            ?? throw new InvalidOperationException($"Embedded script 'Nexus.Db.{name}' not found.");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(path))
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void DeleteDatabaseFiles(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            try { File.Delete(path + suffix); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
