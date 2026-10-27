-- ERP Inteligente: the two queries expected to become slow with volume.
-- Run before and after optimize.sql:  sqlite3 erp.db < db/benchmark.sql
.timer on

-- Q1. Kardex: movement history of one SKU in a period, with running balance.
EXPLAIN QUERY PLAN
SELECT m.created_at, m.kind, m.quantity,
       SUM(CASE WHEN m.kind IN ('receive', 'ship', 'adjust') THEN m.quantity ELSE 0 END)
           OVER (ORDER BY m.created_at ROWS UNBOUNDED PRECEDING) AS running_balance
FROM stock_movements m
JOIN products p ON p.id = m.product_id
WHERE p.sku = 'VOL-00042'
  AND m.created_at >= '2026-01-01' AND m.created_at < '2026-07-01'
ORDER BY m.created_at;

SELECT count(*) AS q1_rows FROM (
    SELECT m.created_at, m.kind, m.quantity,
           SUM(CASE WHEN m.kind IN ('receive', 'ship', 'adjust') THEN m.quantity ELSE 0 END)
               OVER (ORDER BY m.created_at ROWS UNBOUNDED PRECEDING) AS running_balance
    FROM stock_movements m
    JOIN products p ON p.id = m.product_id
    WHERE p.sku = 'VOL-00042'
      AND m.created_at >= '2026-01-01' AND m.created_at < '2026-07-01'
    ORDER BY m.created_at);

-- Q2. FEFO: lots expiring in the next 30 days that still have stock, by expiry date.
EXPLAIN QUERY PLAN
SELECT lt.expires_on, p.sku, lt.lot_code, l.code AS location_code, b.quantity
FROM lots lt
JOIN stock_balances b ON b.lot_id = lt.id AND b.quantity > 0
JOIN products p ON p.id = lt.product_id
JOIN locations l ON l.id = b.location_id
WHERE lt.expires_on BETWEEN '2026-10-27' AND date('2026-10-27', '+30 days')
ORDER BY lt.expires_on, p.sku;

SELECT count(*) AS q2_rows FROM (
    SELECT lt.expires_on, p.sku, lt.lot_code, l.code AS location_code, b.quantity
    FROM lots lt
    JOIN stock_balances b ON b.lot_id = lt.id AND b.quantity > 0
    JOIN products p ON p.id = lt.product_id
    JOIN locations l ON l.id = b.location_id
    WHERE lt.expires_on BETWEEN '2026-10-27' AND date('2026-10-27', '+30 days')
    ORDER BY lt.expires_on, p.sku);
