using erp_inteligente.Core.Engine;
using erp_inteligente.Core.Memory;
using erp_inteligente.Models;
using Nexus.Tests.Core.Policy;

namespace Nexus.Tests.Core.Memory;

/// <summary>Feature 5 (section 8): confidence follows the plans the memories fed, and recurring errors become memories.</summary>
public sealed class MemoryFeedbackTests : IDisposable
{
    private readonly PolicyTestFixture _f = new();
    private readonly MemoryStore _store;
    private readonly MemoryFeedback _feedback;
    private readonly PlanEngine _engine;
    private readonly UserSession _joao;
    private readonly UserSession _maria;

    public MemoryFeedbackTests()
    {
        _store = new MemoryStore(_f.Db.Database, _f.Policy, _f.Audit, _f.Time);
        _feedback = new MemoryFeedback(_f.Db.Database, _store, _f.Audit, _f.Time);
        _engine = new PlanEngine(_f.Db.Database, _f.Policy, _f.Audit, _f.Time, _store, feedback: _feedback);
        _joao = _f.SessionFor("op.joao");
        _maria = _f.SessionFor("sup.maria");
    }

    public void Dispose() => _f.Dispose();

    private const string Move =
        """{"dsl_version":"1.0","intent":"putaway","skill":"armazenagem","steps":[{"op":"move_stock","args":{"sku":"ABC-123","qty":1,"from":"DOCA-01","to":"A-02-01"}}]}""";

    private const string Adjust =
        """{"dsl_version":"1.0","intent":"adjust","steps":[{"op":"adjust_stock","args":{"sku":"ABC-123","qty":-5,"location":"DOCA-01","reason":"Avaria: embalagem violada"}}]}""";

    private static string Overdraw(int qty) =>
        """{"dsl_version":"1.0","intent":"putaway","skill":"armazenagem","steps":[""" +
        $$$"""{"op":"move_stock","args":{"sku":"ABC-123","qty":{{{qty}}},"from":"DOCA-01","to":"A-02-01"}}]}""";

    private long AiMemory(string content) =>
        _store.Remember(_joao, MemoryKind.Fact, MemoryScope.Global, null, content, MemoryOrigin.Ai).Id;

    private double Confidence(long id) => _store.List(_maria).Single(m => m.Id == id).Confidence;

    private long Count(string sql) => _f.Scalar(sql);

    private UserSession AddSupervisor(string username)
    {
        var id = _f.Scalar("SELECT COALESCE(MAX(id), 0) + 1 FROM users");
        _f.Exec("INSERT INTO users (id, username, display_name, password_hash, role_id, warehouse_id) " +
                "VALUES ($id, $u, $u, 'no-login-in-tests', 2, 1)", ("$id", id), ("$u", username));
        return _f.SessionFor(username);
    }

    private List<MemoryRecord> Recurring() =>
        _store.List(_maria).Where(m => m.Content.StartsWith("Erro recorrente", StringComparison.Ordinal)).ToList();

    [Fact]
    public void Executed_ai_plan_raises_the_confidence_of_its_memories()
    {
        var used = AiMemory("A doca 01 recebe paletes de manhã.");
        var other = AiMemory("O endereço A-02-01 aceita só caixas pequenas.");

        var result = _engine.Submit(_joao, Move, "ai", memoryIds: [used]);

        Assert.Equal("executed", result.Status);
        Assert.Equal(MemoryStore.AiConfidence + MemoryFeedback.UsefulStep, Confidence(used), 6);
        Assert.Equal(MemoryStore.AiConfidence, Confidence(other), 6);
        Assert.Equal(1, Count("SELECT COUNT(*) FROM audit_log WHERE action = 'memory.useful' AND actor_type = 'system'"));
    }

    [Fact]
    public void Rejected_ai_plan_lowers_the_confidence_and_takes_a_fresh_ai_memory_out_of_the_context()
    {
        // A critical AI plan by a supervisor waits for another supervisor.
        var used = _store.Remember(_maria, MemoryKind.Fact, MemoryScope.Global, null,
            "Ajustes de avaria dispensam motivo detalhado.", MemoryOrigin.Ai).Id;
        var bia = AddSupervisor("sup.bia");

        var submitted = _engine.Submit(_maria, Adjust, "ai", memoryIds: [used]);
        Assert.Equal("pending_approval", submitted.Status);
        _engine.Reject(bia, submitted.PlanId!.Value, "Avaria precisa de laudo.");

        Assert.Equal(MemoryStore.AiConfidence - MemoryFeedback.ContradictedStep, Confidence(used), 6);
        Assert.True(Confidence(used) < 0.3); // the retriever only injects memories with confidence >= 0.3
        Assert.Equal(1, Count("SELECT COUNT(*) FROM audit_log WHERE action = 'memory.contradicted'"));
    }

    [Fact]
    public void Operator_plans_never_touch_memory_confidence()
    {
        var used = AiMemory("A doca 01 recebe paletes de manhã.");
        _engine.Submit(_joao, Move, "user", memoryIds: [used]);
        Assert.Equal(MemoryStore.AiConfidence, Confidence(used), 6);
        Assert.Equal(0, Count("SELECT COUNT(*) FROM audit_log WHERE action LIKE 'memory.useful%'"));
    }

    [Fact]
    public void Confidence_never_passes_the_reinforcement_ceiling()
    {
        var used = _store.Remember(_maria, MemoryKind.Fact, MemoryScope.Global, null, "Conferência dupla na doca.", MemoryOrigin.User).Id;
        for (var i = 0; i < 4; i++)
            Assert.Equal("executed", _engine.Submit(_joao, Move, "ai", memoryIds: [used]).Status);
        Assert.Equal(MemoryStore.MaxReinforcedConfidence, Confidence(used), 6);
    }

    [Fact]
    public void Recurring_execution_error_becomes_one_pattern_memory()
    {
        EngineSubmission? failed = null;
        for (var i = 1; i < MemoryFeedback.RecurrenceThreshold; i++)
        {
            failed = _engine.Submit(_joao, Overdraw(99999), "ai");
            Assert.Equal("failed", failed.Status);
        }
        Assert.Empty(Recurring());

        _engine.Submit(_joao, Overdraw(99998), "user");
        var codes = failed!.Errors.Select(e => e.Code).Distinct().ToList();
        var created = Recurring();
        Assert.Equal(codes.Count, created.Count); // one memory per recurring code
        foreach (var code in codes)
            Assert.Single(created, m => m.Content.StartsWith($"Erro recorrente ({code})", StringComparison.Ordinal));
        Assert.All(created, memory =>
        {
            Assert.Equal(MemoryKind.Pattern, memory.Kind);
            Assert.Equal(MemoryOrigin.System, memory.Origin);
            Assert.Equal(MemoryScope.Skill, memory.Scope);
            Assert.Equal("armazenagem", memory.ScopeRef);
            Assert.Equal(MemoryStore.SystemConfidence, memory.Confidence, 6);
        });

        // More failures of the same codes do not create more memories.
        _engine.Submit(_joao, Overdraw(99997), "ai");
        Assert.Equal(created.Count, Recurring().Count);
    }

    [Fact]
    public void Errors_of_another_warehouse_do_not_count()
    {
        var other = _f.SessionFor("op.joao") with { WarehouseId = 999 };
        var code = _engine.Submit(_joao, Overdraw(99999), "ai").Errors[0];
        Assert.Empty(_feedback.ObserveErrors(other, null, [code]));
        Assert.Empty(_feedback.ObserveErrors(_joao, null, [code])); // one occurrence is not a pattern
    }
}
