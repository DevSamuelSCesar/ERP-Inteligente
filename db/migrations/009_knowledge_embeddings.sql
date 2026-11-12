-- Nexus migration 8 → 9 (vectors of the knowledge chunks for the hybrid RAG search; feature 5).
-- Applied by DatabaseBootstrapper inside one IMMEDIATE transaction to databases created by schema version 8.
-- New databases get the same result directly from schema.sql.

-- One vector per chunk: little-endian float32, the format sqlite-vec reads. A plain table, not a vec0 virtual table,
-- so the file stays readable without the extension; sqlite-vec computes the distance when it is loaded (same text as schema.sql).
CREATE TABLE knowledge_embeddings (
    chunk_id   INTEGER PRIMARY KEY REFERENCES knowledge_chunks (id) ON DELETE CASCADE,
    model      TEXT NOT NULL CHECK (length(model) BETWEEN 1 AND 120),
    dimensions INTEGER NOT NULL CHECK (dimensions BETWEEN 1 AND 4096),
    sha256     TEXT NOT NULL,
    embedding  BLOB NOT NULL CHECK (length(embedding) = 4 * dimensions),
    created_at TEXT NOT NULL
) STRICT;

-- A replaced or removed chunk drops its vector even on a connection without foreign keys (same text as schema.sql).
CREATE TRIGGER knowledge_embeddings_chunk_delete AFTER DELETE ON knowledge_chunks BEGIN
    DELETE FROM knowledge_embeddings WHERE chunk_id = old.id;
END;
