using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Security;
using Microsoft.Data.Sqlite;

namespace Nexus.Tests.Core.Data;

/// <summary>
/// Databases created by schema versions 1 to 5 (scripts copied from git history into Fixtures/) are upgraded to the
/// current version on open, keeping their data. Version 2 → 3 adds the catalog flags, the AI operational rights and
/// the v_ai_* views (migrations/003_full_catalog.sql). Version 4 → 5 adds skills and v_ai_skills (migrations/005_skills.sql).
/// Version 5 → 6 rebuilds memories with warehouse, status and origin, and guards them (migrations/006_memory.sql).
/// Version 6 → 7 adds the recipe columns, guards and v_ai_recipes with the template (migrations/007_recipes.sql).
/// </summary>
public sealed class MigrationTests : IDisposable
{
    private const string OldOperatorHash =
        "$argon2id$v=19$m=65536,t=3,p=4$4H3UK/Bmc+aoDlJBaqyenA$qVxvHSzAzpt4OnIntR3nD14T/SSD/TSRo8ARXm5bBeU";

    private static readonly string[] Version1Scripts = ["schema_v1.sql", "security_v1.sql", "seed_v1.sql"];
    private static readonly string[] Version2Scripts = ["schema_v2.sql", "security_v2.sql", "seed_v2.sql"];
    private static readonly string[] Version3Scripts = ["schema_v3.sql", "security_v3.sql", "seed_v3.sql"];
    private static readonly string[] Version4Scripts = ["schema_v4.sql", "security_v4.sql", "seed_v4.sql"];
    private static readonly string[] Version5Scripts = ["schema_v5.sql", "security_v5.sql", "seed_v5.sql"];
    private static readonly string[] Version6Scripts = ["schema_v6.sql", "security_v6.sql", "seed_v6.sql"];
    private static readonly string[] Version7Scripts = ["schema_v7.sql", "security_v7.sql", "seed_v7.sql"];

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

    /// <summary>Checks what the current schema (version 7) must have: migrations/007_recipes.sql on top of the version 6 objects.</summary>
    private static void AssertVersion8Schema(SqliteConnection c)
    {
        Assert.Equal(8L, Scalar(c, "PRAGMA user_version"));
        Assert.Equal("id,occurred_at,user_id,warehouse_id,role,provider,model,calls,prompt_tokens,completion_tokens,result", ColumnList(c, "llm_usage"));
        Assert.Equal(3L, Scalar(c, "SELECT count(*) FROM sqlite_schema WHERE name IN ('llm_usage_by_time', 'llm_usage_deny_update', 'llm_usage_deny_delete')"));
        AssertVersion7Schema(c);
    }

    /// <summary>Checks what migrations/007_recipes.sql must have produced on top of the version 6 objects.</summary>
    private static void AssertVersion7Schema(SqliteConnection c)
    {
        Assert.EndsWith(",skill,signature,example_params_json,failure_streak,source_plan_id,promoted_by,created_at,promoted_at,last_used_at,status_reason",
            ColumnList(c, "recipes"));
        Assert.Equal(1L, Scalar(c, "SELECT count(*) FROM sqlite_schema WHERE type = 'index' AND name = 'recipes_by_signature'"));
        Assert.Equal(2L, Scalar(c, "SELECT count(*) FROM sqlite_schema WHERE type = 'trigger' AND name IN ('recipes_deny_delete', 'recipes_guard_update')"));
        Assert.Equal("id,name,description,intent,skill,plan_template_json,params_schema_json,status", ColumnList(c, "v_ai_recipes"));
        // The seeded recipe keeps its id and uses, and gets the structure and example values of a fresh install.
        Assert.Equal("move_stock(from,qty,sku,to)", Scalar(c, "SELECT signature FROM recipes WHERE id = 1 AND name = 'armazenar_da_doca'"));
        Assert.Equal(7L, Scalar(c, "SELECT success_count FROM recipes WHERE id = 1"));
        // recipe.manage joins the list the AI role can never get.
        Assert.Contains("'recipe.manage'", (string)Scalar(c, "SELECT sql FROM sqlite_schema WHERE name = 'role_permissions_deny_ai_escalation'")!);
        AssertVersion6Schema(c);
    }

