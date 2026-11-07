using erp_inteligente.Core.Audit;
using Microsoft.Data.Sqlite;

namespace Nexus.Tests.Core.Audit;

public class AuditVerifierTests : IDisposable
{
    private readonly TestDatabase _t = new();

    public void Dispose() => _t.Dispose();

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void SeededChain_IsValid_WithLastHashOfTheSeed()
    {
        using var c = _t.Database.OpenApplication();

        Assert.Equal(new AuditVerification(true, 4, null, null), AuditVerifier.Verify(c));

        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT hash FROM audit_log WHERE id = 4";
        Assert.Equal("9a69f1ea1772b917e4b897ac789b3b4fea63a484e896137f661e792a213e4b77", cmd.ExecuteScalar());
    }

    [Fact]
    public void EmptyChain_IsValid_WithZeroRows()
    {
        using var c = _t.Database.OpenApplication();
        Exec(c, "DROP TRIGGER audit_log_deny_delete");
        Exec(c, "DELETE FROM audit_log");

        Assert.Equal(new AuditVerification(true, 0, null, null), AuditVerifier.Verify(c));
    }

    [Fact]
    public void ModifiedAction_IsDetectedAtThatRow()
    {
        using var c = _t.Database.OpenApplication();
        Exec(c, "DROP TRIGGER audit_log_deny_update");
        Exec(c, "UPDATE audit_log SET action = 'stock.adjust' WHERE id = 2");

        var verification = AuditVerifier.Verify(c);

        Assert.False(verification.IsValid);
        Assert.Equal(2, verification.FirstBrokenId);
        Assert.Equal(1, verification.RowsChecked);
        Assert.Equal("hash does not match the recomputed value", verification.Reason);
    }

    [Fact]
    public void BrokenLink_IsDetectedAtThatRow()
    {
        using var c = _t.Database.OpenApplication();
        Exec(c, "DROP TRIGGER audit_log_deny_update");
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE audit_log SET prev_hash = $prev WHERE id = 3";
            cmd.Parameters.AddWithValue("$prev", new string('0', 64));
            cmd.ExecuteNonQuery();
        }

        var verification = AuditVerifier.Verify(c);

        Assert.False(verification.IsValid);
        Assert.Equal(3, verification.FirstBrokenId);
        Assert.Equal(2, verification.RowsChecked);
        Assert.Contains("prev_hash", verification.Reason);
    }
}
