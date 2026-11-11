-- ERP Inteligente: database-level security controls (apply after schema.sql).
-- SQLite has no GRANT/DENY or row-level security; these triggers and views are the in-database layer.
-- The application adds the rest: SQLCipher at rest, AES-GCM column encryption, the policy engine
-- (deny-overrides + warehouse row scope) and the SQLite authorizer callback on the AI connection.

-- ---------------------------------------------------------------------------
-- Immutability: ledger, audit and finished plans cannot be changed or erased
-- ---------------------------------------------------------------------------

CREATE TRIGGER audit_log_deny_update BEFORE UPDATE ON audit_log BEGIN
    SELECT RAISE(ABORT, 'DENY: audit_log is append-only');
END;

CREATE TRIGGER audit_log_deny_delete BEFORE DELETE ON audit_log BEGIN
    SELECT RAISE(ABORT, 'DENY: audit_log is append-only');
END;

CREATE TRIGGER stock_movements_deny_update BEFORE UPDATE ON stock_movements BEGIN
    SELECT RAISE(ABORT, 'DENY: stock_movements is append-only; use a compensating movement');
END;

CREATE TRIGGER stock_movements_deny_delete BEFORE DELETE ON stock_movements BEGIN
    SELECT RAISE(ABORT, 'DENY: stock_movements is append-only; use a compensating movement');
END;

CREATE TRIGGER order_reservations_deny_delete BEFORE DELETE ON order_reservations BEGIN
    SELECT RAISE(ABORT, 'DENY: order_reservations is append-only; release it instead');
END;

-- Only state, updated_plan_id and (when a pick moves the stock to a dock) balance_id may change, and only along
-- reserved -> picked -> shipped, or reserved/picked -> released. Released and shipped rows are final.
CREATE TRIGGER order_reservations_guard_update BEFORE UPDATE ON order_reservations
WHEN new.id <> old.id OR new.warehouse_id <> old.warehouse_id OR new.outbound_order_line_id <> old.outbound_order_line_id
  OR new.quantity <> old.quantity OR new.created_plan_id IS NOT old.created_plan_id OR new.created_at <> old.created_at
  OR (new.balance_id <> old.balance_id AND NOT (old.state = 'reserved' AND new.state = 'picked'))
  OR NOT ((old.state = 'reserved' AND new.state IN ('reserved', 'picked', 'released'))
       OR (old.state = 'picked'   AND new.state IN ('picked', 'shipped', 'released'))) BEGIN
    SELECT RAISE(ABORT, 'DENY: illegal reservation change');
END;

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

-- Memories are archived, never deleted. Identity, warehouse, origin and creation are fixed; content, scope,
-- confidence, use counters and status may change (migrations/006_memory.sql).
CREATE TRIGGER memories_deny_delete BEFORE DELETE ON memories BEGIN
    SELECT RAISE(ABORT, 'DENY: memories are archived, never deleted');
END;

CREATE TRIGGER memories_guard_update BEFORE UPDATE ON memories
WHEN new.id <> old.id OR new.warehouse_id <> old.warehouse_id OR new.origin <> old.origin
  OR new.created_by IS NOT old.created_by OR new.created_at <> old.created_at BEGIN
    SELECT RAISE(ABORT, 'DENY: illegal memory change');
END;

-- Recipes are demoted, never deleted; the template, its parameters and its origin are fixed (migrations/007_recipes.sql).
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

CREATE TRIGGER plans_deny_update_final BEFORE UPDATE ON plans
WHEN old.status IN ('executed', 'failed', 'rejected') BEGIN
    SELECT RAISE(ABORT, 'DENY: finished plans are immutable');
END;

CREATE TRIGGER plans_deny_delete BEFORE DELETE ON plans BEGIN
    SELECT RAISE(ABORT, 'DENY: plans are never deleted');
END;

-- ---------------------------------------------------------------------------
-- Privilege escalation guards for the AI role
-- ---------------------------------------------------------------------------

-- Permissions the AI role can never receive, whoever runs the INSERT. stock.adjust and stock.ship are not
-- here: since 2026-11-08 the AI has the operational rights of the user (migrations/003_full_catalog.sql).
-- skill.manage is here: the AI never saves its own instructions (migrations/005_skills.sql).
-- memory.manage is here too: the AI never edits or archives what it has learned (migrations/006_memory.sql).
CREATE TRIGGER role_permissions_deny_ai_escalation BEFORE INSERT ON role_permissions
WHEN new.effect = 'ALLOW'
 AND (SELECT is_ai FROM roles WHERE id = new.role_id) = 1
 AND new.permission IN ('plan.approve', 'user.manage', 'role.manage', 'sensitive.read',
                        'audit.read', 'skill.manage', 'memory.manage', 'recipe.manage') BEGIN
    SELECT RAISE(ABORT, 'DENY: permission cannot be granted to the AI role');
END;

-- DENY rows of the AI role cannot be removed or weakened.
CREATE TRIGGER role_permissions_deny_ai_unblock BEFORE DELETE ON role_permissions
WHEN old.effect = 'DENY' AND (SELECT is_ai FROM roles WHERE id = old.role_id) = 1 BEGIN
    SELECT RAISE(ABORT, 'DENY: AI role restrictions cannot be removed');
END;

CREATE TRIGGER role_permissions_deny_update BEFORE UPDATE ON role_permissions BEGIN
    SELECT RAISE(ABORT, 'DENY: delete and re-insert permissions instead of updating them');
END;

-- An AI service account cannot approve plans (segregation of duties).
CREATE TRIGGER plans_deny_ai_approval BEFORE UPDATE OF approved_by ON plans
WHEN new.approved_by IS NOT NULL
 AND (SELECT r.is_ai FROM users u JOIN roles r ON r.id = u.role_id WHERE u.id = new.approved_by) = 1 BEGIN
    SELECT RAISE(ABORT, 'DENY: the AI cannot approve plans');