    /// <summary>Checks what migrations/006_memory.sql must have produced on top of the version 5 objects.</summary>
    private static void AssertVersion6Schema(SqliteConnection c)
    {
        Assert.Equal(1L, Scalar(c, "SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name = 'memories'"));
        Assert.Equal(1L, Scalar(c, "SELECT count(*) FROM sqlite_schema WHERE type = 'index' AND name = 'memories_by_warehouse'"));
        Assert.Equal(5L, Scalar(c, "SELECT count(*) FROM sqlite_schema WHERE type = 'trigger' AND name IN " +
            "('memories_fts_insert', 'memories_fts_delete', 'memories_fts_update', 'memories_deny_delete', 'memories_guard_update')"));
        Assert.Equal("id,warehouse_id,kind,scope,scope_ref,content,confidence,use_count,status,origin,created_by,source_plan_id,created_at,updated_at,last_used_at",
            ColumnList(c, "memories"));
        // The four seeded memories keep their ids and text, and get CD-01, active status, user origin and sup.maria as creator.
        Assert.Equal(4L, Scalar(c, "SELECT count(*) FROM memories WHERE warehouse_id = 1 AND status = 'active' AND origin = 'user' AND created_by = 2"));
        Assert.Equal("armazenagem", Scalar(c,
            "SELECT m.scope_ref FROM memories_fts f JOIN memories m ON m.id = f.rowid WHERE memories_fts MATCH 'refrigerados'"));
        // Supervisor (2) may manage memories; nobody else does, and the AI never does.
        Assert.Equal(1L, Scalar(c, "SELECT count(*) FROM v_effective_permissions WHERE role_id = 2 AND permission = 'memory.manage'"));
        Assert.Equal(0L, Scalar(c, "SELECT count(*) FROM v_effective_permissions WHERE permission = 'memory.manage' AND role_id <> 2"));
        Assert.Equal(1L, Scalar(c,
            "SELECT count(*) FROM role_permissions WHERE role_id = 5 AND effect = 'DENY' AND permission = 'memory.manage'"));
        AssertVersion5Schema(c);
    }

    /// <summary>Checks what migrations/005_skills.sql must have produced (same as a fresh schema 5 database).</summary>
    private static void AssertVersion5Schema(SqliteConnection c)
    {
        Assert.Equal(2L, Scalar(c, "SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name IN ('skills', 'skill_versions')"));
        Assert.Equal(4L, Scalar(c, "SELECT count(*) FROM sqlite_schema WHERE type = 'trigger' AND name IN " +
            "('skills_deny_delete', 'skills_guard_update', 'skill_versions_deny_update', 'skill_versions_deny_delete')"));
        Assert.Equal(1L, Scalar(c, "SELECT count(*) FROM sqlite_schema WHERE type = 'view' AND name = 'v_ai_skills'"));
        Assert.Equal("name,description,operations_json,body,version", ColumnList(c, "v_ai_skills"));
        Assert.Equal(0L, Scalar(c, "SELECT count(*) FROM skills"));
        // Supervisor (2) may manage skills; nobody else does, and the AI never does.
        Assert.Equal(1L, Scalar(c, "SELECT count(*) FROM v_effective_permissions WHERE role_id = 2 AND permission = 'skill.manage'"));
        Assert.Equal(0L, Scalar(c, "SELECT count(*) FROM v_effective_permissions WHERE permission = 'skill.manage' AND role_id <> 2"));
        Assert.Equal(1L, Scalar(c,
            "SELECT count(*) FROM role_permissions WHERE role_id = 5 AND effect = 'DENY' AND permission = 'skill.manage'"));
        Assert.Equal(1L, Scalar(c,
            "SELECT count(*) FROM sqlite_schema WHERE type = 'trigger' AND name = 'role_permissions_deny_ai_escalation'"));
        AssertVersion4Schema(c);
    }

    /// <summary>Checks what migrations/004_reservations.sql must have produced (same as a fresh schema 4 database).</summary>
    private static void AssertVersion4Schema(SqliteConnection c)
    {
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

        AssertVersion8Schema(c);
    }

