-- Nexus migration 3 → 4 (per-order stock reservations).
-- Applied by DatabaseBootstrapper inside one IMMEDIATE transaction to databases created by schema version 3.
-- New databases get the same result directly from schema.sql + security.sql + seed.sql.

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

-- Backfill of reservations made before this table existed: an order in status 'reserved' (nothing picked) gets one
-- 'reserved' row per balance holding reserved stock of its products, but ONLY when every line is unambiguous: the
-- warehouse's reserved_qty for the line's product equals the line's ordered_qty and no other reserved/picking order
-- of the warehouse has a line for that product. Any other order stays legacy (reserved_qty without rows); releasing
-- or cancelling it then leaves that quantity reserved until someone corrects it. Audit rows are not touched.
INSERT INTO order_reservations (warehouse_id, outbound_order_line_id, balance_id, quantity, state)
SELECT o.warehouse_id, l.id, b.id, b.reserved_qty, 'reserved'
FROM outbound_orders o
JOIN outbound_order_lines l ON l.outbound_order_id = o.id
JOIN stock_balances b ON b.product_id = l.product_id AND b.warehouse_id = o.warehouse_id AND b.reserved_qty > 0
WHERE o.status = 'reserved'
  AND NOT EXISTS (
      SELECT 1 FROM outbound_order_lines l2
      WHERE l2.outbound_order_id = o.id
        AND NOT (l2.picked_qty = 0
             AND (SELECT COALESCE(SUM(b2.reserved_qty), 0) FROM stock_balances b2
                  WHERE b2.product_id = l2.product_id AND b2.warehouse_id = o.warehouse_id) = l2.ordered_qty
             AND (SELECT COUNT(*) FROM outbound_order_lines l3
                  JOIN outbound_orders o3 ON o3.id = l3.outbound_order_id
                  WHERE l3.product_id = l2.product_id AND o3.warehouse_id = o.warehouse_id
                    AND o3.status IN ('reserved', 'picking')) = 1));
