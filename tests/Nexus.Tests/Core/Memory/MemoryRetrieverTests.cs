using erp_inteligente.Core.Memory;
using erp_inteligente.Models;
using Nexus.Tests.Core.Policy;

namespace Nexus.Tests.Core.Memory;

public sealed class MemoryRetrieverTests : IDisposable
{
    private readonly PolicyTestFixture _fx = new();
    private readonly MemoryStore _store;
    private readonly MemoryRetriever _retriever;
    private readonly UserSession _sup;
    private readonly UserSession _op;

    public MemoryRetrieverTests()
    {
        _store = new MemoryStore(_fx.Db.Database, _fx.Policy, _fx.Audit, _fx.Time);
        _retriever = new MemoryRetriever(_fx.Db.Database, _fx.Policy);
        _sup = _fx.SessionFor("sup.maria");
        _op = _fx.SessionFor("op.joao");
    }

    public void Dispose() => _fx.Dispose();

    private long User(string content, MemoryScope scope = MemoryScope.Global, string? scopeRef = null) =>
        _store.Create(_sup, MemoryKind.Fact, scope, scopeRef, content).Id;

    private long Ai(string content, MemoryScope scope = MemoryScope.Global, string? scopeRef = null) =>
        _store.Remember(_op, MemoryKind.Fact, scope, scopeRef, content, MemoryOrigin.Ai).Id;

    [Fact]
    public void Search_AccentAndCaseInsensitive_AndFindsSeedMemories()
    {
        var hits = _retriever.Search(_op, "REFRIGERADOS zona C");
        Assert.Contains(hits, h => h.Content.Contains("refrigerados", StringComparison.OrdinalIgnoreCase));

        var id = User("Câmara de refrigeração fica na zona C");
        Assert.Contains(_retriever.Search(_op, "refrigeracao"), h => h.Id == id);
        Assert.Contains(_retriever.Search(_op, "REFRIGERAÇÃO"), h => h.Id == id);
    }

    [Fact]
    public void Search_ScopeBonusBeatsSlightlyBetterBm25()
    {
        var plain = User("Cristaleira exige manuseio lento");                     // confidence 0.8
        var scoped = Ai("Espelhos empilhar máximo duas camadas", MemoryScope.Sku, "ABC-123"); // confidence 0.3

        var without = _retriever.Search(_op, "cristaleira espelhos");
        Assert.Equal(plain, without[0].Id);

        var with = _retriever.Search(_op, "ABC-123 cristaleira espelhos");
        Assert.Equal(scoped, with[0].Id);
        Assert.Equal(plain, with[1].Id);
    }

    [Fact]
    public void Search_FiltersByConfidenceScopeAndLimit()
    {
        var low = Ai("Cristaleira A frágil");
        var high = User("Cristaleira B frágil");
        Assert.DoesNotContain(_retriever.Search(_op, "cristaleira", new MemorySearchOptions(MinConfidence: 0.5)), h => h.Id == low);
        Assert.Contains(_retriever.Search(_op, "cristaleira", new MemorySearchOptions(MinConfidence: 0.5)), h => h.Id == high);

        var skill = User("Cristaleira C frágil", MemoryScope.Skill, "armazenagem");
        var onlySkill = _retriever.Search(_op, "cristaleira", new MemorySearchOptions(Scope: MemoryScope.Skill, ScopeRef: "armazenagem"));
        Assert.Equal([skill], onlySkill.Select(h => h.Id));

        for (var i = 0; i < 15; i++) User($"Cristaleira variante {i} frágil");
        Assert.Equal(10, _retriever.Search(_op, "cristaleira", new MemorySearchOptions(Limit: 99)).Count);
        Assert.Single(_retriever.Search(_op, "cristaleira", new MemorySearchOptions(Limit: 0)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"")]
    [InlineData("*")]
    [InlineData("NEAR(")]
    [InlineData("a AND")]
    [InlineData("col:x")]
    [InlineData("(((")]
    [InlineData("\" OR content:*")]
    [InlineData("'; DROP TABLE memories; --")]
    public void Search_GarbageNeverThrows(string query)
    {
        User("Cristaleira exige manuseio lento");
        var hits = _retriever.Search(_op, query);
        Assert.NotNull(hits);
        Assert.True(_fx.Scalar("SELECT count(*) FROM memories") > 0);
    }

    [Fact]
    public void Search_ReturnsScoresBestFirst()
    {
        User("Cristaleira exige manuseio lento");
        User("Cristaleira");
        var hits = _retriever.Search(_op, "cristaleira manuseio");
        Assert.Equal(hits.OrderByDescending(h => h.Score).Select(h => h.Id), hits.Select(h => h.Id));
        Assert.All(hits, h => Assert.True(h.Score > 0));
    }

    [Theory]
    [InlineData("a-02-03 palete", "a-02-03", true)]
    [InlineData("ao lado de a-02-031", "a-02-03", false)]
    [InlineData("xa-02-03", "a-02-03", false)]
    [InlineData("sku abc-123.", "abc-123", true)]
    public void ContainsToken_RespectsBoundaries(string text, string token, bool expected) =>
        Assert.Equal(expected, MemoryRetriever.ContainsToken(text, token));
}
