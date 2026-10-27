"""Security and performance tests for the SQLite scripts in db/.

Run from the repository root:  python3 -m unittest discover -s db/tests -v

The AI connection authorizer below is the reference behaviour the C# application must reproduce
(SQLitePCLRaw sqlite3_set_authorizer): read-only, v_ai_* views only, no sensitive columns.
"""

import pathlib
import shutil
import sqlite3
import tempfile
import unittest

DB_DIR = pathlib.Path(__file__).resolve().parent.parent
BASE_SCRIPTS = ("schema.sql", "security.sql", "seed.sql")

SENSITIVE_COLUMNS = {
    "unit_cost_enc", "cpf_enc", "email_enc", "full_name_enc", "password_hash",
    "cnpj_enc", "contact_name_enc", "contact_email_enc", "phone_enc",
    "document_enc", "address_enc",
}

AI_ROLE_ID = 5

# Base tables behind the v_ai_* views. SQLite reports a READ with an empty column name when a joined
# table contributes no column (e.g. LEFT JOIN elimination); that exposes no data and is allowed only here.
AI_VIEW_TABLES = {
    "products", "locations", "zones", "lots", "stock_balances", "inbound_orders",
    "inbound_order_lines", "outbound_orders", "outbound_order_lines", "suppliers", "customers", "recipes",
}


def load_scripts(conn, names):
    for name in names:
        conn.executescript((DB_DIR / name).read_text(encoding="utf-8"))


def ai_authorizer(action, arg1, arg2, db_name, source):
    """Allow only SELECTs that read base tables through v_ai_* views."""
    if action in (sqlite3.SQLITE_SELECT, sqlite3.SQLITE_FUNCTION, sqlite3.SQLITE_RECURSIVE):
        return sqlite3.SQLITE_OK
    if action == sqlite3.SQLITE_READ:
        table, column = arg1, arg2
        if column in SENSITIVE_COLUMNS:
            return sqlite3.SQLITE_DENY
        if table.startswith("v_ai_") or (source or "").startswith("v_ai_"):
            return sqlite3.SQLITE_OK
        if column == "" and table in AI_VIEW_TABLES:
            return sqlite3.SQLITE_OK
        return sqlite3.SQLITE_DENY
    return sqlite3.SQLITE_DENY


def is_allowed(conn, role_id, permission):
    row = conn.execute(
        "SELECT 1 FROM v_effective_permissions WHERE role_id = ? AND permission = ?",
        (role_id, permission),
    ).fetchone()
    return row is not None


def ai_is_allowed(conn, on_behalf_role_id, permission):
    """The AI acts with the intersection of its own rights and the requesting user's rights."""
    return is_allowed(conn, AI_ROLE_ID, permission) and is_allowed(conn, on_behalf_role_id, permission)