    [Fact]
    public void Version_3_database_is_upgraded_to_8_with_the_reservation_backfill()
    {
        var path = CreateLegacyDatabase("v3.db", 3, Version3Scripts);
        using var db = Open(path);
        using var c = db.OpenApplication();

        AssertVersion8Schema(c);
        Assert.Equal(13L, Scalar(c, "SELECT count(*) FROM stock_balances"));
        Assert.True(AuditVerifier.Verify(c).IsValid);
    }

    [Fact]
    public void Version_2_database_is_upgraded_to_8_and_keeps_its_data()
    {
        var path = CreateLegacyDatabase("v2.db", 2, Version2Scripts);
        using var db = Open(path);
        using var c = db.OpenApplication();

        AssertVersion8Schema(c);
        Assert.Equal(13L, Scalar(c, "SELECT count(*) FROM stock_balances"));
        Assert.True(AuditVerifier.Verify(c).IsValid);
    }

    [Fact]
    public void Version_4_database_is_upgraded_to_8_and_keeps_its_data()
    {
        var path = CreateLegacyDatabase("v4.db", 4, Version4Scripts);
        using var db = Open(path);
        using var c = db.OpenApplication();

        AssertVersion8Schema(c);
        Assert.Equal(2L, Scalar(c, "SELECT count(*) FROM plans"));
        Assert.Equal(13L, Scalar(c, "SELECT count(*) FROM stock_balances"));
        Assert.Equal(2L, Scalar(c, "SELECT count(*) FROM order_reservations"));
        Assert.Equal(4L, Scalar(c, "SELECT count(*) FROM audit_log"));
        Assert.True(AuditVerifier.Verify(c).IsValid);
    }

    [Fact]
    public void Version_5_database_is_upgraded_to_8_and_keeps_its_memories_and_search()
    {
        var path = CreateLegacyDatabase("v5.db", 5, Version5Scripts);
        using var db = Open(path);
        using var c = db.OpenApplication();

        AssertVersion8Schema(c);
        Assert.Equal(2L, Scalar(c, "SELECT count(*) FROM plans"));
        Assert.Equal(13L, Scalar(c, "SELECT count(*) FROM stock_balances"));
        Assert.Equal(4L, Scalar(c, "SELECT count(*) FROM audit_log"));
        Assert.True(AuditVerifier.Verify(c).IsValid);
    }

    [Fact]
    public void Version_6_database_is_upgraded_to_8_and_keeps_its_recipe_plans_and_audit()
    {
        var path = CreateLegacyDatabase("v6.db", 6, Version6Scripts);
        using var db = Open(path);
        using var c = db.OpenApplication();

        AssertVersion8Schema(c);
        Assert.Equal(2L, Scalar(c, "SELECT count(*) FROM plans"));
        Assert.Equal(4L, Scalar(c, "SELECT count(*) FROM memories"));
        Assert.Equal(4L, Scalar(c, "SELECT count(*) FROM audit_log"));
        Assert.True(AuditVerifier.Verify(c).IsValid);
        Assert.Contains("DENY", Assert.Throws<SqliteException>(() => Exec(c, "DELETE FROM recipes WHERE id = 1")).Message);
        Assert.Contains("DENY", Assert.Throws<SqliteException>(() =>
            Exec(c, "UPDATE recipes SET plan_template_json = '{}' WHERE id = 1")).Message);
        Assert.Contains("DENY", Assert.Throws<SqliteException>(() =>
            Exec(c, "INSERT INTO role_permissions (role_id, permission, effect) VALUES (5, 'recipe.manage', 'ALLOW')")).Message);
    }

    [Fact]
    public void Version_7_database_is_upgraded_to_8_and_usage_is_append_only()
    {
        var path = CreateLegacyDatabase("v7.db", 7, Version7Scripts);
        using var db = Open(path);
        using var c = db.OpenApplication();

        AssertVersion8Schema(c);
        Assert.Equal(4L, Scalar(c, "SELECT count(*) FROM audit_log"));
        Assert.True(AuditVerifier.Verify(c).IsValid);
        Exec(c, "INSERT INTO llm_usage (occurred_at, role, provider, model, calls, prompt_tokens, completion_tokens, result) " +
                "VALUES ('2026-11-11T10:00:00Z', 'planner', 'claude', 'claude-sonnet-5-5', 1, 10, 5, 'answer')");
        Assert.Contains("DENY", Assert.Throws<SqliteException>(() => Exec(c, "DELETE FROM llm_usage")).Message);
    }

