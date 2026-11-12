using erp_inteligente.Core.Knowledge;
using erp_inteligente.Core.Llm;
using Nexus.Tests.Core.Policy;
using Nexus.Tests.Fakes;

namespace Nexus.Tests.Core.Knowledge;

/// <summary>Feature 5 completed: vectors of the chunks (knowledge_embeddings, sqlite-vec) and the hybrid BM25 + cosine search (RRF).</summary>
public sealed class HybridSearchTests : IDisposable
{
    private readonly PolicyTestFixture _fx = new();
    private readonly ConceptEmbeddingProvider _embeddings = new(concepts:
    [
        ["gelado", "gelada", "refrigerado", "refrigerados", "frio", "camara", "congelado", "temperatura"],
        ["quebrado", "avaria", "avariado", "danificado", "violada"],
        ["devolucao", "devolver", "retorno"],
    ]);
    private readonly KnowledgeVectorIndex _index;
    private readonly KnowledgeRetriever _retriever;

    public HybridSearchTests()
    {
        _index = new KnowledgeVectorIndex(_fx.Db.Database, _embeddings, _fx.Time);
        _retriever = new KnowledgeRetriever(_fx.Db.Database, _index);
        KnowledgeIndexer.Sync(_fx.Db.Database, Sources);
    }

    public void Dispose() => _fx.Dispose();

    private static readonly IReadOnlyList<KnowledgeSource> Sources =
    [
        new("knowledge/armazenagem.md", "# Armazenagem\n\n## Câmara\nProdutos refrigerados vão para a câmara da zona C, abaixo de 8 graus.\n\n" +
                                        "## Endereço\nEscolha o endereço livre mais próximo da doca, respeitando a capacidade.\n"),
        new("knowledge/avarias.md", "# Avarias\n\n## Registro\nItem avariado ou com embalagem violada é ajustado com motivo e foto.\n"),
        new("knowledge/devolucao.md", "# Devolução\n\n## Cliente\nA devolução do cliente entra pela doca 2 com nota de retorno.\n"),
    ];

    private long Count(string sql) => _fx.Scalar(sql);

    // ---- index ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Sync_embeds_each_chunk_once_and_again_only_when_it_or_the_model_changes()
    {
        var first = await _index.SyncAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new VectorSyncResult(4, 0, first.Tokens, null), first);
        Assert.Equal(4, Count("SELECT count(*) FROM knowledge_embeddings WHERE dimensions = 32 AND length(embedding) = 128"));
        Assert.Equal(new VectorCoverage("fake:concepts", 4, 4, KnowledgeVectorIndex.SqliteVecAvailable), _index.Coverage());

        Assert.Equal(0, (await _index.SyncAsync(TestContext.Current.CancellationToken)).Embedded);

        // One section changes: only its new chunk is embedded; the old vector went with the old chunk.
        KnowledgeIndexer.Sync(_fx.Db.Database,
            [Sources[0], Sources[1], new("knowledge/devolucao.md", "# Devolução\n\n## Cliente\nA devolução entra pela doca 3.\n")]);
        Assert.Equal(3, Count("SELECT count(*) FROM knowledge_embeddings"));
        Assert.Equal(1, (await _index.SyncAsync(TestContext.Current.CancellationToken)).Embedded);

