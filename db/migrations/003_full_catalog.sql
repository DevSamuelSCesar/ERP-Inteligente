-- Nexus migration 2 → 3 (full catalog and the AI's operational rights).
-- Applied by DatabaseBootstrapper inside one IMMEDIATE transaction to databases created by schema version 2.
-- New databases get the same result directly from schema.sql + security.sql + seed.sql.

-- Zones, suppliers and customers can be deactivated without losing their history.
ALTER TABLE zones ADD COLUMN is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1));
ALTER TABLE suppliers ADD COLUMN is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1));
ALTER TABLE customers ADD COLUMN is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1));

-- Decision of 2026-11-08 (product owner): the AI role gets the same operational rights as a user, so it can
-- manage orders, locations, adjust stock and ship. The effective rights of an AI action are still the
-- intersection AI ∩ requester; every AI proposal needs the requester's confirmation, and critical plans still
-- need another supervisor. The AI still can never approve plans, read sensitive data, manage users or roles,
-- or read the audit log: those stay denied by role_permissions_deny_ai_escalation and by the AI DENY rows.

-- The escalation list loses stock.adjust and stock.ship (same trigger as security.sql).
DROP TRIGGER role_permissions_deny_ai_escalation;
CREATE TRIGGER role_permissions_deny_ai_escalation BEFORE INSERT ON role_permissions
WHEN new.effect = 'ALLOW'
 AND (SELECT is_ai FROM roles WHERE id = new.role_id) = 1
 AND new.permission IN ('plan.approve', 'user.manage', 'role.manage', 'sensitive.read',
                        'audit.read') BEGIN
    SELECT RAISE(ABORT, 'DENY: permission cannot be granted to the AI role');
END;

-- Removing the two AI DENY rows needs role_permissions_deny_ai_unblock out of the way. It is recreated right
-- after the DELETE, exactly as in security.sql, so the AI DENY rows that remain stay protected.
DROP TRIGGER role_permissions_deny_ai_unblock;
DELETE FROM role_permissions
 WHERE role_id = 5 AND effect = 'DENY' AND permission IN ('stock.adjust', 'stock.ship');
CREATE TRIGGER role_permissions_deny_ai_unblock BEFORE DELETE ON role_permissions
WHEN old.effect = 'DENY' AND (SELECT is_ai FROM roles WHERE id = old.role_id) = 1 BEGIN
    SELECT RAISE(ABORT, 'DENY: AI role restrictions cannot be removed');
END;

-- order.manage: operador, supervisor and the AI (which acts within the requester's rights).
-- location.manage, stock.adjust and stock.ship: the AI role, as allowed by the decision above.
INSERT INTO role_permissions (role_id, permission, effect) VALUES
    (1, 'order.manage', 'ALLOW'),
    (2, 'order.manage', 'ALLOW'),
    (5, 'order.manage', 'ALLOW'),
    (5, 'location.manage', 'ALLOW'),
    (5, 'stock.adjust', 'ALLOW'),
    (5, 'stock.ship', 'ALLOW');

-- Views for the AI connection (same definitions as security.sql): no sensitive columns.
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
