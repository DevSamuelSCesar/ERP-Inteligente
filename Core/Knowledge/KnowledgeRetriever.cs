using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Memory;
using erp_inteligente.Core.Data;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Core.Knowledge;

/// <summary>A knowledge chunk found by <see cref="KnowledgeRetriever"/>; <see cref="Score"/> is -bm25 (higher is better).</summary>
public sealed record KnowledgeHit(string SourcePath, string Heading, string Content, double Score);

/// <summary>
/// Search over the indexed company procedures. Knowledge is global, not per warehouse. Runs on the application connection
/// with constant, parameterized SQL; the caller (the Planner) decides who may use it.
/// <list type="bullet">
/// <item><see cref="Search"/>: BM25 over <c>knowledge_fts</c>; the heading column weighs twice as much as the body.</item>
/// <item><see cref="SearchHybridAsync"/> (with <paramref name="vectors"/> and embeddings on): the top
/// <see cref="KnowledgeVectorIndex.Candidates"/> of BM25 and of cosine similarity, merged by Reciprocal Rank Fusion
/// (score = sum of 1 / (60 + rank)). When embeddings are off or fail, it is exactly <see cref="Search"/>.</item>
/// </list>
/// </summary>
public sealed class KnowledgeRetriever(NexusDatabase database, KnowledgeVectorIndex? vectors = null)
{
    public const int MaxLimit = 10;
    public const int RrfK = 60;

    /// <summary>The query embedding is skipped after this long: the search then stays BM25 for that request.</summary>
    public static readonly TimeSpan QueryEmbeddingTimeout = TimeSpan.FromSeconds(5);

    /// <summary>True when vectors take part in the search (embeddings configured).</summary>
    public bool IsHybrid => vectors?.Embeddings.Model is not null;

    /// <summary>
    /// Hybrid search. <paramref name="forContext"/> applies the stricter similarity threshold of the automatic context to
    /// hits found only by the vector (lexical hits keep the rules of <see cref="Search"/>). The query sent to the embeddings
    /// provider is masked by <see cref="Safety.SensitiveDataRedactor"/> first. Never throws for provider failures.
    /// </summary>
    public async Task<IReadOnlyList<KnowledgeHit>> SearchHybridAsync(string query, int limit = 4, double? minScore = null,
        int minMatchedTerms = 1, bool forContext = false, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        var lexical = Search(query, MaxLimit, minScore, minMatchedTerms);
        var model = vectors?.Embeddings.Model;
        if (vectors is null || model is null || FtsQuery.Terms(query).Count < Math.Max(1, minMatchedTerms))
            return lexical.Take(limit).ToList();

        float[] queryVector;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(QueryEmbeddingTimeout);
            var batch = await vectors.Embeddings.EmbedAsync([Safety.SensitiveDataRedactor.Redact(query)], timeout.Token).ConfigureAwait(false);
            queryVector = batch.Vectors[0];
        }
        catch (LlmException ex)
        {
            Trace.TraceWarning($"KnowledgeRetriever: query embedding failed ({ex.Kind}); BM25 only.");
            return lexical.Take(limit).ToList();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Trace.TraceWarning("KnowledgeRetriever: query embedding timed out; BM25 only.");
            return lexical.Take(limit).ToList();
        }

