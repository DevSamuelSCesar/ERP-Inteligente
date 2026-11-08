using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using erp_inteligente.Core.Memory;
using erp_inteligente.Core.Data;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Core.Knowledge;

/// <summary>A knowledge chunk found by <see cref="KnowledgeRetriever"/>; <see cref="Score"/> is -bm25 (higher is better).</summary>
public sealed record KnowledgeHit(string SourcePath, string Heading, string Content, double Score);

/// <summary>
/// BM25 search over the indexed company procedures (<c>knowledge_fts</c>). Knowledge is global, not per
/// warehouse. Runs on the application connection with constant, parameterized SQL; the caller (the Planner)
/// decides who may use it. The heading column weighs twice as much as the body.
/// </summary>
public sealed class KnowledgeRetriever(NexusDatabase database)
{
    public const int MaxLimit = 10;

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
