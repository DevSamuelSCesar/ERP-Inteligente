using erp_inteligente.Core.Memory;
using erp_inteligente.Core.Policy;
using Nexus.Tests.Core.Policy;

namespace Nexus.Tests.Core.Memory;

/// <summary>Feature 9: the memory maintenance job archives duplicates, near-duplicates and stale memories, never deletes.</summary>
public sealed class MemoryMaintenanceTests : IDisposable
{
    private readonly PolicyTestFixture _f = new();
    private readonly MemoryMaintenance _job;

    public MemoryMaintenanceTests() => _job = new MemoryMaintenance(_f.Db.Database, _f.Audit, _f.Time, _f.Policy);

    public void Dispose() => _f.Dispose();

    private long Add(string content, string origin = "ai", double confidence = 0.3, long uses = 0, string kind = "fact",
        string scope = "zone", string? scopeRef = "C", string created = "2026-11-01T10:00:00Z", string? lastUsed = null)
    {
        _f.Exec("INSERT INTO memories (warehouse_id, kind, scope, scope_ref, content, confidence, use_count, origin, created_by, created_at, last_used_at) " +
                "VALUES (1, $k, $s, $r, $c, $conf, $u, $o, 2, $at, $last)",
            ("$k", kind), ("$s", scope), ("$r", (object?)scopeRef ?? DBNull.Value), ("$c", content), ("$conf", confidence),
            ("$u", uses), ("$o", origin), ("$at", created), ("$last", (object?)lastUsed ?? DBNull.Value));
        return _f.Scalar("SELECT MAX(id) FROM memories");
    }

    private string Status(long id) => _f.Scalar($"SELECT status = 'active' FROM memories WHERE id = {id}") == 1 ? "active" : "archived";

    [Fact]
    public void Duplicates_keep_the_best_one_which_inherits_the_uses()
    {
        var weak = Add("A zona C é refrigerada.", confidence: 0.3, uses: 2);
        var strong = Add("a zona c e REFRIGERADA", origin: "system", confidence: 0.6, uses: 1);
        var report = _job.Run(_f.SessionFor("sup.maria"));

        Assert.Equal(1, report.Duplicates);
        Assert.Equal(("archived", "active"), (Status(weak), Status(strong)));
        Assert.Equal(3, _f.Scalar($"SELECT use_count FROM memories WHERE id = {strong}"));
    }

    [Fact]
    public void Similar_texts_in_the_same_scope_are_merged_and_other_scopes_are_not()
    {
        var a = Add("Paletes de bebida ficam sempre no corredor 3 da zona A", scopeRef: "A", confidence: 0.6);
        var b = Add("Paletes de bebida ficam sempre no corredor 3 na zona A", scopeRef: "A", confidence: 0.3);
        var other = Add("Paletes de bebida ficam sempre no corredor 3 na zona A", scopeRef: "B", confidence: 0.3);

        var report = _job.Run(_f.SessionFor("sup.maria"));

        Assert.Equal(1, report.Merged);
        Assert.Equal(("active", "archived", "active"), (Status(a), Status(b), Status(other)));
    }

    [Fact]
    public void Stale_memories_are_archived_but_never_the_supervisors()
    {
        var unusedAi = Add("Doca 2 abre às 6h.", created: "2026-09-01T10:00:00Z");
        var recentAi = Add("Doca 1 fecha às 18h.", created: "2026-11-05T10:00:00Z");
        var idleSystem = Add("Lote vencido vai para Q.", origin: "system", confidence: 0.6, uses: 4,
            created: "2026-01-01T10:00:00Z", lastUsed: "2026-04-01T10:00:00Z");
        var oldUser = Add("Regra antiga do supervisor.", origin: "user", confidence: 0.8, created: "2025-01-01T10:00:00Z");

        var report = _job.Run(_f.SessionFor("sup.maria"));

        Assert.Equal(2, report.Stale);
        Assert.Equal("archived", Status(unusedAi));
        Assert.Equal("active", Status(recentAi));
        Assert.Equal("archived", Status(idleSystem));
        Assert.Equal("active", Status(oldUser));
        Assert.Equal(0, _f.Scalar("SELECT COUNT(*) FROM memories WHERE status NOT IN ('active', 'archived')"));
    }

    [Fact]
    public void One_audit_row_per_run_with_counts_and_ids_and_the_daily_job_waits_a_day()
    {
        var id = Add("Doca 2 abre às 6h.", created: "2026-09-01T10:00:00Z");
        Assert.NotNull(_job.RunScheduled());
        Assert.Null(_job.RunScheduled());
        _f.Time.Advance(TimeSpan.FromHours(25));
        Assert.NotNull(_job.RunScheduled());

        Assert.Equal(2, _f.Scalar("SELECT COUNT(*) FROM audit_log WHERE action = 'memory.maintenance' AND actor_type = 'system'"));
        Assert.Equal(1, _f.Scalar($"SELECT COUNT(*) FROM audit_log WHERE action = 'memory.maintenance' AND details_json LIKE '%[{id}]%'"));
        Assert.Equal(0, _f.Scalar("SELECT COUNT(*) FROM audit_log WHERE action = 'memory.maintenance' AND details_json LIKE '%Doca%'"));
    }

    [Fact]
    public void Only_the_supervisor_runs_it_on_demand()
    {
        Assert.Throws<PolicyDeniedException>(() => _job.Run(_f.SessionFor("op.joao")));
    }
}
