-- Nexus migration 9 → 10 (prompt-cache tokens in llm_usage, so the cost panel prices cached input correctly).
-- Applied by DatabaseBootstrapper inside one IMMEDIATE transaction to databases created by schema version 9.
-- New databases get the same columns directly from schema.sql. Earlier rows keep 0: they were recorded before caching.

ALTER TABLE llm_usage ADD COLUMN cache_read_tokens INTEGER NOT NULL DEFAULT 0 CHECK (cache_read_tokens >= 0);
ALTER TABLE llm_usage ADD COLUMN cache_write_tokens INTEGER NOT NULL DEFAULT 0 CHECK (cache_write_tokens >= 0);
