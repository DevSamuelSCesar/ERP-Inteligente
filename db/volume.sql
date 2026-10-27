-- ERP Inteligente: synthetic volume for the optimization benchmark (apply after seed.sql).
-- Generates fictitious rows with recursive CTEs:
--   5,000 products · 2,000 locations · 100,000 lots · 300,000 balances · 1,000,000 movements.
-- Run it on a throwaway database, never on production data.

PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;

BEGIN;

CREATE TEMP TABLE vol_base AS
SELECT (SELECT max(id) FROM products)  AS product0,
       (SELECT max(id) FROM locations) AS location0,
       (SELECT max(id) FROM lots)      AS lot0;

INSERT INTO products (sku, name, unit, weight_kg, tracks_expiry)
WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 5000)
SELECT printf('VOL-%05d', i), printf('Produto sintético %05d', i), 'un', (i % 50) / 10.0, i % 3 = 0
FROM n;

INSERT INTO locations (zone_id, code, capacity)
WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 2000)
SELECT 2 + (i % 3), printf('V-%04d', i), 100
FROM n;

INSERT INTO lots (product_id, lot_code, expires_on)
WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 100000)
SELECT (SELECT product0 FROM vol_base) + 1 + (i % 5000),
       printf('VL%06d', i),
       date('2026-01-01', '+' || (abs(random()) % 730) || ' days')
FROM n;

INSERT OR IGNORE INTO stock_balances (warehouse_id, product_id, location_id, lot_id, quantity)
WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 300000)
SELECT 1,
       l.product_id,
       (SELECT location0 FROM vol_base) + 1 + (abs(random()) % 2000),
       l.id,
       1 + abs(random()) % 200
FROM n
JOIN lots l ON l.id = (SELECT lot0 FROM vol_base) + 1 + (n.i % 100000);

INSERT INTO stock_movements (warehouse_id, product_id, from_location_id, to_location_id, kind, quantity, reason, user_id, created_at)
WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 1000000),
-- MATERIALIZED: evaluate random() once per row; an inlined CTE would re-roll it on every reference.
r AS MATERIALIZED (
    SELECT i,
           (SELECT product0 FROM vol_base) + 1 + (abs(random()) % 5000) AS product_id,
           (SELECT location0 FROM vol_base) + 1 + (abs(random()) % 2000) AS location_id,
           abs(random()) % 5 AS k,
           1 + abs(random()) % 50 AS qty,
           abs(random()) % 57024000 AS offset_s
    FROM n
)
SELECT 1,
       product_id,
       CASE WHEN k IN (1, 2, 3, 4) THEN location_id END,
       CASE WHEN k IN (0, 1) THEN location_id END,
       CASE k WHEN 0 THEN 'receive' WHEN 1 THEN 'move' WHEN 2 THEN 'pick' WHEN 3 THEN 'ship' ELSE 'adjust' END,
       CASE WHEN k IN (3, 4) THEN -qty ELSE qty END,
       CASE WHEN k = 4 THEN 'Ajuste sintético de volume' END,
       1 + (i % 2),
       strftime('%Y-%m-%dT%H:%M:%SZ', '2025-01-01', '+' || offset_s || ' seconds')
FROM r;

DROP TABLE vol_base;

COMMIT;

ANALYZE;
