using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using erp_inteligente.Core.Data;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Core.Knowledge;

/// <summary>What <see cref="KnowledgeIndexer.Sync"/> changed.</summary>
public sealed record IndexResult(int Inserted, int Replaced, int Deleted, int Unchanged)
{
    public bool Changed => Inserted + Replaced + Deleted > 0;
}

/// <summary>
/// Keeps <c>knowledge_chunks</c> (and, through triggers, <c>knowledge_fts</c>) equal to the Markdown documents
/// embedded in the app (<c>knowledge/*.md</c>). Idempotent: unchanged chunks (same SHA-256) are not touched.
/// Skills are not indexed; they are loaded on demand by <c>load_skill</c>.
/// </summary>
public static class KnowledgeIndexer
{
    private const string ResourcePrefix = "Nexus.Knowledge.";

    /// <summary>The documents embedded in the assembly, sorted by path.</summary>
    public static IReadOnlyList<KnowledgeSource> EmbeddedSources()
    {
        var assembly = typeof(KnowledgeIndexer).Assembly;
        var sources = new List<KnowledgeSource>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded resource '{name}' could not be opened.");
            using var reader = new StreamReader(stream);
            sources.Add(new KnowledgeSource("knowledge/" + name[ResourcePrefix.Length..], reader.ReadToEnd()));
        }
        return sources.OrderBy(s => s.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>Synchronizes with the embedded documents.</summary>
    public static IndexResult Sync(NexusDatabase database) => Sync(database, EmbeddedSources());

    /// <summary>Synchronizes with <paramref name="sources"/> in one transaction (tests pass their own).</summary>
    public static IndexResult Sync(NexusDatabase database, IReadOnlyList<KnowledgeSource> sources)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(sources);

        var wanted = new Dictionary<(string Path, string Heading), KnowledgeChunk>();
        foreach (var source in sources)
            foreach (var chunk in KnowledgeChunker.Chunk(source))
                wanted[(chunk.SourcePath, chunk.Heading)] = chunk;

        using var connection = database.OpenApplication();
        using var transaction = connection.BeginTransaction(deferred: false);

        var existing = new Dictionary<(string Path, string Heading), (long Id, string Sha)>();
        using (var select = Cmd(connection, transaction, "SELECT id, source_path, heading, sha256 FROM knowledge_chunks"))
        using (var reader = select.ExecuteReader())
            while (reader.Read())
                existing[(reader.GetString(1), reader.GetString(2))] = (reader.GetInt64(0), reader.GetString(3));

        int inserted = 0, replaced = 0, deleted = 0, unchanged = 0;
        foreach (var (key, row) in existing)
        {
            if (wanted.TryGetValue(key, out var chunk) && chunk.Sha256 == row.Sha) { unchanged++; continue; }
            Delete(connection, transaction, row.Id);
            if (wanted.ContainsKey(key)) replaced++; else deleted++;
        }
        foreach (var (key, chunk) in wanted)
        {
            if (existing.TryGetValue(key, out var row) && row.Sha == chunk.Sha256) continue;
            Insert(connection, transaction, chunk);
            if (!existing.ContainsKey(key)) inserted++;
        }

        transaction.Commit();
        return new IndexResult(inserted, replaced, deleted, unchanged);
    }

    private static void Delete(SqliteConnection connection, SqliteTransaction transaction, long id)
    {
        using var command = Cmd(connection, transaction, "DELETE FROM knowledge_chunks WHERE id = $id");
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, KnowledgeChunk chunk)
    {
        using var command = Cmd(connection, transaction,
            "INSERT INTO knowledge_chunks (source_path, heading, content, token_count, sha256) " +
            "VALUES ($path, $heading, $content, $tokens, $sha)");
        command.Parameters.AddWithValue("$path", chunk.SourcePath);
        command.Parameters.AddWithValue("$heading", chunk.Heading);
        command.Parameters.AddWithValue("$content", chunk.Content);
        command.Parameters.AddWithValue("$tokens", chunk.TokenCount);
        command.Parameters.AddWithValue("$sha", chunk.Sha256);
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Cmd(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}
