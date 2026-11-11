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
    public const int SchemaVersion = 7;

    /// <summary>Migration scripts (db/migrations) indexed by the version they upgrade FROM.</summary>
    private static readonly (int From, string Script)[] Migrations =
    [
        (1, "migrations.002_plan_decisions.sql"),
        (2, "migrations.003_full_catalog.sql"),
        (3, "migrations.004_reservations.sql"),
        (4, "migrations.005_skills.sql"),
        (5, "migrations.006_memory.sql"),
        (6, "migrations.007_recipes.sql"),
    ];

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
            else Migrate(full, keys);
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

    /// <summary>
    /// Brings an existing database to <see cref="SchemaVersion"/>. Each migration and its version bump run in one
    /// IMMEDIATE transaction, so a failure leaves the database at the previous version. Unknown (newer or
    /// older-than-supported) versions are refused.
    /// </summary>
    internal static void Migrate(string path, DatabaseKeys keys)
    {
        NexusDatabase.EnsureInitialized();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            NexusDatabase.ApplyKey(connection, keys.SqlCipherKey);
            var version = ReadUserVersion(connection);
            while (version < SchemaVersion)
            {
                var step = Array.Find(Migrations, m => m.From == version);
                if (step.Script is null)
                    throw new DatabaseUnavailableException(
                        "O banco de dados está incompleto ou tem uma versão de esquema desconhecida.");

                using var transaction = connection.BeginTransaction(deferred: false);
                using (var migrate = connection.CreateCommand())
                {
                    migrate.Transaction = transaction;
                    migrate.CommandText = ReadScript(step.Script);
                    migrate.ExecuteNonQuery();
                }
                using (var bump = connection.CreateCommand())
                {
                    bump.Transaction = transaction;
                    bump.CommandText = $"PRAGMA user_version = {version + 1};"; // integer from code, no input
                    bump.ExecuteNonQuery();
                }
                transaction.Commit();
                version++;
            }
        }
        catch (SqliteException ex)
        {
            throw new DatabaseUnavailableException("Não foi possível atualizar o banco de dados.", ex);
        }
    }

    private static int ReadUserVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
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
