using System.Text.Json.Nodes;
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Policy;
using erp_inteligente.Core.Skills;
using erp_inteligente.Models;
using Nexus.Tests.Core.Policy;

namespace Nexus.Tests.Core.Skills;

public sealed class SkillStoreTests : IDisposable
{
    private static readonly SkillDocument BuiltIn = new(
        "armazenagem", "Skill nativa de teste para guardar mercadoria nos endereços", ["move_stock"], "# Nativa\n\nTexto.");

    private readonly PolicyTestFixture _fx = new();
    private readonly SkillStore _store;
    private readonly UserSession _sup;
    private readonly UserSession _op;

    public SkillStoreTests()
    {
        _store = new SkillStore(_fx.Db.Database, _fx.Policy, _fx.Audit, _fx.Time, [BuiltIn]);
        _sup = _fx.SessionFor("sup.maria");
        _op = _fx.SessionFor("op.joao");
    }

    public void Dispose() => _fx.Dispose();

    private static SkillDocument Doc(string name = "recebimento", string body = "# Corpo\n\nSegredo do corpo.") =>
        new(name, "Use ao receber mercadoria de fornecedor: confere NF e guarda.", ["receive_goods"], body);

    [Fact]
    public void Save_CreatesVersionOne_ThenUpdatesToVersionTwo_WithHistory()
    {
        var created = _store.Save(_sup, Doc(), SkillOrigin.User, null);
        Assert.Equal(1, created.Version);
        Assert.Equal(SkillStatus.Active, created.Status);
        Assert.Equal("2026-11-07T12:00:00Z", created.UpdatedAt);
        Assert.NotNull(created.UpdatedByName);

        var updated = _store.Save(_sup, Doc(body: "# Novo\n\nTexto novo."), SkillOrigin.Ai, 1);
        Assert.Equal(2, updated.Version);
        Assert.Equal(SkillOrigin.Ai, updated.Origin);

        var history = _store.History(_sup, "recebimento");
        Assert.Equal([2, 1], history.Select(h => h.Version));
        Assert.Contains("Segredo", history[1].Body);
    }

    [Fact]
    public void Save_Operator_IsDeniedAndAudited()
    {
        Assert.Throws<PolicyDeniedException>(() => _store.Save(_op, Doc(), SkillOrigin.User, null));
        Assert.Equal(1, _fx.Scalar("SELECT count(*) FROM audit_log WHERE action = 'policy.denied' AND target = 'permission:skill.manage'"));
        Assert.Equal(0, _fx.Scalar("SELECT count(*) FROM skills"));
        Assert.Throws<PolicyDeniedException>(() => _store.Archive(_op, "recebimento"));
    }

    [Fact]
    public void Save_InvalidDocument_ThrowsWithIssues()
    {
        var ex = Assert.Throws<SkillValidationException>(() =>
            _store.Save(_sup, new SkillDocument("Ruim", "curta", ["x"], ""), SkillOrigin.User, null));
        Assert.True(ex.Issues.Count >= 3);
        Assert.Equal(0, _fx.Scalar("SELECT count(*) FROM skills"));
    }

    [Fact]
    public void Save_BuiltInOrigin_IsRejected() =>
        Assert.Throws<ArgumentException>(() => _store.Save(_sup, Doc(), SkillOrigin.BuiltIn, null));

    [Fact]
    public void Save_StaleExpectedVersion_Conflicts()
    {
        _store.Save(_sup, Doc(), SkillOrigin.User, null);
        _store.Save(_sup, Doc(body: "Segunda versão."), SkillOrigin.User, 1);
        var ex = Assert.Throws<SkillConflictException>(() => _store.Save(_sup, Doc(body: "Terceira."), SkillOrigin.User, 1));
        Assert.Equal("A skill foi alterada por outra pessoa. Recarregue.", ex.Message);
        Assert.Equal(2, _store.History(_sup, "recebimento").Count);
    }

