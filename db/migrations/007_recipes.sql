-- Nexus migration 6 → 7 (recipes: plans that worked, parameterized, promoted by a supervisor; feature 7).
-- Applied by DatabaseBootstrapper inside one IMMEDIATE transaction to databases created by schema version 6.
-- New databases get the same result directly from schema.sql + security.sql + seed.sql.

-- New columns, appended in the same order as in schema.sql (ADD COLUMN accepts only constant defaults).
ALTER TABLE recipes ADD COLUMN skill TEXT;
ALTER TABLE recipes ADD COLUMN signature TEXT;
ALTER TABLE recipes ADD COLUMN example_params_json TEXT CHECK (example_params_json IS NULL OR json_valid(example_params_json));
ALTER TABLE recipes ADD COLUMN failure_streak INTEGER NOT NULL DEFAULT 0 CHECK (failure_streak >= 0);
ALTER TABLE recipes ADD COLUMN source_plan_id INTEGER REFERENCES plans (id);
ALTER TABLE recipes ADD COLUMN promoted_by INTEGER REFERENCES users (id);
ALTER TABLE recipes ADD COLUMN created_at TEXT;
ALTER TABLE recipes ADD COLUMN promoted_at TEXT;
ALTER TABLE recipes ADD COLUMN last_used_at TEXT;
ALTER TABLE recipes ADD COLUMN status_reason TEXT CHECK (status_reason IS NULL OR length(status_reason) BETWEEN 1 AND 300);

-- One recipe per plan structure (same text as schema.sql).
CREATE UNIQUE INDEX recipes_by_signature ON recipes (signature) WHERE signature IS NOT NULL;

-- The seeded recipe (id 1) gets the values a fresh install has (same values as seed.sql).
UPDATE recipes
SET skill = 'armazenagem',
    signature = 'move_stock(from,qty,sku,to)',
    example_params_json = '{"sku":"ABC-123","qty":40,"dock":"DOCA-01","target":"A-01-01"}',
    created_at = '2026-10-20T10:05:00Z'
WHERE id = 1 AND name = 'armazenar_da_doca';

-- Recipes are demoted, never deleted; the template, its parameters and its origin are fixed (same text as security.sql).
CREATE TRIGGER recipes_deny_delete BEFORE DELETE ON recipes BEGIN
    SELECT RAISE(ABORT, 'DENY: recipes are demoted, never deleted');
END;

CREATE TRIGGER recipes_guard_update BEFORE UPDATE ON recipes
WHEN new.id <> old.id OR new.plan_template_json <> old.plan_template_json
  OR new.params_schema_json <> old.params_schema_json OR new.intent <> old.intent
  OR (old.signature IS NOT NULL AND new.signature IS NOT old.signature)
  OR new.source_plan_id IS NOT old.source_plan_id
  OR (old.created_at IS NOT NULL AND new.created_at IS NOT old.created_at) BEGIN
    SELECT RAISE(ABORT, 'DENY: illegal recipe change');
END;

-- The AI never promotes, suspends or demotes recipes: recipe.manage joins the escalation list (same trigger as security.sql).
DROP TRIGGER role_permissions_deny_ai_escalation;
CREATE TRIGGER role_permissions_deny_ai_escalation BEFORE INSERT ON role_permissions
WHEN new.effect = 'ALLOW'
 AND (SELECT is_ai FROM roles WHERE id = new.role_id) = 1
 AND new.permission IN ('plan.approve', 'user.manage', 'role.manage', 'sensitive.read',
                        'audit.read', 'skill.manage', 'memory.manage', 'recipe.manage') BEGIN
    SELECT RAISE(ABORT, 'DENY: permission cannot be granted to the AI role');
END;

-- The AI reads active recipes with their template, to use one as the base of a plan (search_recipes).
DROP VIEW v_ai_recipes;
CREATE VIEW v_ai_recipes AS
SELECT id, name, description, intent, skill, plan_template_json, params_schema_json, status
FROM recipes
WHERE status = 'active';
