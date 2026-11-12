using erp_inteligente.Core.Knowledge;
using Nexus.Tests.Core.Policy;

namespace Nexus.Tests.Core.Knowledge;

public sealed class KnowledgeIndexTests : IDisposable
{
    private readonly PolicyTestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static KnowledgeSource Doc(string name, string body) =>
        new($"knowledge/{name}.md", $"# Doc {name}\n\n## Primeira\n{body}\n\n## Segunda\nTexto fixo da segunda seção de {name}.\n");

    [Fact]
    public void Sync_IsIdempotent_AndReplacesOnlyChangedChunks()
    {
        IReadOnlyList<KnowledgeSource> sources = [Doc("a", "Texto um."), Doc("b", "Texto b.")];

        var first = KnowledgeIndexer.Sync(_fx.Db.Database, sources);
        Assert.Equal(new IndexResult(4, 0, 0, 0), first);

        var second = KnowledgeIndexer.Sync(_fx.Db.Database, sources);
        Assert.Equal(new IndexResult(0, 0, 0, 4), second);
        Assert.False(second.Changed);

        long MaxIdOf(string path, string heading) => _fx.Scalar(
            "SELECT id FROM knowledge_chunks WHERE source_path = $p AND heading = $h", ("$p", path), ("$h", heading));
        var untouched = MaxIdOf("knowledge/b.md", "Primeira");

        var changed = KnowledgeIndexer.Sync(_fx.Db.Database, [Doc("a", "Texto novo e diferente."), Doc("b", "Texto b.")]);
        Assert.Equal(new IndexResult(0, 1, 0, 3), changed);
        Assert.Equal(untouched, MaxIdOf("knowledge/b.md", "Primeira"));
        Assert.Equal(4, _fx.Scalar("SELECT count(*) FROM knowledge_chunks"));
        Assert.Equal(1, _fx.Scalar("SELECT count(*) FROM knowledge_fts WHERE knowledge_fts MATCH '\"diferente\"'"));
        Assert.Equal(0, _fx.Scalar("SELECT count(*) FROM knowledge_fts WHERE knowledge_fts MATCH '\"um\"'"));
    }

    [Fact]
    public void Sync_DeletesChunksOfRemovedSourcesAndSections()
    {
        KnowledgeIndexer.Sync(_fx.Db.Database, [Doc("a", "x"), Doc("b", "y")]);
        var result = KnowledgeIndexer.Sync(_fx.Db.Database,
            [new KnowledgeSource("knowledge/a.md", "# Doc a\n\n## Primeira\nx\n")]);
        Assert.Equal(3, result.Deleted);
        Assert.Equal(1, _fx.Scalar("SELECT count(*) FROM knowledge_chunks"));
        Assert.Equal(0, _fx.Scalar("SELECT count(*) FROM knowledge_fts WHERE knowledge_fts MATCH '\"segunda\"'"));
    }

    [Fact]
    public void EmbeddedSources_ContainsThe22Procedures_WithoutReadme()
    {
        var sources = KnowledgeIndexer.EmbeddedSources();
        Assert.Equal(22, sources.Count);
        Assert.Contains(sources, s => s.Path == "knowledge/03-armazenagem.md");
        Assert.DoesNotContain(sources, s => s.Path.EndsWith("README.md", StringComparison.Ordinal));
    }

    [Fact]
    public void Retriever_OverRealDocs_FindsStorageProcedureForRefrigerated()
    {
        KnowledgeIndexer.Sync(_fx.Db.Database);
        var retriever = new KnowledgeRetriever(_fx.Db.Database);

        var hits = retriever.Search("armazenagem refrigerado");
        Assert.NotEmpty(hits);
        Assert.Contains(hits.Take(2), h => h.SourcePath == "knowledge/03-armazenagem.md");
        Assert.All(hits, h => Assert.True(h.Score > 0));
        Assert.True(hits.Count <= 4);
    }

    [Fact]
    public void Retriever_IsAccentInsensitive_AndSurvivesHostileQueries()
    {
        KnowledgeIndexer.Sync(_fx.Db.Database);
        var retriever = new KnowledgeRetriever(_fx.Db.Database);

        Assert.NotEmpty(retriever.Search("DIVERGENCIAS de recebimento"));
        foreach (var hostile in new[] { "\"", "*", "NEAR(", "a AND", "col:x", "(", "", "   ", "'; DROP TABLE knowledge_chunks;--" })
            _ = retriever.Search(hostile);
        Assert.True(_fx.Scalar("SELECT count(*) FROM knowledge_chunks") > 0);
        Assert.Empty(retriever.Search(""));
    }

    [Fact]
    public void Retriever_LimitAndMinScore()
    {
        KnowledgeIndexer.Sync(_fx.Db.Database);
        var retriever = new KnowledgeRetriever(_fx.Db.Database);
        Assert.True(retriever.Search("estoque endereço recebimento", limit: 2).Count <= 2);
        Assert.Empty(retriever.Search("estoque", minScore: 1e9));
    }

    [Fact]
    public async Task NullEmbeddingProvider_IsOff()
    {
        Assert.Null(NullEmbeddingProvider.Instance.Model);
        var error = await Assert.ThrowsAsync<erp_inteligente.Core.Llm.LlmException>(() =>
            NullEmbeddingProvider.Instance.EmbedAsync(["x"], CancellationToken.None));
        Assert.Equal(erp_inteligente.Core.Llm.LlmErrorKind.NotConfigured, error.Kind);
    }
}
