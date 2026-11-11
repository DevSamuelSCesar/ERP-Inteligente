-- Nexus migration 7 → 8 (token usage of every LLM request, for the cost panel; feature 9).
-- Applied by DatabaseBootstrapper inside one IMMEDIATE transaction to databases created by schema version 7.
-- New databases get the same result directly from schema.sql + security.sql.

-- One row per request and role: tokens summed over the calls of that request, never the text (same text as schema.sql).
CREATE TABLE llm_usage (
    id                INTEGER PRIMARY KEY,
    occurred_at       TEXT NOT NULL,
    user_id           INTEGER REFERENCES users (id),
    warehouse_id      INTEGER REFERENCES warehouses (id),
    role              TEXT NOT NULL CHECK (role IN ('planner', 'matcher')),
    provider          TEXT NOT NULL CHECK (provider IN ('deepseek', 'claude', 'openai', 'local')),
    model             TEXT NOT NULL CHECK (length(model) BETWEEN 1 AND 80),
    calls             INTEGER NOT NULL CHECK (calls >= 0),
    prompt_tokens     INTEGER NOT NULL CHECK (prompt_tokens >= 0),
    completion_tokens INTEGER NOT NULL CHECK (completion_tokens >= 0),
    result            TEXT NOT NULL CHECK (result IN ('answer', 'proposals', 'recipe', 'no_match', 'escalated', 'failed'))
) STRICT;

CREATE INDEX llm_usage_by_time ON llm_usage (occurred_at);

-- Usage is accounting: append-only, like the audit log (same text as security.sql).
CREATE TRIGGER llm_usage_deny_update BEFORE UPDATE ON llm_usage BEGIN
    SELECT RAISE(ABORT, 'DENY: llm_usage is append-only');
END;

CREATE TRIGGER llm_usage_deny_delete BEFORE DELETE ON llm_usage BEGIN
    SELECT RAISE(ABORT, 'DENY: llm_usage is append-only');
END;
