using erp_inteligente.Core.Knowledge;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Memory;

namespace Nexus.Tests.Fakes;

/// <summary>
/// Deterministic embeddings for tests: words of the same concept (e.g. "gelado", "refrigerado", "frio") share a dimension,
/// other words hash into noise dimensions. So a query and a text with no word in common can still be close, like a real
/// model, without any API. Records every text it receives and can be told to fail.
/// </summary>
public sealed class ConceptEmbeddingProvider(string? model = "fake:concepts", params string[][] concepts) : IEmbeddingProvider
{
    public const int ConceptDimensions = 16;
    public const int Dimensions = 32;

    private readonly Dictionary<string, int> _concepts = Build(concepts);

    public string? Model { get; set; } = model;

    public EmbeddingThresholds Thresholds { get; set; } = new(0.3, 0.6);

    /// <summary>When set, every call throws an <see cref="LlmException"/> of this kind.</summary>
    public LlmErrorKind? FailWith { get; set; }

    public List<string> Texts { get; } = [];

    public int Calls { get; private set; }

    public Task<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        Calls++;
        if (FailWith is { } kind) throw new LlmException(kind, "fake failure");
        Texts.AddRange(texts);
        return Task.FromResult(new EmbeddingBatch(texts.Select(Vector).ToList(), texts.Sum(t => t.Length / 4)));
    }

    public float[] Vector(string text)
    {
        var vector = new float[Dimensions];
        foreach (var word in TextFold.Fold(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                     .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray())).Where(w => w.Length > 2))
        {
            if (_concepts.TryGetValue(word, out var concept)) vector[concept] += 3;
            else vector[ConceptDimensions + (int)((uint)StableHash(word) % (Dimensions - ConceptDimensions))] += 1;
        }
        if (vector.All(v => v == 0)) vector[Dimensions - 1] = 1;
        return vector;
    }

    private static Dictionary<string, int> Build(string[][] concepts)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < concepts.Length && i < ConceptDimensions; i++)
            foreach (var word in concepts[i])
                map[TextFold.Fold(word)] = i;
        return map;
    }

    private static int StableHash(string text)
    {
        unchecked
        {
            var hash = 17;
            foreach (var c in text) hash = hash * 31 + c;
            return hash;
        }
    }
}
