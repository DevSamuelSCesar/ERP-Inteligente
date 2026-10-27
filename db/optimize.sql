-- ERP Inteligente: indexes for the benchmark queries (see benchmark.sql).

-- Q1 (kardex): seek by product, range and order by date without a temp B-tree.
CREATE INDEX IF NOT EXISTS idx_stock_movements_product_created
    ON stock_movements (product_id, created_at);

-- Q2 (FEFO): covering range scan on expiry date, then covering lookup of balances by lot.
-- Plain (non-covering) versions of these indexes only cut ~15%; covering ones cut ~70%.
CREATE INDEX IF NOT EXISTS idx_lots_expires_on
    ON lots (expires_on, product_id, lot_code);

CREATE INDEX IF NOT EXISTS idx_stock_balances_lot
    ON stock_balances (lot_id, location_id, quantity) WHERE quantity > 0;

ANALYZE;
