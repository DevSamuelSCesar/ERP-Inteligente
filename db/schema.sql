-- ERP Inteligente: warehouse (WMS) schema for SQLite.
-- Sensitive columns (suffix _enc) hold AES-GCM ciphertext produced by the application;
-- they are never written in plaintext. The whole file is additionally encrypted at rest with SQLCipher.
-- Performance indexes for the benchmark queries live in optimize.sql (applied after the "before" measurement).

PRAGMA foreign_keys = ON;

-- ---------------------------------------------------------------------------
-- Access control
-- ---------------------------------------------------------------------------

CREATE TABLE roles (
    id          INTEGER PRIMARY KEY,
    name        TEXT NOT NULL UNIQUE,
    description TEXT NOT NULL,
    is_ai       INTEGER NOT NULL DEFAULT 0 CHECK (is_ai IN (0, 1))
) STRICT;

-- Deny-overrides: a DENY row for (role, permission) always wins over any ALLOW.
CREATE TABLE role_permissions (
    role_id    INTEGER NOT NULL REFERENCES roles (id),
    permission TEXT NOT NULL,
    effect     TEXT NOT NULL CHECK (effect IN ('ALLOW', 'DENY')),
    PRIMARY KEY (role_id, permission, effect)
) STRICT;

CREATE TABLE warehouses (
    id   INTEGER PRIMARY KEY,
    code TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL
) STRICT;

CREATE TABLE users (
    id            INTEGER PRIMARY KEY,
    username      TEXT NOT NULL UNIQUE,
    display_name  TEXT NOT NULL,
    full_name_enc BLOB,
    cpf_enc       BLOB,
    email_enc     BLOB,
    password_hash TEXT NOT NULL,
    role_id       INTEGER NOT NULL REFERENCES roles (id),
    warehouse_id  INTEGER REFERENCES warehouses (id),
    is_active     INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    -- Login lockout, enforced by the application: 5 consecutive failures lock the user for 15 minutes.
    failed_attempts INTEGER NOT NULL DEFAULT 0 CHECK (failed_attempts >= 0),  -- consecutive failed logins
    locked_until    TEXT,  -- UTC 'YYYY-MM-DDTHH:MM:SSZ' until which login is refused; NULL when not locked
    last_login_at   TEXT,  -- UTC timestamp of the last successful login
    created_at    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now'))
) STRICT;

-- ---------------------------------------------------------------------------
-- Warehouse domain
-- ---------------------------------------------------------------------------

CREATE TABLE zones (
    id           INTEGER PRIMARY KEY,
    warehouse_id INTEGER NOT NULL REFERENCES warehouses (id),
    code         TEXT NOT NULL,
    name         TEXT NOT NULL,
    is_active    INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    UNIQUE (warehouse_id, code)
) STRICT;

CREATE TABLE locations (
    id        INTEGER PRIMARY KEY,
    zone_id   INTEGER NOT NULL REFERENCES zones (id),
    code      TEXT NOT NULL UNIQUE,
    capacity  INTEGER NOT NULL CHECK (capacity > 0),
    is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1))
) STRICT;

CREATE TABLE products (
    id            INTEGER PRIMARY KEY,
    sku           TEXT NOT NULL UNIQUE,
    name          TEXT NOT NULL,
    description   TEXT,
    unit          TEXT NOT NULL,
    weight_kg     REAL CHECK (weight_kg >= 0),
    tracks_expiry INTEGER NOT NULL DEFAULT 0 CHECK (tracks_expiry IN (0, 1)),
    unit_cost_enc BLOB
) STRICT;

CREATE TABLE lots (
    id         INTEGER PRIMARY KEY,
    product_id INTEGER NOT NULL REFERENCES products (id),
    lot_code   TEXT NOT NULL,
    expires_on TEXT,
    UNIQUE (product_id, lot_code)
) STRICT;

CREATE TABLE stock_balances (
    id           INTEGER PRIMARY KEY,
    warehouse_id INTEGER NOT NULL REFERENCES warehouses (id),
    product_id   INTEGER NOT NULL REFERENCES products (id),
    location_id  INTEGER NOT NULL REFERENCES locations (id),
    lot_id       INTEGER REFERENCES lots (id),
    quantity     INTEGER NOT NULL CHECK (quantity >= 0),
    reserved_qty INTEGER NOT NULL DEFAULT 0 CHECK (reserved_qty >= 0 AND reserved_qty <= quantity),
    updated_at   TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    UNIQUE (product_id, location_id, lot_id)
) STRICT;

