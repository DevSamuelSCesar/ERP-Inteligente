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

-- Permissions the AI role can never receive, whoever runs the INSERT.
CREATE TRIGGER role_permissions_deny_ai_escalation BEFORE INSERT ON role_permissions
WHEN new.effect = 'ALLOW'
 AND (SELECT is_ai FROM roles WHERE id = new.role_id) = 1
 AND new.permission IN ('plan.approve', 'stock.adjust', 'stock.ship', 'user.manage',
                        'role.manage', 'sensitive.read', 'audit.read') BEGIN
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
SELECT id, name, description, intent, params_schema_json, status
FROM recipes
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
