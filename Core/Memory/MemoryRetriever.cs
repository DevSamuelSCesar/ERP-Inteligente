using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Knowledge;
using erp_inteligente.Core.Policy;
using erp_inteligente.Models;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Core.Memory;

/// <summary>
/// Finds the memories relevant to a text. Candidates come from FTS5 (<c>memories_fts</c>, BM25) on the
/// application connection, always filtered by the session's warehouse and by <c>status = 'active'</c> with
/// constant, parameterized SQL (the AI connection is not used: its authorizer denies FTS tables on purpose).
/// The final score is computed in C# over the best <see cref="CandidateCount"/> rows:
/// <c>0.6 * bm25 (normalized to 0..1) + 0.25 * confidence + 0.35 when the scope reference appears in the query</c>.
/// Embeddings are a later extension (see <see cref="IEmbeddingProvider"/>).
/// </summary>
public sealed class MemoryRetriever
{
    public const int CandidateCount = 30;
    public const int MaxLimit = 10;
    private const double Bm25Weight = 0.6;
    private const double ConfidenceWeight = 0.25;
    private const double ScopeBonus = 0.35;

    private readonly NexusDatabase _database;
    private readonly PolicyEngine _policy;

    public MemoryRetriever(NexusDatabase database, PolicyEngine policy)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    /// <summary>
    /// The best active memories of the user's warehouse for <paramref name="query"/>, best first. An empty or
    /// unusable query returns an empty list. Requires <c>stock.read</c>.
    /// </summary>
    public IReadOnlyList<MemoryHit> Search(UserSession user, string query, MemorySearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        _policy.Require(user, Permissions.StockRead);
        options ??= new MemorySearchOptions();

        var match = FtsQuery.Build(query);
        if (match is null) return [];
        var limit = Math.Clamp(options.Limit, 1, MaxLimit);

        string? scopeFilter = options.Scope?.DbValue();
        string? refFilter = options.ScopeRef?.Trim();
        if (string.IsNullOrEmpty(refFilter)) refFilter = null;
        else if (options.Scope is MemoryScope.Sku or MemoryScope.Location or MemoryScope.Zone) refFilter = refFilter.ToUpperInvariant();

        var rows = new List<(MemoryHit Hit, double Relevance)>();
        try
        {
            using var connection = _database.OpenApplication();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT m.id, m.kind, m.scope, m.scope_ref, m.content, m.confidence, bm25(memories_fts) AS rank " +
                "FROM memories_fts JOIN memories m ON m.id = memories_fts.rowid " +
                "WHERE memories_fts MATCH $match AND m.warehouse_id = $w AND m.status = 'active' " +
                "AND m.confidence >= $min AND ($scope IS NULL OR m.scope = $scope) AND ($ref IS NULL OR m.scope_ref = $ref) " +
                "ORDER BY rank LIMIT $candidates";
            command.Parameters.AddWithValue("$match", match);
            command.Parameters.AddWithValue("$w", user.WarehouseId);
            command.Parameters.AddWithValue("$min", options.MinConfidence);
            command.Parameters.AddWithValue("$scope", (object?)scopeFilter ?? DBNull.Value);
            command.Parameters.AddWithValue("$ref", (object?)refFilter ?? DBNull.Value);
            command.Parameters.AddWithValue("$candidates", CandidateCount);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var hit = new MemoryHit(
                    reader.GetInt64(0), MemoryDb.KindFromDb(reader.GetString(1)), MemoryDb.ScopeFromDb(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetDouble(5), 0);
                rows.Add((hit, -reader.GetDouble(6)));
            }
        }
        catch (SqliteException ex)
        {
            // The expression is built from quoted terms, so this is not expected; a search must never break a chat turn.
            // Only the type and the SQLite code are logged: no SQL text and no user text.
            Trace.TraceWarning($"MemoryRetriever: search failed ({ex.GetType().Name}, code {ex.SqliteErrorCode}).");
            return [];
        }
        if (rows.Count == 0) return [];

        var maxRelevance = rows.Max(r => r.Relevance);
        var foldedQuery = TextFold.Fold(query);
        return rows
            .Select(r =>
            {
                var relevance = maxRelevance > 1e-9 ? Math.Max(0, r.Relevance) / maxRelevance : 0;
                var bonus = r.Hit.ScopeRef is { } reference && ContainsToken(foldedQuery, TextFold.Fold(reference)) ? ScopeBonus : 0;
                return r.Hit with { Score = Bm25Weight * relevance + ConfidenceWeight * r.Hit.Confidence + bonus };
            })
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Id)
            .Take(limit)
            .ToList();
    }

    /// <summary>True when <paramref name="token"/> occurs in <paramref name="text"/> delimited by non-alphanumeric characters.</summary>
    internal static bool ContainsToken(string text, string token)
    {
        if (token.Length == 0) return false;
        var start = 0;
        while ((start = text.IndexOf(token, start, StringComparison.Ordinal)) >= 0)
        {
            var end = start + token.Length;
            var beforeOk = start == 0 || !char.IsLetterOrDigit(text[start - 1]);
            var afterOk = end == text.Length || !char.IsLetterOrDigit(text[end]);
            if (beforeOk && afterOk) return true;
            start++;
        }
        return false;
    }
}