class DatabaseTestCase(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.path = pathlib.Path(self.tmp) / "erp.db"
        self.conn = sqlite3.connect(self.path)
        self.conn.execute("PRAGMA foreign_keys = ON")
        load_scripts(self.conn, BASE_SCRIPTS)

    def tearDown(self):
        self.conn.close()
        shutil.rmtree(self.tmp)

    def assertDenied(self, sql, params=()):
        with self.assertRaises(sqlite3.DatabaseError) as ctx:
            self.conn.execute(sql, params)
        self.assertIn("DENY", str(ctx.exception))


class SeedTests(DatabaseTestCase):
    def test_seed_loads_expected_rows(self):
        counts = {t: self.conn.execute(f"SELECT count(*) FROM {t}").fetchone()[0]
                  for t in ("roles", "users", "products", "stock_balances", "memories")}
        self.assertEqual(counts, {"roles": 5, "users": 5, "products": 10, "stock_balances": 13, "memories": 4})

    def test_sensitive_columns_are_not_stored_in_plaintext(self):
        for table, column in (("products", "unit_cost_enc"), ("users", "cpf_enc"), ("suppliers", "cnpj_enc")):
            values = self.conn.execute(f"SELECT {column} FROM {table} WHERE {column} IS NOT NULL").fetchall()
            self.assertEqual(values, [], f"{table}.{column} must only hold ciphertext written by the app")

    def test_memory_full_text_search(self):
        rows = self.conn.execute(
            "SELECT m.scope_ref FROM memories_fts f JOIN memories m ON m.id = f.rowid "
            "WHERE memories_fts MATCH ? ORDER BY bm25(memories_fts)", ("refrigerados",)).fetchall()
        self.assertEqual(rows, [("armazenagem",)])


class ImmutabilityTests(DatabaseTestCase):
    def test_audit_log_is_append_only(self):
        self.assertDenied("UPDATE audit_log SET outcome = 'allowed' WHERE id = 4")
        self.assertDenied("DELETE FROM audit_log")

    def test_stock_movements_are_append_only(self):
        self.assertDenied("UPDATE stock_movements SET quantity = 999 WHERE id = 1")
        self.assertDenied("DELETE FROM stock_movements WHERE id = 1")

    def test_finished_plans_are_immutable_and_never_deleted(self):
        self.assertDenied("UPDATE plans SET status = 'failed' WHERE id = 1")
        self.assertDenied("DELETE FROM plans WHERE id = 2")

    def test_adjust_requires_reason(self):
        with self.assertRaises(sqlite3.IntegrityError):
            self.conn.execute(
                "INSERT INTO stock_movements (warehouse_id, product_id, from_location_id, kind, quantity) "
                "VALUES (1, 1, 3, 'adjust', -5)")


class PrivilegeEscalationTests(DatabaseTestCase):
    def test_ai_role_cannot_receive_forbidden_permissions(self):
        for permission in ("plan.approve", "stock.adjust", "sensitive.read", "role.manage"):
            self.assertDenied(
                "INSERT INTO role_permissions (role_id, permission, effect) VALUES (?, ?, 'ALLOW')",
                (AI_ROLE_ID, permission))

    def test_ai_deny_rows_cannot_be_removed(self):
        self.assertDenied("DELETE FROM role_permissions WHERE role_id = ? AND effect = 'DENY'", (AI_ROLE_ID,))

    def test_ai_cannot_approve_plans(self):
        self.assertDenied("UPDATE plans SET approved_by = 5 WHERE id = 2")

    def test_requester_cannot_approve_own_plan(self):
        self.assertDenied("UPDATE plans SET approved_by = 1 WHERE id = 2")

    def test_supervisor_can_approve_pending_plan(self):
        self.conn.execute("UPDATE plans SET approved_by = 2, status = 'executed' WHERE id = 2")
        self.assertEqual(self.conn.execute("SELECT approved_by FROM plans WHERE id = 2").fetchone(), (2,))

    def test_deny_overrides_allow(self):
        self.conn.execute("INSERT INTO role_permissions VALUES (1, 'plan.approve', 'ALLOW')")
        self.assertFalse(is_allowed(self.conn, 1, "plan.approve"))

    def test_ai_rights_are_intersection_with_requesting_user(self):
        operador, supervisor = 1, 2
        self.assertTrue(ai_is_allowed(self.conn, operador, "stock.move"))
        self.assertFalse(ai_is_allowed(self.conn, operador, "catalog.manage"))
        self.assertFalse(ai_is_allowed(self.conn, supervisor, "stock.adjust"))
        self.assertFalse(ai_is_allowed(self.conn, supervisor, "plan.approve"))


class AiConnectionTests(DatabaseTestCase):
    def setUp(self):
        super().setUp()
        self.conn.commit()
        self.ai = sqlite3.connect(f"file:{self.path}?mode=ro", uri=True)
        self.ai.set_authorizer(ai_authorizer)

    def tearDown(self):
        self.ai.close()
        super().tearDown()

    def test_ai_reads_stock_through_ai_view(self):
        rows = self.ai.execute("SELECT sku, available_qty FROM v_ai_stock WHERE sku = ?", ("ABC-124",)).fetchall()
        self.assertEqual(rows, [("ABC-124", 25)])

    def test_ai_cannot_read_base_tables_or_sensitive_columns(self):
        for sql in ("SELECT * FROM users",
                    "SELECT count(*) FROM users",
                    "SELECT sku FROM products",
                    "SELECT unit_cost_enc FROM products",
                    "SELECT password_hash FROM v_users_masked JOIN users USING (id)",
                    "SELECT * FROM audit_log"):
            with self.subTest(sql=sql), self.assertRaises(sqlite3.DatabaseError):
                self.ai.execute(sql).fetchall()

    def test_ai_cannot_write(self):
        with self.assertRaises(sqlite3.DatabaseError):
            self.ai.execute("UPDATE stock_balances SET quantity = 0")

    def test_ai_views_expose_no_sensitive_columns(self):
        views = [r[0] for r in self.conn.execute(
            "SELECT name FROM sqlite_schema WHERE type = 'view' AND name LIKE 'v_ai_%'")]
        self.assertGreaterEqual(len(views), 5)
        for view in views:
            columns = {r[1] for r in self.conn.execute(f"PRAGMA table_info({view})")}
            self.assertFalse(columns & SENSITIVE_COLUMNS, view)


class SqlInjectionTests(DatabaseTestCase):
    PAYLOAD = "x' OR '1'='1"

    def test_string_concatenation_is_vulnerable(self):
        # Shows why concatenation is forbidden: the payload turns the filter into "always true".
        rows = self.conn.execute(f"SELECT sku FROM v_ai_products WHERE sku = '{self.PAYLOAD}'").fetchall()
        self.assertEqual(len(rows), 10)

    def test_parameterized_query_treats_payload_as_data(self):
        rows = self.conn.execute("SELECT sku FROM v_ai_products WHERE sku = ?", (self.PAYLOAD,)).fetchall()
        self.assertEqual(rows, [])

    def test_stacked_statement_payload_does_not_execute(self):
        payload = "ABC-123'; DROP TABLE stock_movements; --"
        self.conn.execute("SELECT sku FROM v_ai_products WHERE sku = ?", (payload,)).fetchall()
        count = self.conn.execute("SELECT count(*) FROM stock_movements").fetchone()[0]
        self.assertEqual(count, 11)


class OptimizationTests(DatabaseTestCase):
    def setUp(self):
        super().setUp()
        load_scripts(self.conn, ("volume.sql",))

    def plan(self, sql):
        return " ".join(r[3] for r in self.conn.execute("EXPLAIN QUERY PLAN " + sql))

    def test_indexes_remove_full_scans(self):
        kardex = ("SELECT m.created_at FROM stock_movements m JOIN products p ON p.id = m.product_id "
                  "WHERE p.sku = 'VOL-00042' AND m.created_at >= '2026-01-01' AND m.created_at < '2026-07-01' "
                  "ORDER BY m.created_at")
        fefo = ("SELECT lt.expires_on, p.sku, lt.lot_code, l.code, b.quantity FROM lots lt "
                "JOIN stock_balances b ON b.lot_id = lt.id AND b.quantity > 0 "
                "JOIN products p ON p.id = lt.product_id JOIN locations l ON l.id = b.location_id "
                "WHERE lt.expires_on BETWEEN '2026-10-27' AND date('2026-10-27', '+30 days') "
                "ORDER BY lt.expires_on, p.sku")
        self.assertIn("SCAN m", self.plan(kardex))
        self.assertIn("SCAN b", self.plan(fefo))

        load_scripts(self.conn, ("optimize.sql",))

        self.assertIn("idx_stock_movements_product_created", self.plan(kardex))
        self.assertNotIn("SCAN m", self.plan(kardex))
        self.assertIn("idx_lots_expires_on", self.plan(fefo))
        self.assertIn("idx_stock_balances_lot", self.plan(fefo))


if __name__ == "__main__":
    unittest.main()