    [Fact]
    public void Migrated_database_guards_memories()
    {
        using var db = Open(CreateLegacyDatabase("v5-memory.db", 5, Version5Scripts));
        using var c = db.OpenApplication();

        Assert.Contains("DENY", Assert.Throws<SqliteException>(() => Exec(c, "DELETE FROM memories WHERE id = 1")).Message);
        Assert.Contains("DENY", Assert.Throws<SqliteException>(() =>
            Exec(c, "UPDATE memories SET warehouse_id = 2 WHERE id = 1")).Message);
        Assert.Contains("DENY", Assert.Throws<SqliteException>(() =>
            Exec(c, "UPDATE memories SET origin = 'ai' WHERE id = 1")).Message);
        Assert.Contains("DENY", Assert.Throws<SqliteException>(() =>
            Exec(c, "UPDATE memories SET created_by = 1 WHERE id = 1")).Message);
        Assert.Contains("CHECK", Assert.Throws<SqliteException>(() =>
            Exec(c, "INSERT INTO memories (warehouse_id, kind, scope, content, origin) VALUES (1, 'fact', 'mundo', 'x', 'ai')")).Message);

        // Content may change, and the full-text index follows it.
        Exec(c, "UPDATE memories SET content = 'Caixas frágeis vão para a zona B.' WHERE id = 1");
        Assert.Equal(0L, Scalar(c, "SELECT count(*) FROM memories_fts WHERE memories_fts MATCH 'refrigerados'"));
        Assert.Equal(1L, Scalar(c, "SELECT count(*) FROM memories_fts WHERE memories_fts MATCH 'frágeis'"));
    }

    [Fact]
    public void Migrated_database_guards_skills_and_their_history()
    {
        using var db = Open(CreateLegacyDatabase("v4-skills.db", 4, Version4Scripts));
        using var c = db.OpenApplication();

        Exec(c, "INSERT INTO skills (name, description, operations_json, body, version, status, origin, created_by, " +
                "created_at, updated_by, updated_at) VALUES ('recebimento', 'Como conferir uma nota fiscal de recebimento.', " +
                "'[\"receive_stock\"]', 'Conferir a nota antes de lançar.', 2, 'active', 'user', 2, " +
                "'2026-11-08T10:00:00Z', 2, '2026-11-08T10:00:00Z')");
        Exec(c, "INSERT INTO skill_versions (skill_id, version, description, operations_json, body, origin, saved_by, saved_at) " +
                "VALUES (1, 1, 'Versão anterior da skill.', '[]', 'Texto antigo.', 'user', 2, '2026-11-08T09:00:00Z')");

        Assert.Contains("DENY", Assert.Throws<SqliteException>(() => Exec(c, "DELETE FROM skills")).Message);
        Assert.Contains("DENY", Assert.Throws<SqliteException>(() => Exec(c, "DELETE FROM skill_versions")).Message);
        Assert.Contains("DENY", Assert.Throws<SqliteException>(() =>
            Exec(c, "UPDATE skill_versions SET body = 'alterado'")).Message);
        Assert.Contains("DENY", Assert.Throws<SqliteException>(() =>
            Exec(c, "UPDATE skills SET name = 'outro'")).Message);
        Assert.Contains("DENY", Assert.Throws<SqliteException>(() =>
            Exec(c, "UPDATE skills SET version = 1")).Message);

        Exec(c, "UPDATE skills SET version = 3, body = 'Conferir a nota e as quantidades.', updated_by = 2");
        Assert.Equal(3L, Scalar(c, "SELECT version FROM skills WHERE name = 'recebimento'"));
    }

    [Theory]
    [InlineData("plan.approve")]
    [InlineData("user.manage")]
    [InlineData("sensitive.read")]
    [InlineData("role.manage")]
    [InlineData("audit.read")]
    [InlineData("skill.manage")]
    [InlineData("memory.manage")]
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
