using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Security;
using Microsoft.Data.Sqlite;

namespace Nexus.Tests.Core.Data;

/// <summary>
/// Databases created by schema versions 1, 2 and 3 (scripts copied from git history into Fixtures/) are upgraded to the
/// current version on open, keeping their data. Version 2 → 3 adds the catalog flags, the AI operational rights and
/// the v_ai_* views (migrations/003_full_catalog.sql).
/// </summary>
public sealed class MigrationTests : IDisposable
{
    private const string OldOperatorHash =
        "$argon2id$v=19$m=65536,t=3,p=4$4H3UK/Bmc+aoDlJBaqyenA$qVxvHSzAzpt4OnIntR3nD14T/SSD/TSRo8ARXm5bBeU";

    private static readonly string[] Version1Scripts = ["schema_v1.sql", "security_v1.sql", "seed_v1.sql"];
    private static readonly string[] Version2Scripts = ["schema_v2.sql", "security_v2.sql", "seed_v2.sql"];
    private static readonly string[] Version3Scripts = ["schema_v3.sql", "security_v3.sql", "seed_v3.sql"];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-tests-" + Guid.NewGuid().ToString("N"));
    private readonly InMemorySecretStore _store = new();
    private readonly string _path;

    public MigrationTests()
    {
        Directory.CreateDirectory(_dir);
        _path = CreateLegacyDatabase("v1.db", 1, Version1Scripts);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Creates an encrypted database from fixture scripts at the given user_version, with its keys in the store.</summary>
    private string CreateLegacyDatabase(string fileName, int version, string[] scripts)
    {
        var path = Path.Combine(_dir, fileName);
        using var keys = DatabaseKeys.Generate();
        NexusDatabase.EnsureInitialized();
        using (var connection = new SqliteConnection($"Data Source={path};Mode=ReadWriteCreate;Pooling=False"))
        {
            connection.Open();
            NexusDatabase.ApplyKey(connection, keys.SqlCipherKey);
            foreach (var script in scripts)
                Exec(connection, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Core", "Data", "Fixtures", script)));
            Exec(connection, $"PRAGMA user_version = {version};");
        }
        _store.Set(path, KeyManager.SqlCipherKeyName, keys.SqlCipherKey.ToArray());
        _store.Set(path, KeyManager.ColumnKeyName, keys.ColumnKey.ToArray());
        return path;
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

    private NexusDatabase Open(string? path = null) =>
        DatabaseBootstrapper.OpenOrCreate(path ?? _path, new KeyManager(_store));

    /// <summary>Checks what migrations/004_reservations.sql must have produced (same as a fresh schema 4 database).</summary>
    private static void AssertVersion4Schema(SqliteConnection c)
    {
        Assert.Equal(4L, Scalar(c, "PRAGMA user_version"));
        Assert.Equal(1L, Scalar(c, "SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name = 'order_reservations'"));
        Assert.Equal(2L, Scalar(c, "SELECT count(*) FROM sqlite_schema WHERE type = 'index' AND name LIKE 'idx_order_reservations_%'"));
        Assert.Equal(2L, Scalar(c, "SELECT count(*) FROM sqlite_schema WHERE type = 'trigger' AND name LIKE 'order_reservations_%'"));
        // The seeded reserved order PED-FICT-5001 is backfilled: 10 of ABC-124 and 2 of FER-010.
        Assert.Equal(12L, Scalar(c, "SELECT COALESCE(SUM(quantity), 0) FROM order_reservations WHERE state = 'reserved'"));
        Assert.Equal(2L, Scalar(c, "SELECT count(*) FROM order_reservations"));
        var ex = Assert.Throws<SqliteException>(() => Exec(c, "DELETE FROM order_reservations"));
        Assert.Contains("DENY", ex.Message);
        AssertVersion3Schema(c);
    }

    /// <summary>Checks what migrations/003_full_catalog.sql must have produced (same as a fresh schema 3 database).</summary>
    private static void AssertVersion3Schema(SqliteConnection c)
    {

        foreach (var table in new[] { "zones", "suppliers", "customers" })
            Assert.Equal(1L, Scalar(c, $"SELECT count(*) FROM pragma_table_info('{table}') WHERE name = 'is_active'"));

        Assert.Equal(3L, Scalar(c,
            "SELECT count(*) FROM v_effective_permissions WHERE permission = 'order.manage' AND role_id IN (1, 2, 5)"));
        Assert.Equal(3L, Scalar(c,
            "SELECT count(*) FROM v_effective_permissions WHERE role_id = 5 " +
            "AND permission IN ('location.manage', 'stock.adjust', 'stock.ship')"));
        Assert.Equal(0L, Scalar(c,
            "SELECT count(*) FROM role_permissions WHERE role_id = 5 AND effect = 'DENY' " +
            "AND permission IN ('stock.adjust', 'stock.ship')"));
        Assert.Equal(2L, Scalar(c,
            "SELECT count(*) FROM sqlite_schema WHERE type = 'trigger' " +
            "AND name IN ('role_permissions_deny_ai_escalation', 'role_permissions_deny_ai_unblock')"));

        Assert.Equal(3L, Scalar(c,
            "SELECT count(*) FROM sqlite_schema WHERE type = 'view' AND name IN ('v_ai_zones', 'v_ai_lots', 'v_ai_partners')"));
        Assert.Equal("id,warehouse_id,code,name,is_active", ColumnList(c, "v_ai_zones"));
        Assert.Equal("sku,lot_code,expires_on", ColumnList(c, "v_ai_lots"));
        Assert.Equal("kind,id,name,is_active", ColumnList(c, "v_ai_partners"));
    }

    private static string ColumnList(SqliteConnection c, string view) =>
        (string)Scalar(c, $"SELECT group_concat(name, ',') FROM (SELECT name FROM pragma_table_info('{view}') ORDER BY cid)")!;

    [Fact]
    public void Version_1_database_gets_every_later_migration()
    {
        using var db = Open();
        using var c = db.OpenApplication();

        AssertVersion4Schema(c);
    }

    [Fact]
    public void Version_3_database_is_upgraded_to_4_with_the_reservation_backfill()
    {
        var path = CreateLegacyDatabase("v3.db", 3, Version3Scripts);
        using var db = Open(path);
        using var c = db.OpenApplication();

        AssertVersion4Schema(c);
        Assert.Equal(13L, Scalar(c, "SELECT count(*) FROM stock_balances"));
        Assert.True(AuditVerifier.Verify(c).IsValid);
    }

    [Fact]
    public void Version_2_database_is_upgraded_to_4_and_keeps_its_data()
    {
        var path = CreateLegacyDatabase("v2.db", 2, Version2Scripts);
        using var db = Open(path);
        using var c = db.OpenApplication();

        AssertVersion4Schema(c);
        Assert.Equal(13L, Scalar(c, "SELECT count(*) FROM stock_balances"));
        Assert.True(AuditVerifier.Verify(c).IsValid);
    }

    [Theory]
    [InlineData("plan.approve")]
    [InlineData("user.manage")]
    [InlineData("sensitive.read")]
    [InlineData("role.manage")]
    [InlineData("audit.read")]
    public void Migrated_database_refuses_escalation_of_the_ai_role(string permission)
    {
        using var db = Open();
        using var c = db.OpenApplication();

        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO role_permissions (role_id, permission, effect) VALUES (5, $permission, 'ALLOW')";
        cmd.Parameters.AddWithValue("$permission", permission);
        var ex = Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
        Assert.Contains("DENY", ex.Message);
    }

    [Fact]
    public void Migrated_database_keeps_the_remaining_ai_deny_rows_protected()
    {
        using var db = Open();
        using var c = db.OpenApplication();

        var ex = Assert.Throws<SqliteException>(() =>
            Exec(c, "DELETE FROM role_permissions WHERE role_id = 5 AND permission = 'plan.approve' AND effect = 'DENY'"));
        Assert.Contains("DENY", ex.Message);
        Assert.Equal(1L, Scalar(c,
            "SELECT count(*) FROM role_permissions WHERE role_id = 5 AND permission = 'plan.approve' AND effect = 'DENY'"));
    }

    [Fact]
    public void Migrated_ai_connection_reads_the_new_views_but_not_partner_documents()
    {
        using var db = Open();
        using var ai = db.OpenAiReadOnly();

        Assert.Equal(4L, Scalar(ai, "SELECT count(*) FROM v_ai_zones"));
        Assert.Equal(6L, Scalar(ai, "SELECT count(*) FROM v_ai_lots"));
        Assert.Equal(6L, Scalar(ai, "SELECT count(*) FROM v_ai_partners"));
        Assert.Throws<SqliteException>(() => Scalar(ai, "SELECT cnpj_enc FROM suppliers"));
    }

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

        Assert.Throws<DatabaseUnavailableException>(() => Open());
    }
}