CREATE TABLE suppliers (
    id                INTEGER PRIMARY KEY,
    name              TEXT NOT NULL,
    cnpj_masked       TEXT,
    cnpj_enc          BLOB,
    contact_name_enc  BLOB,
    contact_email_enc BLOB,
    phone_enc         BLOB,
    is_active         INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1))
) STRICT;

CREATE TABLE customers (
    id                INTEGER PRIMARY KEY,
    name              TEXT NOT NULL,
    document_masked   TEXT,
    document_enc      BLOB,
    address_enc       BLOB,
    contact_email_enc BLOB,
    is_active         INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1))
) STRICT;

CREATE TABLE inbound_orders (
    id              INTEGER PRIMARY KEY,
    warehouse_id    INTEGER NOT NULL REFERENCES warehouses (id),
    supplier_id     INTEGER NOT NULL REFERENCES suppliers (id),
    document_number TEXT NOT NULL,
    status          TEXT NOT NULL CHECK (status IN ('expected', 'receiving', 'received', 'cancelled')),
    expected_on     TEXT
) STRICT;

CREATE TABLE inbound_order_lines (
    id               INTEGER PRIMARY KEY,
    inbound_order_id INTEGER NOT NULL REFERENCES inbound_orders (id),
    product_id       INTEGER NOT NULL REFERENCES products (id),
    expected_qty     INTEGER NOT NULL CHECK (expected_qty > 0),
    received_qty     INTEGER NOT NULL DEFAULT 0 CHECK (received_qty >= 0)
) STRICT;

CREATE TABLE outbound_orders (
    id           INTEGER PRIMARY KEY,
    warehouse_id INTEGER NOT NULL REFERENCES warehouses (id),
    customer_id  INTEGER NOT NULL REFERENCES customers (id),
    order_number TEXT NOT NULL UNIQUE,
    status       TEXT NOT NULL CHECK (status IN ('open', 'reserved', 'picking', 'shipped', 'cancelled')),
    due_on       TEXT
) STRICT;

CREATE TABLE outbound_order_lines (
    id                INTEGER PRIMARY KEY,
    outbound_order_id INTEGER NOT NULL REFERENCES outbound_orders (id),
    product_id        INTEGER NOT NULL REFERENCES products (id),
    ordered_qty       INTEGER NOT NULL CHECK (ordered_qty > 0),
    picked_qty        INTEGER NOT NULL DEFAULT 0 CHECK (picked_qty >= 0)
) STRICT;

-- ---------------------------------------------------------------------------
-- Plans, recipes and the movement ledger
-- ---------------------------------------------------------------------------

CREATE TABLE recipes (
    id                 INTEGER PRIMARY KEY,
    name               TEXT NOT NULL UNIQUE,
    description        TEXT NOT NULL,
    intent             TEXT NOT NULL,
    plan_template_json TEXT NOT NULL CHECK (json_valid(plan_template_json)),
    params_schema_json TEXT NOT NULL CHECK (json_valid(params_schema_json)),
    dsl_version        TEXT NOT NULL,
    skill_version      TEXT NOT NULL,
    status             TEXT NOT NULL CHECK (status IN ('candidate', 'active', 'suspended', 'demoted')),
    success_count      INTEGER NOT NULL DEFAULT 0,
    failure_count      INTEGER NOT NULL DEFAULT 0
) STRICT;

CREATE TABLE plans (
    id                  INTEGER PRIMARY KEY,
    requested_by        INTEGER NOT NULL REFERENCES users (id),
    planned_by          TEXT NOT NULL CHECK (planned_by IN ('ai', 'recipe', 'user')),
    recipe_id           INTEGER REFERENCES recipes (id),
    skill               TEXT,
    intent              TEXT NOT NULL,
    plan_json           TEXT NOT NULL CHECK (json_valid(plan_json)),
    dsl_version         TEXT NOT NULL,
    risk                TEXT NOT NULL CHECK (risk IN ('read', 'reversible', 'critical')),
    status              TEXT NOT NULL CHECK (status IN ('validating', 'pending_approval', 'rejected', 'executed', 'failed')),
    simulated_diff_json TEXT CHECK (simulated_diff_json IS NULL OR json_valid(simulated_diff_json)),
    attempts            INTEGER NOT NULL DEFAULT 1,
    approved_by         INTEGER REFERENCES users (id),
    rejected_by         INTEGER REFERENCES users (id),  -- supervisor who rejected the plan
    rejection_reason    TEXT CHECK (rejection_reason IS NULL OR length(rejection_reason) BETWEEN 1 AND 200),
    failure_reason      TEXT,  -- business-language reason when validation or execution failed
    created_at          TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    executed_at         TEXT,
    CHECK (status <> 'rejected' OR (rejected_by IS NOT NULL AND rejection_reason IS NOT NULL))
) STRICT;

