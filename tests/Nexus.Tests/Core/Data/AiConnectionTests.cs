using erp_inteligente.Core.Data;
using Microsoft.Data.Sqlite;

namespace Nexus.Tests.Core.Data;

public class AiConnectionTests : IDisposable
{
    private readonly TestDatabase _t = new();
    private readonly SqliteConnection _ai;

    public AiConnectionTests() => _ai = _t.Database.OpenAiReadOnly();

    public void Dispose()
    {
        _ai.Dispose();
        _t.Dispose();
    }

    private void AssertDenied(string sql)
    {
        using var cmd = _ai.CreateCommand();
        cmd.CommandText = sql;
        var ex = Assert.Throws<SqliteException>(() =>
        {
            using var r = cmd.ExecuteReader();
            while (r.Read()) { }
        });
        // Statements and column reads fail with SQLITE_AUTH (23); a denied function fails with error 1 and "not authorized".
        Assert.True(ex.SqliteErrorCode == 23 || ex.Message.Contains("not authorized", StringComparison.OrdinalIgnoreCase), ex.Message);
    }

    [Theory]
    [InlineData("SELECT id, code, is_active FROM v_ai_zones")]
    [InlineData("SELECT sku, lot_code, expires_on FROM v_ai_lots")]
    [InlineData("SELECT kind, id, name, is_active FROM v_ai_partners")]
    public void ReadsZonesLotsAndPartnersThroughAiViews(string sql)
    {
        using var cmd = _ai.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
    }

    [Theory]
    [InlineData("SELECT cnpj_enc FROM suppliers")]
    [InlineData("SELECT contact_email_enc FROM customers")]
    [InlineData("SELECT cnpj_masked FROM suppliers")]
    [InlineData("SELECT document_masked FROM customers")]
    [InlineData("SELECT kind, cnpj_masked FROM v_ai_partners JOIN suppliers USING (id)")]
    public void PartnerDocumentsAndContacts_AreDenied(string sql) => AssertDenied(sql);

    [Fact]
    public void ReadsStockThroughAiView()
    {
        using var cmd = _ai.CreateCommand();
        cmd.CommandText = "SELECT sku, available_qty FROM v_ai_stock WHERE sku = $sku";
        cmd.Parameters.AddWithValue("$sku", "ABC-124");
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("ABC-124", r.GetString(0));
        Assert.Equal(25, r.GetInt64(1));
        Assert.False(r.Read());
    }

    [Theory]
    [InlineData("SELECT * FROM stock_balances")]
    [InlineData("SELECT * FROM users")]
    [InlineData("SELECT count(*) FROM users")]
    [InlineData("SELECT sku FROM products")]
    [InlineData("SELECT unit_cost_enc FROM products")]
    [InlineData("SELECT password_hash FROM v_users_masked JOIN users USING (id)")]
    [InlineData("SELECT * FROM audit_log")]
    public void BaseAndSensitiveReads_AreDenied(string sql) => AssertDenied(sql);

    [Fact]
    public void ReadsOnlyActiveSkillsThroughTheSkillView()
    {
        using (var rw = _t.Database.OpenApplication())
        {
            foreach (var (name, status) in new[] { ("recebimento", "active"), ("antigo", "archived") })
            {
                using var cmd = rw.CreateCommand();
                cmd.CommandText = "INSERT INTO skills (name, description, operations_json, body, version, status, origin, " +
                                  "created_by, created_at, updated_by, updated_at) VALUES ($name, " +
                                  "'Como conferir uma nota fiscal de recebimento antes de lançar.', '[]', 'Conferir a nota.', " +
                                  "1, $status, 'user', 2, '2026-11-08T10:00:00Z', 2, '2026-11-08T10:00:00Z')";
                cmd.Parameters.AddWithValue("$name", name);
                cmd.Parameters.AddWithValue("$status", status);
                cmd.ExecuteNonQuery();
            }
        }

        using var read = _ai.CreateCommand();
        read.CommandText = "SELECT name, version FROM v_ai_skills";
        using var r = read.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("recebimento", r.GetString(0));
        Assert.False(r.Read());
    }

    [Theory]
    [InlineData("SELECT name FROM skills")]
    [InlineData("SELECT count(*) FROM skills")]
    [InlineData("SELECT body FROM skill_versions")]
    [InlineData("SELECT count(*) FROM skill_versions")]
    public void SkillTables_AreDenied(string sql) => AssertDenied(sql);

    [Theory]
    [InlineData("INSERT INTO memories (scope, scope_ref, content) VALUES ('x','y','z')")]
    [InlineData("UPDATE stock_balances SET quantity = 0")]
    [InlineData("DELETE FROM stock_balances")]
    [InlineData("CREATE TABLE evil (a)")]
    [InlineData("ATTACH DATABASE ':memory:' AS other")]
    [InlineData("PRAGMA table_info(users)")]
    [InlineData("SELECT load_extension('x')")]
    [InlineData("BEGIN")]
    public void WritesDdlAndPragmas_AreDenied(string sql) => AssertDenied(sql);

    [Fact]
    public void Authorizer_AllowsExpectedActionsOnly()
    {
        Assert.Equal(SQLitePCL.raw.SQLITE_OK, AiAuthorizer.Decide(SQLitePCL.raw.SQLITE_SELECT, null, null, null));
        Assert.Equal(SQLitePCL.raw.SQLITE_OK, AiAuthorizer.Decide(SQLitePCL.raw.SQLITE_READ, "products", "", null));
        Assert.Equal(SQLitePCL.raw.SQLITE_DENY, AiAuthorizer.Decide(SQLitePCL.raw.SQLITE_READ, "users", "", null));
        Assert.Equal(SQLitePCL.raw.SQLITE_DENY, AiAuthorizer.Decide(SQLitePCL.raw.SQLITE_READ, "v_ai_stock", "cpf_enc", null));
        Assert.Equal(SQLitePCL.raw.SQLITE_OK, AiAuthorizer.Decide(SQLitePCL.raw.SQLITE_READ, "products", "sku", "v_ai_products"));
        Assert.Equal(SQLitePCL.raw.SQLITE_DENY, AiAuthorizer.Decide(SQLitePCL.raw.SQLITE_PRAGMA, "x", null, null));
    }

    [Fact]
    public void AiViews_ExposeNoSensitiveColumns()
    {
        using var rw = _t.Database.OpenApplication();
        var views = new List<string>();
        using (var cmd = rw.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'view' AND name LIKE 'v_ai_%'";
            using var r = cmd.ExecuteReader();
            while (r.Read()) views.Add(r.GetString(0));
        }
        Assert.True(views.Count >= 5);
        foreach (var view in views)
        {
            using var cmd = rw.CreateCommand();
            cmd.CommandText = $"SELECT name FROM pragma_table_info('{view}')"; // view names come from sqlite_schema
            using var r = cmd.ExecuteReader();
            while (r.Read())
                Assert.DoesNotContain(r.GetString(0), (IReadOnlySet<string>)AiAuthorizer.SensitiveColumns);
        }
    }
}
