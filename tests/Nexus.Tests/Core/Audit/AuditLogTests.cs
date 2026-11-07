using erp_inteligente.Core.Audit;
using Microsoft.Data.Sqlite;
using Nexus.Tests.Core;

namespace Nexus.Tests.Core.Audit;

public class AuditLogTests : IDisposable
{
    private readonly TestDatabase _t = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 7, 12, 10, 0, TimeSpan.Zero));

    public void Dispose() => _t.Dispose();

    private AuditLog Log() => new(_t.Database, _time);

    private static AuditEntry Entry(string action = "stock.move") =>
        new(AuditActorType.Ai, 5, 1, action, "sku:ABC-123", AuditOutcome.Allowed, """{"qty":2}""");

    private static long Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string Text(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (string)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void Append_ExtendsChain_WithContiguousIdsAndLinks()
    {
        var log = Log();
        var first = log.Append(Entry());
        _time.Advance(TimeSpan.FromSeconds(30));
        var second = log.Append(Entry("plan.submit"));

        Assert.Equal(5, first);
        Assert.Equal(6, second);

        using var c = _t.Database.OpenApplication();
        var seedLastHash = Text(c, "SELECT hash FROM audit_log WHERE id = 4");

        var rows = new List<(long Id, string OccurredAt, string PrevHash, string Hash, string Action)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, occurred_at, prev_hash, hash, action FROM audit_log WHERE id >= 5 ORDER BY id";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal("2026-11-07T12:10:00Z", rows[0].OccurredAt);
        Assert.Equal(seedLastHash, rows[0].PrevHash);
        Assert.Equal(
            AuditHasher.ComputeV1(seedLastHash, 5, "2026-11-07T12:10:00Z", "ai", 5, 1,
                "stock.move", "sku:ABC-123", "allowed", """{"qty":2}"""),
            rows[0].Hash);

        Assert.Equal("2026-11-07T12:10:30Z", rows[1].OccurredAt);
        Assert.Equal(rows[0].Hash, rows[1].PrevHash);
        Assert.Equal("plan.submit", rows[1].Action);

        var verification = AuditVerifier.Verify(c);
        Assert.True(verification.IsValid);
        Assert.Equal(6, verification.RowsChecked);
    }

    [Fact]
    public void AppendWithin_RolledBack_LeavesNoRow_AndNextAppendLinksCorrectly()
    {
        var log = Log();
        using (var writer = _t.Database.OpenApplication())
        {
            using var transaction = writer.BeginTransaction(deferred: false);
            log.AppendWithin(writer, transaction, Entry());
            transaction.Rollback();
        }

        using var c = _t.Database.OpenApplication();
        Assert.Equal(4, Scalar(c, "SELECT count(*) FROM audit_log"));

        var id = log.Append(Entry("plan.submit"));
        Assert.Equal(5, id);

        var seedLastHash = Text(c, "SELECT hash FROM audit_log WHERE id = 4");
        Assert.Equal(seedLastHash, Text(c, "SELECT prev_hash FROM audit_log WHERE id = 5"));
        Assert.True(AuditVerifier.Verify(c).IsValid);
    }

    [Fact]
    public void AppendWithin_Committed_PersistsRowWithCallerTransaction()
    {
        var log = Log();
        using (var writer = _t.Database.OpenApplication())
        {
            using var transaction = writer.BeginTransaction(deferred: false);
            var id = log.AppendWithin(writer, transaction, Entry());
            transaction.Commit();
            Assert.Equal(5, id);
        }

        using var c = _t.Database.OpenApplication();
        Assert.Equal(5, Scalar(c, "SELECT count(*) FROM audit_log"));
        Assert.True(AuditVerifier.Verify(c).IsValid);
    }

    [Fact]
    public async Task ConcurrentAppends_FromSeparateConnections_KeepChainValidAndIdsContiguous()
    {
        var tasks = Enumerable.Range(0, 2)
            .Select(_ => Task.Run(() =>
            {
                var log = Log();
                for (var i = 0; i < 20; i++) log.Append(Entry());
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        using var c = _t.Database.OpenApplication();
        Assert.Equal(44, Scalar(c, "SELECT count(*) FROM audit_log"));
        Assert.Equal(1, Scalar(c, "SELECT min(id) FROM audit_log"));
        Assert.Equal(44, Scalar(c, "SELECT max(id) FROM audit_log"));

        var verification = AuditVerifier.Verify(c);
        Assert.True(verification.IsValid);
        Assert.Equal(44, verification.RowsChecked);
    }
}