-- Append-only ledger: every stock change is one row; balances are kept in sync by the Executor.
CREATE TABLE stock_movements (
    id               INTEGER PRIMARY KEY,
    warehouse_id     INTEGER NOT NULL REFERENCES warehouses (id),
    product_id       INTEGER NOT NULL REFERENCES products (id),
    lot_id           INTEGER REFERENCES lots (id),
    from_location_id INTEGER REFERENCES locations (id),
    to_location_id   INTEGER REFERENCES locations (id),
    kind             TEXT NOT NULL CHECK (kind IN ('receive', 'move', 'pick', 'ship', 'adjust')),
    quantity         INTEGER NOT NULL CHECK (quantity <> 0),
    reason           TEXT,
    plan_id          INTEGER REFERENCES plans (id),
    user_id          INTEGER REFERENCES users (id),
    created_at       TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    CHECK (kind <> 'adjust' OR reason IS NOT NULL)
) STRICT;

-- Which order holds how much of which balance. stock_balances.reserved_qty is the sum of the open rows
-- (state 'reserved' or 'picked') of its balance. quantity never changes: a partial operation releases the row and
-- inserts new ones. Transitions are guarded by triggers in security.sql.
CREATE TABLE order_reservations (
    id                     INTEGER PRIMARY KEY,
    warehouse_id           INTEGER NOT NULL REFERENCES warehouses (id),
    outbound_order_line_id INTEGER NOT NULL REFERENCES outbound_order_lines (id),
    balance_id             INTEGER NOT NULL REFERENCES stock_balances (id),
    quantity               INTEGER NOT NULL CHECK (quantity > 0),
    state                  TEXT NOT NULL DEFAULT 'reserved' CHECK (state IN ('reserved', 'picked', 'shipped', 'released')),
    created_plan_id        INTEGER REFERENCES plans (id),
    updated_plan_id        INTEGER REFERENCES plans (id),
    created_at             TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now'))
) STRICT;

CREATE INDEX idx_order_reservations_line    ON order_reservations (outbound_order_line_id, state);
CREATE INDEX idx_order_reservations_balance ON order_reservations (balance_id, state);

-- ---------------------------------------------------------------------------
-- Skills: versioned instructions saved by a supervisor (origin says whether a person or the AI drafted them).
-- The AI reads only the active ones through v_ai_skills. Guards are in security.sql.
-- ---------------------------------------------------------------------------

CREATE TABLE skills (
    id              INTEGER PRIMARY KEY,
    name            TEXT NOT NULL UNIQUE CHECK (length(name) BETWEEN 2 AND 40 AND name GLOB '[a-z]*' AND name NOT GLOB '*[^a-z_]*'),
    description     TEXT NOT NULL CHECK (length(description) BETWEEN 20 AND 300),
    operations_json TEXT NOT NULL CHECK (json_valid(operations_json) AND json_type(operations_json) = 'array'),
    body            TEXT NOT NULL CHECK (length(body) BETWEEN 1 AND 12000),
    version         INTEGER NOT NULL CHECK (version >= 1),
    status          TEXT NOT NULL CHECK (status IN ('active', 'archived')),
    origin          TEXT NOT NULL CHECK (origin IN ('user', 'ai')),
    created_by      INTEGER NOT NULL REFERENCES users (id),
    created_at      TEXT NOT NULL,
    updated_by      INTEGER NOT NULL REFERENCES users (id),
    updated_at      TEXT NOT NULL
) STRICT;