    [Fact]
    public void Save_WritesAuditWithoutBody()
    {
        _store.Save(_sup, Doc(), SkillOrigin.User, null);
        _store.Save(_sup, Doc(body: "Outro texto."), SkillOrigin.Ai, null);
        using var c = _fx.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT action, target, actor_type, details_json FROM audit_log WHERE target = 'skill:recebimento' ORDER BY id";
        using var r = cmd.ExecuteReader();
        var rows = new List<(string, string, string, string)>();
        while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        Assert.Equal(["skill.create", "skill.update"], rows.Select(x => x.Item1));
        Assert.All(rows, x => Assert.Equal("user", x.Item3));
        Assert.All(rows, x => { Assert.DoesNotContain("Segredo", x.Item4); Assert.DoesNotContain("Outro texto", x.Item4); });
        var details = JsonNode.Parse(rows[1].Item4)!;
        Assert.Equal(2, (int)details["version"]!);
        Assert.Equal("ai", (string)details["origin"]!);
    }

    [Fact]
    public void ArchiveAndReactivate_Work_AndAreAudited()
    {
        _store.Save(_sup, Doc(), SkillOrigin.User, null);
        _store.Archive(_sup, "recebimento");
        Assert.Equal(SkillStatus.Archived, _store.List(_sup).Single(s => s.Name == "recebimento").Status);
        Assert.Throws<SkillStoreException>(() => _store.Archive(_sup, "recebimento"));
        _store.Reactivate(_sup, "recebimento");
        Assert.Equal(SkillStatus.Active, _store.List(_sup).Single(s => s.Name == "recebimento").Status);
        Assert.Equal(1, _fx.Scalar("SELECT count(*) FROM audit_log WHERE action = 'skill.archive'"));
        Assert.Equal(1, _fx.Scalar("SELECT count(*) FROM audit_log WHERE action = 'skill.reactivate'"));
    }

    [Fact]
    public void Archive_BuiltIn_IsRefused_AndUnknownToo()
    {
        var ex = Assert.Throws<SkillStoreException>(() => _store.Archive(_sup, "armazenagem"));
        Assert.Contains("nativas", ex.Message);
        Assert.Throws<SkillStoreException>(() => _store.Archive(_sup, "naoexiste"));
        Assert.Throws<SkillStoreException>(() => _store.Archive(_sup, "a'; DROP TABLE skills;--"));
    }

    [Fact]
    public void List_RequiresStockRead_AndShowsBuiltIns()
    {
        var list = _store.List(_op);
        var builtIn = Assert.Single(list);
        Assert.True(builtIn.IsBuiltIn);
        Assert.Equal(SkillOrigin.BuiltIn, builtIn.Origin);
        Assert.Equal(1, builtIn.Version);
    }

    [Fact]
    public void Personalize_OverridesBuiltIn_AndArchivingRestoresIt()
    {
        var custom = new SkillDocument("armazenagem", "Versão personalizada da armazenagem para o depósito", ["move_stock"], "Personalizado.");
        var saved = _store.Save(_sup, custom, SkillOrigin.User, null);
        Assert.True(saved.OverridesBuiltIn);

        var list = _store.List(_sup);
        var shown = Assert.Single(list);
        Assert.False(shown.IsBuiltIn);
        Assert.True(shown.OverridesBuiltIn);
        Assert.Equal("Personalizado.", shown.Body);

        _store.Archive(_sup, "armazenagem");
        list = _store.List(_sup);
        Assert.Equal(2, list.Count);
        Assert.Single(list, s => s.IsBuiltIn && s.Status == SkillStatus.Active);
        Assert.Single(list, s => !s.IsBuiltIn && s.Status == SkillStatus.Archived && s.OverridesBuiltIn);
    }

    [Fact]
    public void History_HostileName_ReturnsEmpty() =>
        Assert.Empty(_store.History(_sup, "x' OR '1'='1"));
}