        var thresholds = vectors.Embeddings.Thresholds;
        IReadOnlyList<VectorHit> nearest;
        try
        {
            nearest = vectors.Nearest(queryVector, model);
        }
        catch (SqliteException ex)
        {
            Trace.TraceWarning($"KnowledgeRetriever: vector search failed (code {ex.SqliteErrorCode}); BM25 only.");
            return lexical.Take(limit).ToList();
        }
        var rows = Chunks(nearest.Select(h => h.ChunkId));
        var lexicalKeys = lexical.Select(h => (h.SourcePath, h.Heading)).ToHashSet();
        var minimum = forContext ? thresholds.Context : thresholds.Search;
        var semantic = nearest
            .Where(h => rows.ContainsKey(h.ChunkId))
            .Select(h => (Hit: rows[h.ChunkId], h.Similarity))
            // A vector hit that also matched the words is already vouched for; a vector-only hit needs the similarity.
            .Where(x => lexicalKeys.Contains((x.Hit.SourcePath, x.Hit.Heading)) || x.Similarity >= minimum)
            .Select(x => x.Hit)
            .ToList();
        return Fuse(lexical, semantic, limit);
    }

    /// <summary>Reciprocal Rank Fusion of two rankings, keyed by document and section.</summary>
    internal static IReadOnlyList<KnowledgeHit> Fuse(IReadOnlyList<KnowledgeHit> lexical, IReadOnlyList<KnowledgeHit> semantic, int limit)
    {
        var scores = new Dictionary<(string, string), (KnowledgeHit Hit, double Score, int Best)>();
        void Add(IReadOnlyList<KnowledgeHit> ranking)
        {
            for (var rank = 0; rank < ranking.Count; rank++)
            {
                var hit = ranking[rank];
                var key = (hit.SourcePath, hit.Heading);
                var add = 1.0 / (RrfK + rank + 1);
                scores[key] = scores.TryGetValue(key, out var s) ? (s.Hit, s.Score + add, Math.Min(s.Best, rank)) : (hit, add, rank);
            }
        }
        Add(lexical);
        Add(semantic);
        return scores.Values
            .OrderByDescending(s => s.Score).ThenBy(s => s.Best).ThenBy(s => s.Hit.SourcePath, StringComparer.Ordinal)
            .ThenBy(s => s.Hit.Heading, StringComparer.Ordinal)
            .Take(limit)
            .Select(s => s.Hit with { Score = s.Score })
            .ToList();
    }

    private Dictionary<long, KnowledgeHit> Chunks(IEnumerable<long> ids)
    {
        var list = ids.Distinct().ToList();
        var result = new Dictionary<long, KnowledgeHit>();
        if (list.Count == 0) return result;
        using var connection = database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT c.id, c.source_path, c.heading, c.content FROM knowledge_chunks c, json_each($ids) j WHERE c.id = j.value";
        command.Parameters.AddWithValue("$ids", "[" + string.Join(",", list) + "]");
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result[reader.GetInt64(0)] = new KnowledgeHit(reader.GetString(1), reader.GetString(2), reader.GetString(3), 0);
        return result;
    }

    private const int WideCandidates = 30;

    /// <summary>
    /// Best chunks for <paramref name="query"/>; empty for an empty or unusable query. Never throws for bad input.
    /// <paramref name="minMatchedTerms"/> keeps only chunks that contain at least that many distinct query terms
    /// (a query with fewer terms returns nothing): with OR semantics one common word is enough for a hit, which is
    /// noise when the text is injected automatically. Use 1 for an explicit search.
    /// </summary>
    public IReadOnlyList<KnowledgeHit> Search(string query, int limit = 4, double? minScore = null, int minMatchedTerms = 1)
    {
        var match = FtsQuery.Build(query);
        if (match is null) return [];
        limit = Math.Clamp(limit, 1, MaxLimit);
        var terms = FtsQuery.Terms(query);
        var needed = Math.Max(1, minMatchedTerms);
        if (terms.Count < needed) return [];
        var candidates = needed > 1 ? Math.Max(limit, WideCandidates) : limit;

        var hits = new List<KnowledgeHit>();
        try
        {
            using var connection = database.OpenApplication();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT c.source_path, c.heading, c.content, bm25(knowledge_fts, 2.0, 1.0) AS rank " +
                "FROM knowledge_fts JOIN knowledge_chunks c ON c.id = knowledge_fts.rowid " +
                "WHERE knowledge_fts MATCH $match ORDER BY rank LIMIT $limit";
            command.Parameters.AddWithValue("$match", match);
            command.Parameters.AddWithValue("$limit", candidates);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var score = -reader.GetDouble(3);
                if (minScore is { } min && score < min) continue;
                var hit = new KnowledgeHit(reader.GetString(0), reader.GetString(1), reader.GetString(2), score);
                if (needed > 1 && MatchedTerms(terms, hit) < needed) continue;
                hits.Add(hit);
                if (hits.Count >= limit) break;
            }
        }
        catch (SqliteException ex)
        {
            // The expression is built from quoted terms, so this is not expected; a search must never break a chat turn.
            // Only the type and the SQLite code are logged: no SQL text and no user text.
            Trace.TraceWarning($"KnowledgeRetriever: search failed ({ex.GetType().Name}, code {ex.SqliteErrorCode}).");
            return [];
        }
        return hits;
    }

    /// <summary>How many of the distinct <paramref name="terms"/> occur in the chunk (same word/prefix rule as <see cref="FtsQuery"/>).</summary>
    internal static int MatchedTerms(IReadOnlyList<string> terms, KnowledgeHit hit)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        var current = new System.Text.StringBuilder();
        foreach (var c in TextFold.Fold(hit.Heading + " " + hit.Content) + " ")
        {
            if (char.IsLetterOrDigit(c)) { current.Append(c); continue; }
            if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
        }
        return terms.Count(t => words.Contains(t) || (t.Length >= FtsQuery.PrefixFromLength && words.Any(w => w.StartsWith(t, StringComparison.Ordinal))));
    }
}