-- One row per saved version of a skill (the current content lives in skills). Append-only.
CREATE TABLE skill_versions (
    id              INTEGER PRIMARY KEY,
    skill_id        INTEGER NOT NULL REFERENCES skills (id),
    version         INTEGER NOT NULL CHECK (version >= 1),
    description     TEXT NOT NULL,
    operations_json TEXT NOT NULL CHECK (json_valid(operations_json)),
    body            TEXT NOT NULL,
    origin          TEXT NOT NULL CHECK (origin IN ('user', 'ai')),
    saved_by        INTEGER NOT NULL REFERENCES users (id),
    saved_at        TEXT NOT NULL,
    UNIQUE (skill_id, version)
) STRICT;

-- ---------------------------------------------------------------------------
-- AI memory and knowledge base (RAG)
-- ---------------------------------------------------------------------------

CREATE TABLE memories (
    id             INTEGER PRIMARY KEY,
    kind           TEXT NOT NULL CHECK (kind IN ('correction', 'preference', 'pattern', 'fact')),
    scope          TEXT NOT NULL,
    scope_ref      TEXT,
    content        TEXT NOT NULL CHECK (length(content) <= 1000),
    confidence     REAL NOT NULL DEFAULT 0.5 CHECK (confidence BETWEEN 0 AND 1),
    use_count      INTEGER NOT NULL DEFAULT 0,
    source_plan_id INTEGER REFERENCES plans (id),
    created_at     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    last_used_at   TEXT
) STRICT;

CREATE VIRTUAL TABLE memories_fts USING fts5 (
    content,
    content = 'memories',
    content_rowid = 'id',
    tokenize = 'unicode61 remove_diacritics 2'
);

CREATE TRIGGER memories_fts_insert AFTER INSERT ON memories BEGIN
    INSERT INTO memories_fts (rowid, content) VALUES (new.id, new.content);
END;

CREATE TRIGGER memories_fts_delete AFTER DELETE ON memories BEGIN
    INSERT INTO memories_fts (memories_fts, rowid, content) VALUES ('delete', old.id, old.content);
END;

CREATE TRIGGER memories_fts_update AFTER UPDATE OF content ON memories BEGIN
    INSERT INTO memories_fts (memories_fts, rowid, content) VALUES ('delete', old.id, old.content);
    INSERT INTO memories_fts (rowid, content) VALUES (new.id, new.content);
END;

-- Chunks of knowledge/*.md and skills/*/SKILL.md. Embeddings live in a sqlite-vec
-- virtual table created by the application (the extension is not available in the plain sqlite3 CLI):
--   CREATE VIRTUAL TABLE knowledge_vec USING vec0(chunk_id INTEGER PRIMARY KEY, embedding FLOAT[1536]);
CREATE TABLE knowledge_chunks (
    id          INTEGER PRIMARY KEY,
    source_path TEXT NOT NULL,
    heading     TEXT NOT NULL,
    content     TEXT NOT NULL,
    token_count INTEGER NOT NULL,
    sha256      TEXT NOT NULL,
    UNIQUE (source_path, heading)
) STRICT;

CREATE VIRTUAL TABLE knowledge_fts USING fts5 (
    heading,
    content,
    content = 'knowledge_chunks',
    content_rowid = 'id',
    tokenize = 'unicode61 remove_diacritics 2'
);

CREATE TRIGGER knowledge_fts_insert AFTER INSERT ON knowledge_chunks BEGIN
    INSERT INTO knowledge_fts (rowid, heading, content) VALUES (new.id, new.heading, new.content);
END;

CREATE TRIGGER knowledge_fts_delete AFTER DELETE ON knowledge_chunks BEGIN
    INSERT INTO knowledge_fts (knowledge_fts, rowid, heading, content) VALUES ('delete', old.id, old.heading, old.content);
END;

-- ---------------------------------------------------------------------------
-- Audit (append-only, hash-chained by the application with rule v1: see audit_hash_v1 in db/tests/test_db.py)
-- ---------------------------------------------------------------------------

CREATE TABLE audit_log (
    id           INTEGER PRIMARY KEY,
    occurred_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    actor_type   TEXT NOT NULL CHECK (actor_type IN ('user', 'ai', 'system')),
    actor_id     INTEGER REFERENCES users (id),
    on_behalf_of INTEGER REFERENCES users (id),
    action       TEXT NOT NULL,
    target       TEXT,
    outcome      TEXT NOT NULL CHECK (outcome IN ('allowed', 'denied', 'failed')),
    details_json TEXT CHECK (details_json IS NULL OR json_valid(details_json)),
    prev_hash    TEXT NOT NULL,
    hash         TEXT NOT NULL UNIQUE
) STRICT;
