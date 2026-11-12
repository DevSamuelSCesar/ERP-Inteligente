using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Llm;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Core.Knowledge;

/// <summary>What <see cref="KnowledgeVectorIndex.SyncAsync"/> did; <see cref="Error"/> is the failure kind, if any.</summary>
public sealed record VectorSyncResult(int Embedded, int Pending, int Tokens, LlmErrorKind? Error);

/// <summary>How many chunks have a vector of the current model.</summary>
public sealed record VectorCoverage(string? Model, int WithVector, int Total, bool SqliteVec);

/// <summary>A chunk found by cosine similarity.</summary>
public sealed record VectorHit(long ChunkId, double Similarity);

/// <summary>
/// Vectors of the knowledge chunks (<c>knowledge_embeddings</c>, float32 little-endian, one per chunk) for the hybrid
/// search. Only chunks without a vector of the current model and content (SHA-256) are embedded, so a restart costs nothing
/// and a model switch embeds everything again. The distance is computed by <c>sqlite-vec</c> (<c>vec_distance_cosine</c>)
/// when the extension loads, otherwise in C# with the same result; the knowledge base is small (22 documents), so an exact
/// scan is enough and the file needs no <c>vec0</c> virtual table. Runs on the application connection only: the AI
/// connection never sees the vectors.
/// </summary>
public sealed class KnowledgeVectorIndex(NexusDatabase database, IEmbeddingProvider embeddings, TimeProvider? time = null)
{
    public const int Candidates = 20;
    private const int Batch = 32;

    private static int _vecState; // 0 unknown, 1 loads, -1 unavailable

    public IEmbeddingProvider Embeddings => embeddings;

    /// <summary>True once sqlite-vec loaded in this process; false when it could not (C# fallback).</summary>
    public static bool SqliteVecAvailable => Volatile.Read(ref _vecState) == 1;