        // A new model never compares with old vectors: everything is embedded again.
        _embeddings.Model = "fake:other";
        Assert.Equal(0, _index.Coverage().WithVector);
        Assert.Equal(4, (await _index.SyncAsync(TestContext.Current.CancellationToken)).Embedded);
    }

    [Fact]
    public async Task Sync_stops_on_a_provider_failure_and_reports_what_is_left()
    {
        _embeddings.FailWith = LlmErrorKind.Unauthorized;
        var result = await _index.SyncAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new VectorSyncResult(0, 4, 0, LlmErrorKind.Unauthorized), result);
        Assert.Equal(0, Count("SELECT count(*) FROM knowledge_embeddings"));
    }

    [Fact]
    public async Task Sync_does_nothing_with_embeddings_off()
    {
        _embeddings.Model = null;
        Assert.Equal(new VectorSyncResult(0, 0, 0, null), await _index.SyncAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, _embeddings.Calls);
    }

    [Fact]
    public async Task Sqlite_vec_and_the_csharp_distance_agree()
    {
        await _index.SyncAsync(TestContext.Current.CancellationToken);
        var query = _embeddings.Vector("produto gelado");
        var nearest = _index.Nearest(query, "fake:concepts");

        Assert.Equal("Câmara", Heading(nearest[0].ChunkId));
        using var connection = _fx.Db.Database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT chunk_id, embedding FROM knowledge_embeddings";
        using var reader = command.ExecuteReader();
        var expected = new Dictionary<long, double>();
        while (reader.Read())
            expected[reader.GetInt64(0)] = KnowledgeVectorIndex.Cosine(query, KnowledgeVectorIndex.FromBlob((byte[])reader.GetValue(1)));
        foreach (var hit in nearest)
            Assert.Equal(expected[hit.ChunkId], hit.Similarity, 5);
    }

    [Fact]
    public void Sqlite_vec_loads_into_the_encrypted_connection_and_leaves_load_extension_disabled()
    {
        using var connection = _fx.Db.Database.OpenApplication();
        Assert.True(KnowledgeVectorIndex.TryLoadSqliteVec(connection), "vec0 should load on win-x64 and linux-x64");
        using var version = connection.CreateCommand();
        version.CommandText = "SELECT vec_version()";
        Assert.StartsWith("v0.1", (string)version.ExecuteScalar()!);
        using var sql = connection.CreateCommand();
        sql.CommandText = "SELECT load_extension('vec0')";
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => sql.ExecuteScalar());
    }

    [Fact]
    public void The_ai_connection_cannot_read_the_vectors()
    {
        using var ai = _fx.Db.Database.OpenAiReadOnly();
        using var command = ai.CreateCommand();
        command.CommandText = "SELECT embedding FROM knowledge_embeddings";
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => command.ExecuteScalar());
    }

    [Fact]
    public void Blob_format_is_little_endian_float32()
    {
        var blob = KnowledgeVectorIndex.ToBlob([1f, -2.5f]);
        Assert.Equal(new byte[] { 0, 0, 0x80, 0x3F, 0, 0, 0x20, 0xC0 }, blob);
        Assert.Equal([1f, -2.5f], KnowledgeVectorIndex.FromBlob(blob));
        Assert.Equal(1, KnowledgeVectorIndex.Cosine([1, 0], [2, 0]), 6);
        Assert.Equal(0, KnowledgeVectorIndex.Cosine([1, 0], [0, 0]));
    }

    private string Heading(long chunkId)
    {
        using var connection = _fx.Db.Database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT heading FROM knowledge_chunks WHERE id = $id";
        command.Parameters.AddWithValue("$id", chunkId);
        return (string)command.ExecuteScalar()!;
    }

    // ---- hybrid search ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Meaning_finds_a_section_that_shares_no_word_with_the_query()
    {
        await _index.SyncAsync(TestContext.Current.CancellationToken);
        Assert.Empty(_retriever.Search("onde ponho mercadoria gelada"));

        var hits = await _retriever.SearchHybridAsync("onde ponho mercadoria gelada", 3, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Câmara", hits[0].Heading);
        Assert.True(_retriever.IsHybrid);
    }

    [Fact]
    public async Task Words_and_meaning_are_fused_and_a_hit_in_both_lists_comes_first()
    {
        await _index.SyncAsync(TestContext.Current.CancellationToken);
        var hits = await _retriever.SearchHybridAsync("item danificado ajustado", 3, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Registro", hits[0].Heading); // "ajustado" by BM25, "danificado" ~ "avariado" by meaning
        Assert.Equal(2.0 / (KnowledgeRetriever.RrfK + 1), hits[0].Score, 6);
    }

    [Fact]
    public async Task Context_mode_needs_the_higher_similarity_for_hits_found_only_by_meaning()
    {
        await _index.SyncAsync(TestContext.Current.CancellationToken);
        _embeddings.Thresholds = new EmbeddingThresholds(Search: 0.3, Context: 0.99);
        Assert.NotEmpty(await _retriever.SearchHybridAsync("onde ponho mercadoria gelada", 2, minMatchedTerms: 2,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await _retriever.SearchHybridAsync("onde ponho mercadoria gelada", 2, minMatchedTerms: 2, forContext: true,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Provider_failure_or_embeddings_off_is_exactly_bm25()
    {
        await _index.SyncAsync(TestContext.Current.CancellationToken);
        var bm25 = _retriever.Search("devolução do cliente", 3);

        _embeddings.FailWith = LlmErrorKind.Unavailable;
        Assert.Equal(bm25, await _retriever.SearchHybridAsync("devolução do cliente", 3, cancellationToken: TestContext.Current.CancellationToken));

        _embeddings.FailWith = null;
        _embeddings.Model = null;
        Assert.Equal(bm25, await _retriever.SearchHybridAsync("devolução do cliente", 3, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(_retriever.IsHybrid);
    }

    [Fact]
    public async Task The_query_is_masked_before_it_goes_to_the_embeddings_provider()
    {
        await _index.SyncAsync(TestContext.Current.CancellationToken);
        _embeddings.Texts.Clear();
        await _retriever.SearchHybridAsync("devolução do cliente cpf 123.456.789-09 contato ana@exemplo.com",
            cancellationToken: TestContext.Current.CancellationToken);
        var sent = Assert.Single(_embeddings.Texts);
        Assert.DoesNotContain("123.456.789-09", sent);
        Assert.DoesNotContain("ana@exemplo.com", sent);
    }

    [Fact]
    public void Fusion_sums_reciprocal_ranks()
    {
        KnowledgeHit H(string heading) => new("k.md", heading, heading, 0);
        var fused = KnowledgeRetriever.Fuse([H("a"), H("b"), H("c")], [H("c"), H("d")], 4);
        Assert.Equal(["c", "a", "b", "d"], fused.Select(h => h.Heading)); // b and d tie: best rank, then path and heading
        Assert.Equal(1.0 / 63 + 1.0 / 61, fused[0].Score, 9);
    }
}
