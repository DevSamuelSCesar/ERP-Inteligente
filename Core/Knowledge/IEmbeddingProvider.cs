using System.Threading;
using System.Threading.Tasks;

namespace erp_inteligente.Core.Knowledge;

/// <summary>
/// Extension point for vector search (references/ARQUITETURA.md section 8, RAG). Today retrieval is BM25 only
/// (FTS5) and no retriever calls this. The planned hybrid search takes the top-20 BM25 hits plus the top-20 by
/// cosine similarity (a <c>sqlite-vec</c> <c>vec0</c> table linked to <c>knowledge_chunks</c> and
/// <c>memories</c>) and merges them with Reciprocal Rank Fusion (score = sum of 1 / (60 + rank)). Chunks keep
/// their SHA-256, so only changed chunks would be embedded again.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>The embedding of <paramref name="text"/>, or null when no provider is configured.</summary>
    Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken);
}

/// <summary>The default provider: no embeddings, so retrieval stays lexical.</summary>
public sealed class NullEmbeddingProvider : IEmbeddingProvider
{
    public Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken) =>
        Task.FromResult<float[]?>(null);
}