    /// <summary>Embeds the chunks that lack a vector of the current model. Never throws for provider errors.</summary>
    public async Task<VectorSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var model = embeddings.Model;
        if (model is null) return new VectorSyncResult(0, 0, 0, null);
        var pending = Pending(model);
        int embedded = 0, tokens = 0;
        for (var start = 0; start < pending.Count; start += Batch)
        {
            var slice = pending.Skip(start).Take(Batch).ToList();
            EmbeddingBatch batch;
            try
            {
                batch = await embeddings.EmbedAsync(slice.Select(c => c.Text).ToList(), cancellationToken).ConfigureAwait(false);
            }
            catch (LlmException ex)
            {
                Trace.TraceWarning($"KnowledgeVectorIndex: embedding failed ({ex.Kind}).");
                return new VectorSyncResult(embedded, pending.Count - embedded, tokens, ex.Kind);
            }
            Store(model, slice, batch);
            embedded += slice.Count;
            tokens += batch.Tokens;
        }
        return new VectorSyncResult(embedded, 0, tokens, null);
    }

    public VectorCoverage Coverage()
    {
        var model = embeddings.Model;
        using var connection = database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT (SELECT count(*) FROM knowledge_chunks), " +
            "(SELECT count(*) FROM knowledge_embeddings e JOIN knowledge_chunks c ON c.id = e.chunk_id AND c.sha256 = e.sha256 WHERE e.model = $m)";
        command.Parameters.AddWithValue("$m", (object?)model ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        reader.Read();
        return new VectorCoverage(model, model is null ? 0 : reader.GetInt32(1), reader.GetInt32(0), SqliteVecAvailable);
    }

    /// <summary>The chunks closest to <paramref name="query"/> among the vectors of <paramref name="model"/>, best first.</summary>
    public IReadOnlyList<VectorHit> Nearest(float[] query, string model, int limit = Candidates)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length == 0) return [];
        using var connection = database.OpenApplication();
        if (TryLoadSqliteVec(connection))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT e.chunk_id, 1.0 - vec_distance_cosine(e.embedding, $q) AS similarity FROM knowledge_embeddings e " +
                "JOIN knowledge_chunks c ON c.id = e.chunk_id AND c.sha256 = e.sha256 " +
                "WHERE e.model = $m AND e.dimensions = $d ORDER BY similarity DESC LIMIT $limit";
            command.Parameters.AddWithValue("$q", ToBlob(query));
            command.Parameters.AddWithValue("$m", model);
            command.Parameters.AddWithValue("$d", query.Length);
            command.Parameters.AddWithValue("$limit", limit);
            var hits = new List<VectorHit>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                if (!reader.IsDBNull(1)) hits.Add(new VectorHit(reader.GetInt64(0), reader.GetDouble(1)));
            return hits;
        }

        using (var scan = connection.CreateCommand())
        {
            scan.CommandText =
                "SELECT e.chunk_id, e.embedding FROM knowledge_embeddings e JOIN knowledge_chunks c ON c.id = e.chunk_id AND c.sha256 = e.sha256 " +
                "WHERE e.model = $m AND e.dimensions = $d";
            scan.Parameters.AddWithValue("$m", model);
            scan.Parameters.AddWithValue("$d", query.Length);
            var all = new List<VectorHit>();
            using var reader = scan.ExecuteReader();
            while (reader.Read())
                all.Add(new VectorHit(reader.GetInt64(0), Cosine(query, FromBlob((byte[])reader.GetValue(1)))));
            return all.OrderByDescending(h => h.Similarity).ThenBy(h => h.ChunkId).Take(limit).ToList();
        }
    }

    /// <summary>
    /// Loads sqlite-vec into this connection. Microsoft.Data.Sqlite enables extension loading only for the call, so the
    /// <c>load_extension()</c> SQL function stays disabled afterwards.
    /// </summary>
    internal static bool TryLoadSqliteVec(SqliteConnection connection)
    {
        if (Volatile.Read(ref _vecState) == -1) return false;
        try
        {
            connection.LoadExtension("vec0");
            Volatile.Write(ref _vecState, 1);
            return true;
        }
        catch (Exception ex) when (ex is SqliteException or DllNotFoundException or InvalidOperationException)
        {
            Trace.TraceWarning($"KnowledgeVectorIndex: sqlite-vec unavailable ({ex.GetType().Name}); using the C# distance.");
            Volatile.Write(ref _vecState, -1);
            return false;
        }
    }

    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    public static byte[] ToBlob(float[] vector)
    {
        var bytes = new byte[vector.Length * 4];
        for (var i = 0; i < vector.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), vector[i]);
        return bytes;
    }

    public static float[] FromBlob(byte[] bytes)
    {
        var vector = new float[bytes.Length / 4];
        for (var i = 0; i < vector.Length; i++)
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4));
        return vector;
    }

    private sealed record PendingChunk(long Id, string Sha, string Text);

    private List<PendingChunk> Pending(string model)
    {
        using var connection = database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT c.id, c.sha256, c.heading, c.content FROM knowledge_chunks c " +
            "LEFT JOIN knowledge_embeddings e ON e.chunk_id = c.id AND e.model = $m AND e.sha256 = c.sha256 " +
            "WHERE e.chunk_id IS NULL ORDER BY c.id";
        command.Parameters.AddWithValue("$m", model);
        var list = new List<PendingChunk>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            list.Add(new PendingChunk(reader.GetInt64(0), reader.GetString(1), reader.GetString(2) + "\n" + reader.GetString(3)));
        return list;
    }

    private void Store(string model, IReadOnlyList<PendingChunk> chunks, EmbeddingBatch batch)
    {
        var now = AuditHasher.FormatTimestamp((time ?? TimeProvider.System).GetUtcNow());
        using var connection = database.OpenApplication();
        using var transaction = connection.BeginTransaction(deferred: false);
        for (var i = 0; i < chunks.Count; i++)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // The chunk may have been replaced meanwhile: only a row whose content still matches is written.
            command.CommandText =
                "INSERT OR REPLACE INTO knowledge_embeddings (chunk_id, model, dimensions, sha256, embedding, created_at) " +
                "SELECT id, $m, $d, sha256, $v, $at FROM knowledge_chunks WHERE id = $id AND sha256 = $sha";
            command.Parameters.AddWithValue("$m", model);
            command.Parameters.AddWithValue("$d", batch.Vectors[i].Length);
            command.Parameters.AddWithValue("$v", ToBlob(batch.Vectors[i]));
            command.Parameters.AddWithValue("$at", now);
            command.Parameters.AddWithValue("$id", chunks[i].Id);
            command.Parameters.AddWithValue("$sha", chunks[i].Sha);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
