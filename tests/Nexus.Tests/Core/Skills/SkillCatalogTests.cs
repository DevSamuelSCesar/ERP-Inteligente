using erp_inteligente.Core.Skills;
using Nexus.Tests.Core.Policy;

namespace Nexus.Tests.Core.Skills;

public sealed class SkillCatalogTests : IDisposable
{
    private static readonly SkillDocument Receiving = new(
        "recebimento", "Skill nativa de teste para receber mercadoria no depósito", ["receive_goods"], "Corpo nativo de recebimento.");
    private static readonly SkillDocument Shipping = new(
        "expedicao", "Skill nativa de teste para expedir pedidos de saída", ["ship_order"], "Corpo nativo de expedição.");
    private static readonly SkillDocument General = new(
        "geral", "Skill nativa de teste sem operações, vale para qualquer pessoa", [], "Corpo geral.");

    private readonly PolicyTestFixture _fx = new();
    private readonly SkillStore _store;
    private readonly SkillCatalog _catalog;

    public SkillCatalogTests()
    {
        SkillDocument[] builtIns = [Receiving, Shipping, General];
        _store = new SkillStore(_fx.Db.Database, _fx.Policy, _fx.Audit, _fx.Time, builtIns);
        _catalog = new SkillCatalog(_fx.Db.Database, builtIns);
    }

    public void Dispose() => _fx.Dispose();

    private static readonly IReadOnlySet<string> ReceiveRights = new HashSet<string> { "stock.receive" };

    [Fact]
    public void Index_FiltersByRights_AndIsSorted()
    {
        Assert.Equal(["geral", "recebimento"], _catalog.Index(ReceiveRights).Select(e => e.Name));
        Assert.Equal(["geral"], _catalog.Index(new HashSet<string>()).Select(e => e.Name));
        Assert.Equal(["expedicao", "geral", "recebimento"],
            _catalog.Index(new HashSet<string> { "stock.receive", "stock.ship" }).Select(e => e.Name));
    }

    [Fact]
    public void Index_RespectsMax() =>
        Assert.Single(_catalog.Index(new HashSet<string> { "stock.receive", "stock.ship" }, 1));

    [Fact]
    public void Index_DoesNotCarryBody()
    {
        var entry = _catalog.Index(ReceiveRights).First();
        Assert.DoesNotContain(typeof(SkillIndexEntry).GetProperties(), p => p.Name == "Body");
        Assert.Equal(1, entry.Version);
    }

    [Fact]
    public void DatabaseSkill_OverridesBuiltIn_InIndexAndLoad()
    {
        var sup = _fx.SessionFor("sup.maria");
        _store.Save(sup, new SkillDocument("recebimento", "Versão personalizada do recebimento do depósito", ["receive_goods"], "Corpo do banco."),
            SkillOrigin.User, null);
        var entry = Assert.Single(_catalog.Index(ReceiveRights), e => e.Name == "recebimento");
        Assert.Equal("Versão personalizada do recebimento do depósito", entry.Description);
        Assert.Equal("Corpo do banco.", _catalog.Load("recebimento")!.Body);
    }

    [Fact]
    public void ArchivedOverride_FallsBackToBuiltIn()
    {
        var sup = _fx.SessionFor("sup.maria");
        _store.Save(sup, new SkillDocument("recebimento", "Versão personalizada do recebimento do depósito", ["receive_goods"], "Corpo do banco."),
            SkillOrigin.User, null);
        _store.Archive(sup, "recebimento");
        Assert.Equal("Corpo nativo de recebimento.", _catalog.Load("recebimento")!.Body);
        Assert.Contains(_catalog.Index(ReceiveRights), e => e.Name == "recebimento");
    }

    [Fact]
    public void ArchivedDatabaseSkill_IsHidden()
    {
        var sup = _fx.SessionFor("sup.maria");
        _store.Save(sup, new SkillDocument("inventario", "Skill criada pela pessoa para contagem de estoque", [], "Corpo."), SkillOrigin.User, null);
        Assert.NotNull(_catalog.Load("inventario"));
        _store.Archive(sup, "inventario");
        Assert.Null(_catalog.Load("inventario"));
        Assert.DoesNotContain(_catalog.Index(ReceiveRights), e => e.Name == "inventario");
    }

    [Fact]
    public void Load_ReturnsBody_AndHonorsRights()
    {
        Assert.Equal("Corpo nativo de recebimento.", _catalog.Load("recebimento")!.Body);
        Assert.Null(_catalog.Load("expedicao", ReceiveRights));
        Assert.NotNull(_catalog.Load("geral", ReceiveRights));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a'--")]
    [InlineData("")]
    [InlineData("Recebimento")]
    [InlineData("recebimento\n")]
    public void Load_HostileNames_ReturnNull(string name) => Assert.Null(_catalog.Load(name));
}
