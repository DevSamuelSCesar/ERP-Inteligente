using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace erp_inteligente.Core.Knowledge;

/// <summary>Vectors of a batch of texts, in the same order, and the tokens the provider counted.</summary>
public sealed record EmbeddingBatch(IReadOnlyList<float[]> Vectors, int Tokens);

/// <summary>
/// Turns text into vectors for the hybrid RAG search (references/ARQUITETURA.md section 8, RAG). <see cref="Model"/>
/// identifies the vectors ("provider:model"): vectors of another model are never compared and are embedded again.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>"openai:text-embedding-3-small", "local:nexus-embed"...; null when embeddings are off (search stays BM25).</summary>
    string? Model { get; }

    /// <summary>
    /// Minimum cosine similarity for a vector-only hit of an explicit search, and the higher one for a hit that enters
    /// the automatic context without matching words. Each model spreads similarities differently.
    /// </summary>
    EmbeddingThresholds Thresholds { get; }

    /// <summary>One vector per text. Throws <see cref="Llm.LlmException"/> when the provider fails or is not configured.</summary>
    Task<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}

/// <summary>Cosine thresholds of a model (see <see cref="IEmbeddingProvider.Thresholds"/>).</summary>
public sealed record EmbeddingThresholds(double Search, double Context);

/// <summary>No embeddings: retrieval stays lexical.</summary>
public sealed class NullEmbeddingProvider : IEmbeddingProvider
{
    public static NullEmbeddingProvider Instance { get; } = new();

    public string? Model => null;

    public EmbeddingThresholds Thresholds { get; } = new(1, 1);

    public Task<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
        throw new Llm.LlmException(Llm.LlmErrorKind.NotConfigured, "Embeddings are off.");
}