END;

-- A requester cannot approve their own critical plan.
CREATE TRIGGER plans_deny_self_approval BEFORE UPDATE OF approved_by ON plans
WHEN new.approved_by IS NOT NULL AND new.approved_by = new.requested_by BEGIN
    SELECT RAISE(ABORT, 'DENY: a plan cannot be approved by its requester');
END;

-- The AI cannot reject plans either: rejecting is a human supervisor's decision.
CREATE TRIGGER plans_deny_ai_rejection BEFORE UPDATE OF rejected_by ON plans
WHEN new.rejected_by IS NOT NULL
 AND (SELECT r.is_ai FROM users u JOIN roles r ON r.id = u.role_id WHERE u.id = new.rejected_by) = 1 BEGIN
    SELECT RAISE(ABORT, 'DENY: the AI cannot reject plans');
END;

-- A requester cannot reject their own plan.
CREATE TRIGGER plans_deny_self_rejection BEFORE UPDATE OF rejected_by ON plans
WHEN new.rejected_by IS NOT NULL AND new.rejected_by = new.requested_by BEGIN
    SELECT RAISE(ABORT, 'DENY: a plan cannot be rejected by its requester');
END;

-- A plan is either approved or rejected, never both.
CREATE TRIGGER plans_deny_approve_and_reject BEFORE UPDATE ON plans
WHEN new.approved_by IS NOT NULL AND new.rejected_by IS NOT NULL BEGIN
    SELECT RAISE(ABORT, 'DENY: a plan cannot be both approved and rejected');
END;

-- ---------------------------------------------------------------------------
-- Effective permissions (deny-overrides)
-- ---------------------------------------------------------------------------

CREATE VIEW v_effective_permissions AS
SELECT a.role_id, a.permission
FROM role_permissions a
WHERE a.effect = 'ALLOW'
  AND NOT EXISTS (
      SELECT 1 FROM role_permissions d
      WHERE d.role_id = a.role_id AND d.permission = a.permission AND d.effect = 'DENY');

-- ---------------------------------------------------------------------------
-- Views exposed to the AI connection (no sensitive columns). The authorizer callback only
-- allows base-table reads when the innermost view is one of these v_ai_* views.
-- ---------------------------------------------------------------------------

CREATE VIEW v_ai_products AS
SELECT id, sku, name, description, unit, weight_kg, tracks_expiry
FROM products;

CREATE VIEW v_ai_locations AS
SELECT l.id, l.code, z.code AS zone_code, z.warehouse_id, l.capacity, l.is_active
FROM locations l
JOIN zones z ON z.id = l.zone_id;

CREATE VIEW v_ai_stock AS
SELECT b.warehouse_id,
       p.sku,
       p.name AS product_name,
       l.code AS location_code,
       z.code AS zone_code,
       lt.lot_code,
       lt.expires_on,
       b.quantity,
       b.reserved_qty,
       b.quantity - b.reserved_qty AS available_qty
FROM stock_balances b
JOIN products p ON p.id = b.product_id
JOIN locations l ON l.id = b.location_id
JOIN zones z ON z.id = l.zone_id
LEFT JOIN lots lt ON lt.id = b.lot_id;

CREATE VIEW v_ai_inbound_orders AS
SELECT o.id, o.warehouse_id, o.document_number, o.status, o.expected_on,
       s.name AS supplier_name, p.sku, ol.expected_qty, ol.received_qty
FROM inbound_orders o
JOIN suppliers s ON s.id = o.supplier_id
JOIN inbound_order_lines ol ON ol.inbound_order_id = o.id
JOIN products p ON p.id = ol.product_id;

CREATE VIEW v_ai_outbound_orders AS
SELECT o.id, o.warehouse_id, o.order_number, o.status, o.due_on,
       c.name AS customer_name, p.sku, ol.ordered_qty, ol.picked_qty
FROM outbound_orders o
JOIN customers c ON c.id = o.customer_id
JOIN outbound_order_lines ol ON ol.outbound_order_id = o.id
JOIN products p ON p.id = ol.product_id;

CREATE VIEW v_ai_recipes AS
SELECT id, name, description, intent, skill, plan_template_json, params_schema_json, status
FROM recipes
WHERE status = 'active';

CREATE VIEW v_ai_zones AS
SELECT id, warehouse_id, code, name, is_active
FROM zones;

CREATE VIEW v_ai_lots AS
SELECT p.sku, l.lot_code, l.expires_on
FROM lots l
JOIN products p ON p.id = l.product_id;

-- Suppliers and customers without their document, address or contact columns.
CREATE VIEW v_ai_partners AS
SELECT 'supplier' AS kind, id, name, is_active FROM suppliers
UNION ALL
SELECT 'customer', id, name, is_active FROM customers;

-- Active skills only (archived ones and the version history stay out of the AI's reach).
CREATE VIEW v_ai_skills AS
SELECT name, description, operations_json, body, version
FROM skills
WHERE status = 'active';

-- ---------------------------------------------------------------------------
-- Masked views for human roles without sensitive.read
-- ---------------------------------------------------------------------------

CREATE VIEW v_users_masked AS
SELECT u.id, u.username, u.display_name, r.name AS role, u.warehouse_id, u.is_active
FROM users u
JOIN roles r ON r.id = u.role_id;

CREATE VIEW v_suppliers_masked AS
SELECT id, name, cnpj_masked
FROM suppliers;

CREATE VIEW v_customers_masked AS
SELECT id, name, document_masked
FROM customers;
