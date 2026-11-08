-- Nexus migration 4 → 5 (skills: versioned instructions that the AI reads through v_ai_skills).
-- Applied by DatabaseBootstrapper inside one IMMEDIATE transaction to databases created by schema version 4.
-- New databases get the same result directly from schema.sql + security.sql + seed.sql.

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

-- Skills are archived, never deleted.
CREATE TRIGGER skills_deny_delete BEFORE DELETE ON skills BEGIN
    SELECT RAISE(ABORT, 'DENY: skills are archived, never deleted');
END;

-- Identity and creation are fixed, and the version never goes down. Content, status and the updater may change.
CREATE TRIGGER skills_guard_update BEFORE UPDATE ON skills
WHEN new.id <> old.id OR new.name <> old.name OR new.created_by <> old.created_by OR new.created_at <> old.created_at
  OR new.version < old.version BEGIN
    SELECT RAISE(ABORT, 'DENY: illegal skill change');
END;

-- Version history is append-only.
CREATE TRIGGER skill_versions_deny_update BEFORE UPDATE ON skill_versions BEGIN
    SELECT RAISE(ABORT, 'DENY: skill_versions is append-only');
END;

CREATE TRIGGER skill_versions_deny_delete BEFORE DELETE ON skill_versions BEGIN
    SELECT RAISE(ABORT, 'DENY: skill_versions is append-only');
END;

-- The AI never saves its own instructions: skill.manage joins the escalation list (same trigger as security.sql).
DROP TRIGGER role_permissions_deny_ai_escalation;
CREATE TRIGGER role_permissions_deny_ai_escalation BEFORE INSERT ON role_permissions
WHEN new.effect = 'ALLOW'
 AND (SELECT is_ai FROM roles WHERE id = new.role_id) = 1
 AND new.permission IN ('plan.approve', 'user.manage', 'role.manage', 'sensitive.read',
                        'audit.read', 'skill.manage') BEGIN
    SELECT RAISE(ABORT, 'DENY: permission cannot be granted to the AI role');
END;

-- skill.manage: supervisor only. Explicit DENY rows for the other roles, as in seed.sql.
INSERT INTO role_permissions (role_id, permission, effect) VALUES
    (2, 'skill.manage', 'ALLOW'),
    (1, 'skill.manage', 'DENY'),
    (3, 'skill.manage', 'DENY'),
    (4, 'skill.manage', 'DENY'),
    (5, 'skill.manage', 'DENY');

-- The AI reads only active skills, and only through this view (same definition as security.sql).
CREATE VIEW v_ai_skills AS
SELECT name, description, operations_json, body, version
FROM skills
WHERE status = 'active';
