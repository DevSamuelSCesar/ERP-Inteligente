-- Nexus migration 5 → 6 (memory: what the AI learned per warehouse, archived and never deleted).
-- Applied by DatabaseBootstrapper inside one IMMEDIATE transaction to databases created by schema version 5.
-- New databases get the same result directly from schema.sql + security.sql + seed.sql.

-- SQLite cannot add a NOT NULL column with a foreign key to a populated table, so memories is rebuilt
-- (12-step procedure, keeping the ids). The FTS5 index is rebuilt from the new rows at the end.
CREATE TABLE memories_new (
    id             INTEGER PRIMARY KEY,
    warehouse_id   INTEGER NOT NULL REFERENCES warehouses (id),
    kind           TEXT NOT NULL CHECK (kind IN ('correction', 'preference', 'pattern', 'fact')),
    scope          TEXT NOT NULL CHECK (scope IN ('global', 'skill', 'sku', 'location', 'zone', 'supplier', 'customer')),
    scope_ref      TEXT CHECK (scope_ref IS NULL OR length(scope_ref) BETWEEN 1 AND 60),
    content        TEXT NOT NULL CHECK (length(content) BETWEEN 1 AND 1000),
    confidence     REAL NOT NULL DEFAULT 0.5 CHECK (confidence BETWEEN 0 AND 1),
    use_count      INTEGER NOT NULL DEFAULT 0 CHECK (use_count >= 0),
    status         TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'archived')),
    origin         TEXT NOT NULL CHECK (origin IN ('ai', 'system', 'user')),
    created_by     INTEGER REFERENCES users (id),
    source_plan_id INTEGER REFERENCES plans (id),
    created_at     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    updated_at     TEXT,
    last_used_at   TEXT
) STRICT;

-- Existing memories belong to the only warehouse (CD-01), were written by the user (sup.maria, id 2; NULL if that user does not exist) and are active.
INSERT INTO memories_new (id, warehouse_id, kind, scope, scope_ref, content, confidence, use_count, status, origin,
                          created_by, source_plan_id, created_at, updated_at, last_used_at)
SELECT id, 1, kind, scope, scope_ref, content, confidence, use_count, 'active', 'user',
       (SELECT u.id FROM users u WHERE u.id = 2), source_plan_id, created_at, NULL, last_used_at
FROM memories;

-- The FTS triggers belong to the old table: they are dropped with it and recreated below (same text as schema.sql).
DROP TABLE memories;
ALTER TABLE memories_new RENAME TO memories;

CREATE INDEX memories_by_warehouse ON memories (warehouse_id, status);

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

INSERT INTO memories_fts (memories_fts) VALUES ('rebuild');

-- Memories are archived, never deleted. Identity, warehouse, origin and creation are fixed (same text as security.sql).
CREATE TRIGGER memories_deny_delete BEFORE DELETE ON memories BEGIN
    SELECT RAISE(ABORT, 'DENY: memories are archived, never deleted');
END;

CREATE TRIGGER memories_guard_update BEFORE UPDATE ON memories
WHEN new.id <> old.id OR new.warehouse_id <> old.warehouse_id OR new.origin <> old.origin
  OR new.created_by IS NOT old.created_by OR new.created_at <> old.created_at BEGIN
    SELECT RAISE(ABORT, 'DENY: illegal memory change');
END;

-- The AI never edits or archives what it has learned: memory.manage joins the escalation list (same trigger as security.sql).
DROP TRIGGER role_permissions_deny_ai_escalation;
CREATE TRIGGER role_permissions_deny_ai_escalation BEFORE INSERT ON role_permissions
WHEN new.effect = 'ALLOW'
 AND (SELECT is_ai FROM roles WHERE id = new.role_id) = 1
 AND new.permission IN ('plan.approve', 'user.manage', 'role.manage', 'sensitive.read',
                        'audit.read', 'skill.manage', 'memory.manage') BEGIN
    SELECT RAISE(ABORT, 'DENY: permission cannot be granted to the AI role');
END;

-- memory.manage: supervisor only. Explicit DENY rows for the other roles, as in seed.sql.
-- memory.write (who may propose memories) is unchanged.
INSERT INTO role_permissions (role_id, permission, effect) VALUES
    (2, 'memory.manage', 'ALLOW'),
    (1, 'memory.manage', 'DENY'),
    (3, 'memory.manage', 'DENY'),
    (4, 'memory.manage', 'DENY'),
    (5, 'memory.manage', 'DENY');

-- No view exposes memories or knowledge to the AI connection: retrieval runs on the application connection
-- with constant, parameterised SQL (the authorizer of the AI connection denies these tables).
