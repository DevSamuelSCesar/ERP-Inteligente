"""Security and performance tests for the SQLite scripts in db/.

Run from the repository root:  python3 -m unittest discover -s db/tests -v

The AI connection authorizer below is the reference behaviour the C# application must reproduce
(SQLitePCLRaw sqlite3_set_authorizer): read-only, v_ai_* views only, no sensitive columns.
"""

import hashlib
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

MIGRATION_003 = DB_DIR / "migrations" / "003_full_catalog.sql"
MIGRATION_004 = DB_DIR / "migrations" / "004_reservations.sql"
MIGRATION_005 = DB_DIR / "migrations" / "005_skills.sql"
MIGRATION_006 = DB_DIR / "migrations" / "006_memory.sql"
MIGRATION_007 = DB_DIR / "migrations" / "007_recipes.sql"
MIGRATION_008 = DB_DIR / "migrations" / "008_llm_usage.sql"
MIGRATION_009 = DB_DIR / "migrations" / "009_knowledge_embeddings.sql"
MIGRATION_010 = DB_DIR / "migrations" / "010_llm_usage_cache.sql"
V3_SCRIPTS = ("schema_v3.sql", "security_v3.sql", "seed_v3.sql")
V4_SCRIPTS = ("schema_v4.sql", "security_v4.sql", "seed_v4.sql")
V5_SCRIPTS = ("schema_v5.sql", "security_v5.sql", "seed_v5.sql")
V6_SCRIPTS = ("schema_v6.sql", "security_v6.sql", "seed_v6.sql")
V7_SCRIPTS = ("schema_v7.sql", "security_v7.sql", "seed_v7.sql")
V8_SCRIPTS = ("schema_v8.sql", "security_v8.sql", "seed_v8.sql")
V9_SCRIPTS = ("schema_v9.sql", "security_v9.sql", "seed_v9.sql")
# Schema version 2 scripts (git commit d145eee), kept as C# fixtures and shared with the C# migration tests.
V2_FIXTURES = DB_DIR.parent / "tests" / "Nexus.Tests" / "Core" / "Data" / "Fixtures"
V2_SCRIPTS = ("schema_v2.sql", "security_v2.sql", "seed_v2.sql")
AI_DENY_PERMISSIONS = {"plan.approve", "sensitive.read", "user.manage", "role.manage", "audit.read", "recipe.manage",
                       "skill.manage", "memory.manage"}

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


# Audit hash chain, rule "v1" (reference for the C# application).
AUDIT_GENESIS = "GENESIS"
AUDIT_HASHED_FIELDS = ("id", "occurred_at", "actor_type", "actor_id", "on_behalf_of",
                       "action", "target", "outcome", "details_json")


def _audit_field(value):
    """None becomes "-;"; any other value becomes "<UTF-8 byte length>:<text>;"."""
    if value is None:
        return "-;"
    text = str(value)
    return f"{len(text.encode('utf-8'))}:{text};"


def audit_hash_v1(prev_hash, row):
    """Lowercase hex SHA-256 of "nexus-audit-v1\\n" + prev_hash + "\\n" + the fields of the row.

    row is a sequence of 9 values in AUDIT_HASHED_FIELDS order, or a dict with those keys.
    """
    if isinstance(row, dict):
        row = [row[name] for name in AUDIT_HASHED_FIELDS]
    canonical = "nexus-audit-v1\n" + prev_hash + "\n" + "".join(_audit_field(v) for v in row)
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def verify_audit_chain(conn):
    """Return (ok, first_broken_id, reason), walking audit_log ordered by id."""
    rows = conn.execute(
        "SELECT id, occurred_at, actor_type, actor_id, on_behalf_of, action, target, outcome, "
        "details_json, prev_hash, hash FROM audit_log ORDER BY id").fetchall()
    expected_prev = AUDIT_GENESIS
    for row in rows:
        row_id, prev_hash, stored_hash = row[0], row[9], row[10]
        if prev_hash != expected_prev:
            return False, row_id, "prev_hash does not match the previous row's hash"
        if audit_hash_v1(prev_hash, row[:9]) != stored_hash:
            return False, row_id, "hash does not match the recomputed value"
        expected_prev = stored_hash
    return True, None, None


class DatabaseTestCase(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        # Registered first, so it runs after every other cleanup: on Windows an open connection locks the file.
        self.addCleanup(shutil.rmtree, self.tmp)
        self.path = pathlib.Path(self.tmp) / "erp.db"
        self.conn = sqlite3.connect(self.path)
        self.conn.execute("PRAGMA foreign_keys = ON")
        load_scripts(self.conn, BASE_SCRIPTS)

    def tearDown(self):
        self.conn.close()

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


class AuditChainTests(DatabaseTestCase):
    # Known-answer vector for rule v1. Canonical text (UTF-8, newlines are literal):
    #   nexus-audit-v1\nGENESIS\n1:7;20:2026-11-07T12:00:00Z;4:user;1:1;-;10:stock.move;11:sku:ABC-123;7:allowed;9:{"qty":2};
    KAT_ROW = (7, "2026-11-07T12:00:00Z", "user", 1, None, "stock.move", "sku:ABC-123", "allowed", '{"qty":2}')
    KAT_HASH = "344c473b21c5e78f8e4fd28a95096cb3057c37a68fd33e172a3179a3677c63b5"

    # Non-ASCII target ('endereço' is 8 characters but 9 UTF-8 bytes) and NULL details_json.
    # Canonical text: nexus-audit-v1\nGENESIS\n1:9;20:2026-11-07T12:05:00Z;6:system;-;-;13:location.view;9:endereço;7:allowed;-;
    UNICODE_ROW = (9, "2026-11-07T12:05:00Z", "system", None, None, "location.view", "endereço", "allowed", None)
    UNICODE_HASH = "4566d77da7ce58fe9f71e062ff6c769791bf94b8ff9278cc010852ccb737ee5e"

    def test_seed_chain_verifies(self):
        self.assertEqual(verify_audit_chain(self.conn), (True, None, None))

    def test_known_answer_vector(self):
        self.assertEqual(audit_hash_v1(AUDIT_GENESIS, self.KAT_ROW), self.KAT_HASH)

    def test_tampered_row_is_detected(self):
        self.conn.execute("DROP TRIGGER audit_log_deny_update")
        self.conn.execute("UPDATE audit_log SET action = 'stock.adjust' WHERE id = 2")
        ok, broken_id, reason = verify_audit_chain(self.conn)
        self.assertFalse(ok)
        self.assertEqual(broken_id, 2)
        self.assertIsNotNone(reason)

    def test_broken_link_is_detected(self):
        self.conn.execute("DROP TRIGGER audit_log_deny_update")
        self.conn.execute("UPDATE audit_log SET prev_hash = ? WHERE id = 3", ("0" * 64,))
        ok, broken_id, reason = verify_audit_chain(self.conn)
        self.assertFalse(ok)
        self.assertEqual(broken_id, 3)
        self.assertIn("prev_hash", reason)

    def test_null_details_and_non_ascii_target_use_utf8_byte_length(self):
        self.assertEqual(audit_hash_v1(AUDIT_GENESIS, self.UNICODE_ROW), self.UNICODE_HASH)

        # The same kind of row appended after the seed must keep the chain valid.
        last_hash = self.conn.execute("SELECT hash FROM audit_log ORDER BY id DESC LIMIT 1").fetchone()[0]
        row = (5,) + self.UNICODE_ROW[1:]
        self.conn.execute(
            "INSERT INTO audit_log (id, occurred_at, actor_type, actor_id, on_behalf_of, action, target, "
            "outcome, details_json, prev_hash, hash) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            row + (last_hash, audit_hash_v1(last_hash, row)))
        self.assertEqual(verify_audit_chain(self.conn), (True, None, None))


class PrivilegeEscalationTests(DatabaseTestCase):
    def test_ai_role_cannot_receive_forbidden_permissions(self):
        for permission in ("plan.approve", "user.manage", "sensitive.read", "role.manage", "audit.read", "memory.manage"):
            self.assertDenied(
                "INSERT INTO role_permissions (role_id, permission, effect) VALUES (?, ?, 'ALLOW')",
                (AI_ROLE_ID, permission))

    def test_ai_role_can_receive_operational_permissions(self):
        # Decision of 2026-11-08: stock.adjust and stock.ship are no longer on the escalation list.
        for permission in ("stock.adjust", "stock.ship"):
            self.conn.execute(
                "DELETE FROM role_permissions WHERE role_id = ? AND permission = ? AND effect = 'ALLOW'",
                (AI_ROLE_ID, permission))
            self.conn.execute(
                "INSERT INTO role_permissions (role_id, permission, effect) VALUES (?, ?, 'ALLOW')",
                (AI_ROLE_ID, permission))
            self.assertTrue(is_allowed(self.conn, AI_ROLE_ID, permission))

    def test_ai_deny_rows_left_after_migration_are_exactly_the_restricted_ones(self):
        denied = {r[0] for r in self.conn.execute(
            "SELECT permission FROM role_permissions WHERE role_id = ? AND effect = 'DENY'", (AI_ROLE_ID,))}
        self.assertEqual(denied, AI_DENY_PERMISSIONS)

    def test_ai_deny_rows_cannot_be_removed(self):
        self.assertDenied("DELETE FROM role_permissions WHERE role_id = ? AND effect = 'DENY'", (AI_ROLE_ID,))
        self.assertDenied("DELETE FROM role_permissions WHERE role_id = ? AND permission = 'plan.approve' "
                          "AND effect = 'DENY'", (AI_ROLE_ID,))

    def test_ai_cannot_approve_plans(self):
        self.assertDenied("UPDATE plans SET approved_by = 5 WHERE id = 2")

    def test_requester_cannot_approve_own_plan(self):
        self.assertDenied("UPDATE plans SET approved_by = 1 WHERE id = 2")

    def test_supervisor_can_approve_pending_plan(self):
        self.conn.execute("UPDATE plans SET approved_by = 2, status = 'executed' WHERE id = 2")
        self.assertEqual(self.conn.execute("SELECT approved_by FROM plans WHERE id = 2").fetchone(), (2,))

    def test_ai_cannot_reject_plans(self):
        self.assertDenied(
            "UPDATE plans SET status = 'rejected', rejected_by = 5, rejection_reason = 'Motivo' WHERE id = 2")

    def test_requester_cannot_reject_own_plan(self):
        self.assertDenied(
            "UPDATE plans SET status = 'rejected', rejected_by = 1, rejection_reason = 'Motivo' WHERE id = 2")

    def test_supervisor_can_reject_pending_plan(self):
        self.conn.execute(
            "UPDATE plans SET status = 'rejected', rejected_by = 2, rejection_reason = 'Quantidade acima do saldo' "
            "WHERE id = 2")
        self.assertEqual(
            self.conn.execute("SELECT status, rejected_by, rejection_reason FROM plans WHERE id = 2").fetchone(),
            ("rejected", 2, "Quantidade acima do saldo"))

    def test_rejected_status_requires_rejecter_and_reason(self):
        with self.assertRaises(sqlite3.IntegrityError):
            self.conn.execute("UPDATE plans SET status = 'rejected', rejected_by = 2 WHERE id = 2")
        with self.assertRaises(sqlite3.IntegrityError):
            self.conn.execute("UPDATE plans SET status = 'rejected', rejection_reason = 'Motivo' WHERE id = 2")

    def test_rejection_reason_is_limited_to_200_characters(self):
        with self.assertRaises(sqlite3.IntegrityError):
            self.conn.execute(
                "UPDATE plans SET status = 'rejected', rejected_by = 2, rejection_reason = ? WHERE id = 2",
                ("x" * 201,))

    def test_plan_cannot_be_both_approved_and_rejected(self):
        with self.assertRaisesRegex(sqlite3.DatabaseError, "both approved and rejected"):
            self.conn.execute(
                "UPDATE plans SET status = 'rejected', approved_by = 2, rejected_by = 2, "
                "rejection_reason = 'Motivo' WHERE id = 2")

    def test_rejected_plan_is_immutable(self):
        self.conn.execute(
            "UPDATE plans SET status = 'rejected', rejected_by = 2, rejection_reason = 'Motivo' WHERE id = 2")
        self.assertDenied("UPDATE plans SET status = 'pending_approval', rejected_by = NULL, rejection_reason = NULL "
                          "WHERE id = 2")

    def test_deny_overrides_allow(self):
        self.conn.execute("INSERT INTO role_permissions VALUES (1, 'plan.approve', 'ALLOW')")
        self.assertFalse(is_allowed(self.conn, 1, "plan.approve"))

    def test_ai_rights_are_intersection_with_requesting_user(self):
        operador, supervisor = 1, 2
        self.assertTrue(ai_is_allowed(self.conn, operador, "stock.move"))
        self.assertTrue(ai_is_allowed(self.conn, operador, "order.manage"))
        self.assertFalse(ai_is_allowed(self.conn, operador, "catalog.manage"))
        # The AI holds stock.adjust, but an operador asking for it does not: the intersection is empty.
        self.assertFalse(ai_is_allowed(self.conn, operador, "stock.adjust"))
        self.assertFalse(ai_is_allowed(self.conn, operador, "stock.ship"))
        # A supervisor asking for it: the AI and the supervisor both hold it.
        self.assertTrue(ai_is_allowed(self.conn, supervisor, "stock.adjust"))
        self.assertTrue(ai_is_allowed(self.conn, supervisor, "stock.ship"))
        self.assertFalse(ai_is_allowed(self.conn, supervisor, "plan.approve"))
        self.assertFalse(ai_is_allowed(self.conn, supervisor, "user.manage"))

    def test_order_manage_is_allowed_for_operador_supervisor_and_ai(self):
        for role_id in (1, 2, 5):
            self.assertTrue(is_allowed(self.conn, role_id, "order.manage"), role_id)

    def test_ai_holds_location_and_stock_operations(self):
        for permission in ("location.manage", "stock.adjust", "stock.ship"):
            self.assertTrue(is_allowed(self.conn, AI_ROLE_ID, permission), permission)
        self.assertFalse(is_allowed(self.conn, 1, "location.manage"))


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

    def test_ai_reads_zones_lots_and_partners_through_views(self):
        self.assertEqual(
            self.ai.execute("SELECT code, is_active FROM v_ai_zones WHERE code = 'C'").fetchall(), [("C", 1)])
        self.assertEqual(
            self.ai.execute("SELECT sku, lot_code, expires_on FROM v_ai_lots WHERE lot_code = 'L200-2611'").fetchall(),
            [("LIM-200", "L200-2611", "2026-11-30")])
        self.assertEqual(
            self.ai.execute("SELECT kind, name, is_active FROM v_ai_partners WHERE id = 2 ORDER BY kind").fetchall(),
            [("customer", "Mercado Fictício Norte", 1), ("supplier", "Distribuidora Fictícia Beta S.A.", 1)])

    def test_ai_cannot_read_partner_documents_or_contacts(self):
        for sql in ("SELECT cnpj_enc FROM suppliers",
                    "SELECT contact_email_enc FROM customers",
                    "SELECT cnpj_masked FROM suppliers",
                    "SELECT document_masked FROM customers",
                    "SELECT kind, cnpj_masked FROM v_ai_partners JOIN suppliers USING (id)"):
            with self.subTest(sql=sql), self.assertRaises(sqlite3.DatabaseError):
                self.ai.execute(sql).fetchall()

    def test_ai_cannot_write(self):
        with self.assertRaises(sqlite3.DatabaseError):
            self.ai.execute("UPDATE stock_balances SET quantity = 0")

    def test_ai_cannot_read_memories_or_knowledge(self):
        # Retrieval runs on the application connection; the AI reads neither the memory nor the RAG tables.
        for sql in ("SELECT content FROM memories",
                    "SELECT count(*) FROM memories",
                    "SELECT content FROM memories_fts WHERE memories_fts MATCH 'refrigerados'",
                    "SELECT count(*) FROM knowledge_chunks",
                    "SELECT content FROM knowledge_fts WHERE knowledge_fts MATCH 'palete'"):
            with self.subTest(sql=sql), self.assertRaises(sqlite3.DatabaseError):
                self.ai.execute(sql).fetchall()

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


class MigrationTo3Tests(unittest.TestCase):
    """A version 2 database upgraded by migrations/003, 004 and 005 must match a fresh install (schema.sql + security.sql + seed.sql)."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.tmp)  # after the connections closed by other cleanups (Windows locks open files)
        self.paths = {"fresh": pathlib.Path(self.tmp) / "fresh.db", "migrated": pathlib.Path(self.tmp) / "migrated.db"}
        self.fresh = sqlite3.connect(self.paths["fresh"])
        load_scripts(self.fresh, BASE_SCRIPTS)
        self.migrated = sqlite3.connect(self.paths["migrated"])
        for name in V2_SCRIPTS:
            self.migrated.executescript((V2_FIXTURES / name).read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_003.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_004.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_005.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_006.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_007.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_008.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_009.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_010.read_text(encoding="utf-8"))

    def tearDown(self):
        self.fresh.close()
        self.migrated.close()

    def assertSameRows(self, query):
        self.assertEqual(self.migrated.execute(query).fetchall(), self.fresh.execute(query).fetchall(), query)

    def test_permissions_match_fresh_install(self):
        self.assertSameRows("SELECT role_id, permission, effect FROM role_permissions ORDER BY 1, 2, 3")
        self.assertSameRows("SELECT role_id, permission FROM v_effective_permissions ORDER BY 1, 2")

    def test_order_manage_and_ai_operational_rights_after_migration(self):
        for conn in (self.fresh, self.migrated):
            self.assertEqual(
                conn.execute("SELECT role_id FROM v_effective_permissions WHERE permission = 'order.manage' "
                             "ORDER BY role_id").fetchall(), [(1,), (2,), (5,)])
            for permission in ("location.manage", "stock.adjust", "stock.ship"):
                self.assertEqual(conn.execute("SELECT count(*) FROM v_effective_permissions "
                                              "WHERE role_id = 5 AND permission = ?", (permission,)).fetchone(), (1,))

    def test_views_and_triggers_match_fresh_install(self):
        self.assertSameRows("SELECT type, name FROM sqlite_schema WHERE type IN ('view', 'trigger') ORDER BY 1, 2")
        for view in ("v_ai_zones", "v_ai_lots", "v_ai_partners"):
            self.assertSameRows(f"SELECT name, type FROM pragma_table_info('{view}') ORDER BY cid")

    def test_catalog_columns_match_fresh_install(self):
        for table in ("zones", "suppliers", "customers"):
            with self.subTest(table=table):
                self.assertSameRows(
                    f"SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info('{table}') ORDER BY cid")

    def test_active_flags_only_accept_0_and_1(self):
        for table in ("zones", "suppliers", "customers"):
            with self.subTest(table=table), self.assertRaises(sqlite3.IntegrityError):
                self.migrated.execute(f"UPDATE {table} SET is_active = 2 WHERE id = 1")
        self.assertEqual(self.migrated.execute("SELECT count(*) FROM zones WHERE is_active = 1").fetchone(), (4,))

    def test_ai_restrictions_hold_after_migration(self):
        for label, conn in (("fresh", self.fresh), ("migrated", self.migrated)):
            with self.subTest(database=label):
                denied = {r[0] for r in conn.execute(
                    "SELECT permission FROM role_permissions WHERE role_id = 5 AND effect = 'DENY'")}
                self.assertEqual(denied, AI_DENY_PERMISSIONS)
                for permission in ("plan.approve", "sensitive.read", "role.manage", "user.manage", "audit.read"):
                    with self.assertRaisesRegex(sqlite3.DatabaseError, "DENY"):
                        conn.execute("INSERT INTO role_permissions (role_id, permission, effect) "
                                     "VALUES (5, ?, 'ALLOW')", (permission,))
                with self.assertRaisesRegex(sqlite3.DatabaseError, "DENY"):
                    conn.execute("DELETE FROM role_permissions WHERE role_id = 5 AND permission = 'plan.approve' "
                                 "AND effect = 'DENY'")
                conn.rollback()

    def test_ai_reads_new_views_through_the_authorizer(self):
        self.migrated.rollback()  # nothing pending; the read-only AI connection below must see the committed schema
        ai = sqlite3.connect(f"file:{self.paths['migrated']}?mode=ro", uri=True)
        self.addCleanup(ai.close)
        ai.set_authorizer(ai_authorizer)

        self.assertEqual(ai.execute("SELECT count(*) FROM v_ai_zones").fetchone(), (4,))
        self.assertEqual(ai.execute("SELECT count(*) FROM v_ai_lots").fetchone(), (6,))
        self.assertEqual(ai.execute("SELECT count(*) FROM v_ai_partners").fetchone(), (6,))
        for view in ("v_ai_zones", "v_ai_lots", "v_ai_partners"):
            columns = {r[1] for r in self.migrated.execute(f"PRAGMA table_info({view})")}
            self.assertFalse(columns & SENSITIVE_COLUMNS, view)
        self.assertEqual({r[1] for r in self.migrated.execute("PRAGMA table_info(v_ai_partners)")},
                         {"kind", "id", "name", "is_active"})
        with self.assertRaises(sqlite3.DatabaseError):
            ai.execute("SELECT cnpj_enc FROM suppliers").fetchall()


class ReservationTests(unittest.TestCase):
    """order_reservations: guarded transitions, immutable quantity, no delete, seed backfill."""

    def setUp(self):
        self.conn = sqlite3.connect(":memory:")
        load_scripts(self.conn, BASE_SCRIPTS)

    def tearDown(self):
        self.conn.close()

    def new_row(self, line=3, balance=8, qty=3):
        # line 3 = PED-FICT-5002 / product 7, balance 8 = product 7, lot 4 (holds 6 reserved of 16)
        return self.conn.execute(
            "INSERT INTO order_reservations (warehouse_id, outbound_order_line_id, balance_id, quantity) "
            "VALUES (1, ?, ?, ?)", (line, balance, qty)).lastrowid

    def test_seed_backfills_the_unambiguous_reserved_order(self):
        rows = self.conn.execute(
            "SELECT o.order_number, p.sku, r.quantity, r.state FROM order_reservations r "
            "JOIN outbound_order_lines l ON l.id = r.outbound_order_line_id "
            "JOIN outbound_orders o ON o.id = l.outbound_order_id JOIN products p ON p.id = l.product_id "
            "ORDER BY p.sku").fetchall()
        self.assertEqual(rows, [("PED-FICT-5001", "ABC-124", 10, "reserved"), ("PED-FICT-5001", "FER-010", 2, "reserved")])
        # Reservations never exceed what the balances say is reserved.
        self.assertEqual(self.conn.execute(
            "SELECT count(*) FROM stock_balances b WHERE (SELECT COALESCE(SUM(quantity), 0) FROM order_reservations r "
            "WHERE r.balance_id = b.id AND r.state IN ('reserved', 'picked')) > b.reserved_qty").fetchone(), (0,))

    def test_quantity_must_be_positive_and_state_known(self):
        with self.assertRaises(sqlite3.IntegrityError):
            self.new_row(qty=0)
        with self.assertRaises(sqlite3.IntegrityError):
            self.conn.execute("INSERT INTO order_reservations (warehouse_id, outbound_order_line_id, balance_id, quantity, state) "
                              "VALUES (1, 3, 8, 1, 'lost')")

    def test_rows_cannot_be_deleted(self):
        rid = self.new_row()
        with self.assertRaisesRegex(sqlite3.DatabaseError, "DENY"):
            self.conn.execute("DELETE FROM order_reservations WHERE id = ?", (rid,))

    def test_quantity_and_identity_are_immutable(self):
        rid = self.new_row()
        for column, value in (("quantity", 1), ("outbound_order_line_id", 4), ("warehouse_id", 2), ("created_plan_id", 1)):
            with self.subTest(column=column), self.assertRaisesRegex(sqlite3.DatabaseError, "DENY"):
                self.conn.execute(f"UPDATE order_reservations SET {column} = ? WHERE id = ?", (value, rid))

    def test_legal_transitions(self):
        rid = self.new_row()
        for state in ("picked", "shipped"):
            self.conn.execute("UPDATE order_reservations SET state = ? WHERE id = ?", (state, rid))
        other = self.new_row(qty=1)
        self.conn.execute("UPDATE order_reservations SET state = 'released' WHERE id = ?", (other,))
        third = self.new_row(qty=2)
        self.conn.execute("UPDATE order_reservations SET state = 'picked', balance_id = 9 WHERE id = ?", (third,))
        self.conn.execute("UPDATE order_reservations SET state = 'released' WHERE id = ?", (third,))

    def test_illegal_transitions_are_denied(self):
        for start, target in (("reserved", "shipped"), ("picked", "reserved"), ("shipped", "released"),
                              ("released", "reserved"), ("released", "picked"), ("shipped", "picked")):
            with self.subTest(start=start, target=target):
                rid = self.new_row()
                path = {"reserved": [], "picked": ["picked"], "shipped": ["picked", "shipped"], "released": ["released"]}[start]
                for step in path:
                    self.conn.execute("UPDATE order_reservations SET state = ? WHERE id = ?", (step, rid))
                with self.assertRaisesRegex(sqlite3.DatabaseError, "DENY"):
                    self.conn.execute("UPDATE order_reservations SET state = ? WHERE id = ?", (target, rid))

    def test_balance_may_only_change_with_a_pick(self):
        rid = self.new_row()
        with self.assertRaisesRegex(sqlite3.DatabaseError, "DENY"):
            self.conn.execute("UPDATE order_reservations SET balance_id = 9 WHERE id = ?", (rid,))
        with self.assertRaisesRegex(sqlite3.DatabaseError, "DENY"):
            self.conn.execute("UPDATE order_reservations SET state = 'released', balance_id = 9 WHERE id = ?", (rid,))


class MigrationTo4Tests(unittest.TestCase):
    """A version 3 database upgraded by migrations/004 and 005 must match a fresh install."""

    def setUp(self):
        self.fresh = sqlite3.connect(":memory:")
        load_scripts(self.fresh, BASE_SCRIPTS)
        self.migrated = sqlite3.connect(":memory:")
        for name in V3_SCRIPTS:
            self.migrated.executescript((V2_FIXTURES / name).read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_004.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_005.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_006.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_007.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_008.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_009.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_010.read_text(encoding="utf-8"))

    def tearDown(self):
        self.fresh.close()
        self.migrated.close()

    def assertSameRows(self, query):
        self.assertEqual(self.migrated.execute(query).fetchall(), self.fresh.execute(query).fetchall(), query)

    def test_schema_objects_match_fresh_install(self):
        self.assertSameRows("SELECT type, name FROM sqlite_schema ORDER BY 1, 2")
        self.assertSameRows(
            "SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info('order_reservations') ORDER BY cid")

    def test_backfill_matches_the_seed(self):
        self.assertSameRows("SELECT warehouse_id, outbound_order_line_id, balance_id, quantity, state "
                            "FROM order_reservations ORDER BY id")
        self.assertEqual(self.migrated.execute("SELECT count(*) FROM order_reservations").fetchone(), (2,))

    def test_ambiguous_orders_stay_legacy(self):
        # A second reserved order for the same product makes the allocation ambiguous: no rows are invented.
        conn = sqlite3.connect(":memory:")
        self.addCleanup(conn.close)
        for name in V3_SCRIPTS:
            conn.executescript((V2_FIXTURES / name).read_text(encoding="utf-8"))
        conn.execute("INSERT INTO outbound_orders (warehouse_id, customer_id, order_number, status) "
                     "VALUES (1, 1, 'PED-FICT-9', 'reserved')")
        conn.execute("INSERT INTO outbound_order_lines (outbound_order_id, product_id, ordered_qty) "
                     "VALUES (3, 2, 10)")
        conn.executescript(MIGRATION_004.read_text(encoding="utf-8"))
        self.assertEqual(conn.execute("SELECT count(*) FROM order_reservations").fetchone(), (0,))

    def test_audit_log_is_untouched(self):
        self.assertSameRows("SELECT id, hash FROM audit_log ORDER BY id")



class MigrationTo5Tests(unittest.TestCase):
    """A version 4 database upgraded by migrations/005 must match a fresh install, skill objects included."""

    def setUp(self):
        self.fresh = sqlite3.connect(":memory:")
        load_scripts(self.fresh, BASE_SCRIPTS)
        self.migrated = sqlite3.connect(":memory:")
        for name in V4_SCRIPTS:
            self.migrated.executescript((V2_FIXTURES / name).read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_005.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_006.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_007.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_008.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_009.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_010.read_text(encoding="utf-8"))

    def tearDown(self):
        self.fresh.close()
        self.migrated.close()

    def assertSameRows(self, query):
        self.assertEqual(self.migrated.execute(query).fetchall(), self.fresh.execute(query).fetchall(), query)

    def test_schema_objects_match_fresh_install(self):
        self.assertSameRows("SELECT type, name FROM sqlite_schema ORDER BY 1, 2")
        for table in ("skills", "skill_versions"):
            with self.subTest(table=table):
                self.assertSameRows(
                    f"SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info('{table}') ORDER BY cid")

    def test_skill_sql_matches_fresh_install(self):
        self.assertSameRows(
            "SELECT name, sql FROM sqlite_schema WHERE name IN ('skills', 'skill_versions', 'v_ai_skills', "
            "'skills_deny_delete', 'skills_guard_update', 'skill_versions_deny_update', "
            "'skill_versions_deny_delete', 'role_permissions_deny_ai_escalation') ORDER BY name")

    def test_permissions_match_fresh_install(self):
        self.assertSameRows("SELECT role_id, permission, effect FROM role_permissions ORDER BY 1, 2, 3")
        self.assertSameRows("SELECT role_id, permission FROM v_effective_permissions ORDER BY 1, 2")

    def test_existing_data_is_kept(self):
        self.assertEqual(self.migrated.execute("SELECT count(*) FROM stock_balances").fetchone(), (13,))
        self.assertEqual(self.migrated.execute("SELECT count(*) FROM skills").fetchone(), (0,))
        self.assertEqual(self.migrated.execute("SELECT count(*) FROM order_reservations").fetchone(), (2,))


class SkillTests(DatabaseTestCase):
    """skills and skill_versions: archived not deleted, guarded updates, append-only history, AI read through v_ai_skills."""

    DESCRIPTION = "Como conferir uma nota fiscal de recebimento antes de lançar a entrada no estoque."
    BODY = "Conferir a nota contra o pedido e lançar somente as quantidades recebidas."

    def add_skill(self, name="recebimento", status="active", version=1):
        # created_by / updated_by = 2 (sup.maria, supervisor).
        return self.conn.execute(
            "INSERT INTO skills (name, description, operations_json, body, version, status, origin, "
            "created_by, created_at, updated_by, updated_at) "
            "VALUES (?, ?, '[\"receive_stock\"]', ?, ?, ?, 'user', 2, '2026-11-08T10:00:00Z', 2, '2026-11-08T10:00:00Z')",
            (name, self.DESCRIPTION, self.BODY, version, status)).lastrowid

    def test_seed_has_no_skills(self):
        self.assertEqual(self.conn.execute("SELECT count(*) FROM skills").fetchone(), (0,))

    def test_name_must_be_lowercase_snake_case_between_2_and_40(self):
        for name in ("Recebimento", "a", "rec-1", "rec ebimento", "1recebimento", "x" * 41):
            with self.subTest(name=name), self.assertRaises(sqlite3.IntegrityError):
                self.add_skill(name=name)
        self.add_skill(name="recebimento_cego")

    def test_description_and_body_lengths_are_checked(self):
        with self.assertRaises(sqlite3.IntegrityError):
            self.conn.execute(
                "INSERT INTO skills (name, description, operations_json, body, version, status, origin, created_by, "
                "created_at, updated_by, updated_at) VALUES ('curta', 'curta', '[]', 'x', 1, 'active', 'user', 2, "
                "'2026-11-08T10:00:00Z', 2, '2026-11-08T10:00:00Z')")
        with self.assertRaises(sqlite3.IntegrityError):
            self.conn.execute(
                "INSERT INTO skills (name, description, operations_json, body, version, status, origin, created_by, "
                "created_at, updated_by, updated_at) VALUES ('vazia', ?, '[]', '', 1, 'active', 'user', 2, "
                "'2026-11-08T10:00:00Z', 2, '2026-11-08T10:00:00Z')", (self.DESCRIPTION,))

    def test_operations_must_be_a_json_array(self):
        with self.assertRaises(sqlite3.IntegrityError):
            self.conn.execute(
                "INSERT INTO skills (name, description, operations_json, body, version, status, origin, created_by, "
                "created_at, updated_by, updated_at) VALUES ('objeto', ?, '{\"a\": 1}', 'x', 1, 'active', 'user', 2, "
                "'2026-11-08T10:00:00Z', 2, '2026-11-08T10:00:00Z')", (self.DESCRIPTION,))

    def test_skills_are_archived_never_deleted(self):
        sid = self.add_skill()
        self.assertDenied("DELETE FROM skills WHERE id = ?", (sid,))

    def test_identity_and_creation_are_fixed(self):
        sid = self.add_skill()
        for sql in ("UPDATE skills SET name = 'outro' WHERE id = ?",
                    "UPDATE skills SET created_by = 1 WHERE id = ?",
                    "UPDATE skills SET created_at = '2026-01-01T00:00:00Z' WHERE id = ?"):
            with self.subTest(sql=sql):
                self.assertDenied(sql, (sid,))

    def test_version_never_goes_down(self):
        sid = self.add_skill(version=3)
        self.assertDenied("UPDATE skills SET version = 2 WHERE id = ?", (sid,))
        self.conn.execute("UPDATE skills SET version = 4 WHERE id = ?", (sid,))
        self.assertEqual(self.conn.execute("SELECT version FROM skills WHERE id = ?", (sid,)).fetchone(), (4,))

    def test_content_and_status_may_change(self):
        sid = self.add_skill()
        self.conn.execute(
            "UPDATE skills SET body = ?, status = 'archived', updated_by = 2, updated_at = '2026-11-09T09:00:00Z' "
            "WHERE id = ?", ("Texto revisado.", sid))
        self.assertEqual(self.conn.execute("SELECT status, body FROM skills WHERE id = ?", (sid,)).fetchone(),
                         ("archived", "Texto revisado."))

    def test_skill_versions_are_append_only(self):
        sid = self.add_skill()
        vid = self.conn.execute(
            "INSERT INTO skill_versions (skill_id, version, description, operations_json, body, origin, saved_by, saved_at) "
            "VALUES (?, 1, ?, '[]', ?, 'user', 2, '2026-11-08T10:00:00Z')", (sid, self.DESCRIPTION, self.BODY)).lastrowid
        self.assertDenied("UPDATE skill_versions SET body = 'alterado' WHERE id = ?", (vid,))
        self.assertDenied("DELETE FROM skill_versions WHERE id = ?", (vid,))

    def test_skill_version_numbers_are_unique_per_skill(self):
        sid = self.add_skill()
        insert = ("INSERT INTO skill_versions (skill_id, version, description, operations_json, body, origin, saved_by, saved_at) "
                  "VALUES (?, 1, ?, '[]', ?, 'user', 2, '2026-11-08T10:00:00Z')")
        self.conn.execute(insert, (sid, self.DESCRIPTION, self.BODY))
        with self.assertRaises(sqlite3.IntegrityError):
            self.conn.execute(insert, (sid, self.DESCRIPTION, self.BODY))

    def test_only_the_supervisor_holds_skill_manage(self):
        self.assertEqual(self.conn.execute(
            "SELECT role_id FROM v_effective_permissions WHERE permission = 'skill.manage' ORDER BY role_id").fetchall(),
            [(2,)])
        for role_id in (1, 3, 4, 5):
            self.assertFalse(is_allowed(self.conn, role_id, "skill.manage"), role_id)

    def test_ai_role_cannot_be_granted_skill_manage(self):
        self.assertDenied(
            "INSERT INTO role_permissions (role_id, permission, effect) VALUES (?, 'skill.manage', 'ALLOW')",
            (AI_ROLE_ID,))

    def test_ai_reads_only_active_skills_through_the_view(self):
        self.add_skill(name="recebimento", status="active")
        self.add_skill(name="antigo", status="archived")
        self.conn.commit()
        ai = sqlite3.connect(f"file:{self.path}?mode=ro", uri=True)
        self.addCleanup(ai.close)
        ai.set_authorizer(ai_authorizer)

        self.assertEqual(ai.execute("SELECT name, version, operations_json FROM v_ai_skills").fetchall(),
                         [("recebimento", 1, '["receive_stock"]')])
        self.assertEqual({r[1] for r in self.conn.execute("PRAGMA table_info(v_ai_skills)")},
                         {"name", "description", "operations_json", "body", "version"})
        for sql in ("SELECT name FROM skills",
                    "SELECT status FROM skills",
                    "SELECT count(*) FROM skills",
                    "SELECT count(*) FROM skill_versions",
                    "SELECT body FROM skill_versions"):
            with self.subTest(sql=sql), self.assertRaises(sqlite3.DatabaseError):
                ai.execute(sql).fetchall()


class MigrationTo6Tests(unittest.TestCase):
    """A version 5 database upgraded by migrations/006 must match a fresh install, memory objects included."""

    def setUp(self):
        self.fresh = sqlite3.connect(":memory:")
        load_scripts(self.fresh, BASE_SCRIPTS)
        self.migrated = sqlite3.connect(":memory:")
        self.migrated.execute("PRAGMA foreign_keys = ON")
        for name in V5_SCRIPTS:
            self.migrated.executescript((V2_FIXTURES / name).read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_006.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_007.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_008.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_009.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_010.read_text(encoding="utf-8"))

    def tearDown(self):
        self.fresh.close()
        self.migrated.close()

    def assertSameRows(self, query):
        self.assertEqual(self.migrated.execute(query).fetchall(), self.fresh.execute(query).fetchall(), query)

    def test_schema_objects_match_fresh_install(self):
        self.assertSameRows("SELECT type, name FROM sqlite_schema ORDER BY 1, 2")

    def test_memories_columns_match_fresh_install(self):
        self.assertSameRows(
            "SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info('memories') ORDER BY cid")

    def test_memory_triggers_index_and_permissions_match_fresh_install(self):
        self.assertSameRows(
            "SELECT name, sql FROM sqlite_schema WHERE name IN ('memories_fts', 'memories_fts_insert', "
            "'memories_fts_delete', 'memories_fts_update', 'memories_deny_delete', 'memories_guard_update', "
            "'memories_by_warehouse', 'role_permissions_deny_ai_escalation') ORDER BY name")
        self.assertSameRows("SELECT role_id, permission, effect FROM role_permissions ORDER BY 1, 2, 3")
        self.assertSameRows("SELECT role_id, permission FROM v_effective_permissions ORDER BY 1, 2")

    def test_existing_memories_keep_their_ids_and_content_and_get_the_new_columns(self):
        self.assertEqual(self.migrated.execute("SELECT count(*) FROM memories").fetchone(), (4,))
        self.assertEqual(self.migrated.execute("SELECT id, warehouse_id, status, origin, created_by, updated_at "
                                               "FROM memories ORDER BY id").fetchall(),
                         [(1, 1, "active", "user", 2, None), (2, 1, "active", "user", 2, None),
                          (3, 1, "active", "user", 2, None), (4, 1, "active", "user", 2, None)])
        self.assertSameRows("SELECT id, kind, scope, scope_ref, content, confidence, use_count, source_plan_id, "
                            "last_used_at FROM memories ORDER BY id")

    def test_full_text_search_finds_the_rebuilt_rows(self):
        rows = self.migrated.execute(
            "SELECT m.scope_ref FROM memories_fts f JOIN memories m ON m.id = f.rowid "
            "WHERE memories_fts MATCH ? ORDER BY bm25(memories_fts)", ("refrigerados",)).fetchall()
        self.assertEqual(rows, [("armazenagem",)])

    def test_checks_and_guards_hold_after_the_rebuild(self):
        with self.assertRaises(sqlite3.IntegrityError):
            self.migrated.execute("INSERT INTO memories (warehouse_id, kind, scope, content, origin) "
                                  "VALUES (1, 'fact', 'mundo', 'x', 'ai')")
        with self.assertRaisesRegex(sqlite3.DatabaseError, "DENY"):
            self.migrated.execute("DELETE FROM memories WHERE id = 1")
        with self.assertRaisesRegex(sqlite3.DatabaseError, "DENY"):
            self.migrated.execute("UPDATE memories SET origin = 'system' WHERE id = 1")
        self.assertEqual(self.migrated.execute("SELECT count(*) FROM role_permissions WHERE role_id = 5 "
                                               "AND permission = 'memory.manage' AND effect = 'DENY'").fetchone(), (1,))


class MigrationTo7Tests(unittest.TestCase):
    """A version 6 database upgraded by migrations/007 must match a fresh install, recipe objects included."""

    def setUp(self):
        self.fresh = sqlite3.connect(":memory:")
        load_scripts(self.fresh, BASE_SCRIPTS)
        self.migrated = sqlite3.connect(":memory:")
        self.migrated.execute("PRAGMA foreign_keys = ON")
        for name in V6_SCRIPTS:
            self.migrated.executescript((V2_FIXTURES / name).read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_007.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_008.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_009.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_010.read_text(encoding="utf-8"))

    def tearDown(self):
        self.fresh.close()
        self.migrated.close()

    def assertSameRows(self, query):
        self.assertEqual(self.migrated.execute(query).fetchall(), self.fresh.execute(query).fetchall(), query)

    def test_schema_objects_match_fresh_install(self):
        self.assertSameRows("SELECT type, name FROM sqlite_schema ORDER BY 1, 2")

    def test_recipes_columns_match_fresh_install(self):
        self.assertSameRows(
            "SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info('recipes') ORDER BY cid")

    def test_recipe_triggers_view_index_and_permissions_match_fresh_install(self):
        self.assertSameRows(
            "SELECT name, sql FROM sqlite_schema WHERE name IN ('recipes_deny_delete', 'recipes_guard_update', "
            "'recipes_by_signature', 'v_ai_recipes', 'role_permissions_deny_ai_escalation') ORDER BY name")
        self.assertSameRows("SELECT role_id, permission, effect FROM role_permissions ORDER BY 1, 2, 3")

    def test_seeded_recipe_gets_the_new_values(self):
        self.assertSameRows("SELECT id, name, status, success_count, failure_count, skill, signature, example_params_json, "
                            "failure_streak, source_plan_id, promoted_by, created_at FROM recipes ORDER BY id")


class MigrationTo8Tests(unittest.TestCase):
    """A version 7 database upgraded by migrations/008 must match a fresh install (llm_usage and its guards)."""

    def setUp(self):
        self.fresh = sqlite3.connect(":memory:")
        load_scripts(self.fresh, BASE_SCRIPTS)
        self.migrated = sqlite3.connect(":memory:")
        self.migrated.execute("PRAGMA foreign_keys = ON")
        for name in V7_SCRIPTS:
            self.migrated.executescript((V2_FIXTURES / name).read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_008.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_009.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_010.read_text(encoding="utf-8"))

    def tearDown(self):
        self.fresh.close()
        self.migrated.close()

    def assertSameRows(self, query):
        self.assertEqual(self.migrated.execute(query).fetchall(), self.fresh.execute(query).fetchall(), query)

    def test_schema_objects_match_fresh_install(self):
        self.assertSameRows("SELECT type, name FROM sqlite_schema ORDER BY 1, 2")

    def test_usage_table_and_guards_match_fresh_install(self):
        self.assertSameRows(
            "SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info('llm_usage') ORDER BY cid")
        self.assertSameRows("SELECT name, sql FROM sqlite_schema WHERE name IN ('llm_usage_by_time', "
                            "'llm_usage_deny_update', 'llm_usage_deny_delete') ORDER BY name")


class MigrationTo9Tests(unittest.TestCase):
    """A version 8 database upgraded by migrations/009 must match a fresh install (knowledge_embeddings)."""

    def setUp(self):
        self.fresh = sqlite3.connect(":memory:")
        load_scripts(self.fresh, BASE_SCRIPTS)
        self.migrated = sqlite3.connect(":memory:")
        self.migrated.execute("PRAGMA foreign_keys = ON")
        for name in V8_SCRIPTS:
            self.migrated.executescript((V2_FIXTURES / name).read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_009.read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_010.read_text(encoding="utf-8"))

    def tearDown(self):
        self.fresh.close()
        self.migrated.close()

    def assertSameRows(self, query):
        self.assertEqual(self.migrated.execute(query).fetchall(), self.fresh.execute(query).fetchall(), query)

    def test_schema_objects_match_fresh_install(self):
        self.assertSameRows("SELECT type, name FROM sqlite_schema ORDER BY 1, 2")

    def test_embedding_table_and_trigger_match_fresh_install(self):
        self.assertSameRows(
            "SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info('knowledge_embeddings') ORDER BY cid")
        self.assertSameRows("SELECT name, sql FROM sqlite_schema WHERE name = 'knowledge_embeddings_chunk_delete'")


class MigrationTo10Tests(unittest.TestCase):
    """A version 9 database upgraded by migrations/010 must match a fresh install (cache columns of llm_usage)."""

    def setUp(self):
        self.fresh = sqlite3.connect(":memory:")
        load_scripts(self.fresh, BASE_SCRIPTS)
        self.migrated = sqlite3.connect(":memory:")
        self.migrated.execute("PRAGMA foreign_keys = ON")
        for name in V9_SCRIPTS:
            self.migrated.executescript((V2_FIXTURES / name).read_text(encoding="utf-8"))
        self.migrated.executescript(MIGRATION_010.read_text(encoding="utf-8"))

    def tearDown(self):
        self.fresh.close()
        self.migrated.close()

    def assertSameRows(self, query):
        self.assertEqual(self.migrated.execute(query).fetchall(), self.fresh.execute(query).fetchall(), query)

    def test_usage_columns_match_fresh_install(self):
        self.assertSameRows(
            "SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info('llm_usage') ORDER BY cid")
        self.assertSameRows("SELECT type, name FROM sqlite_schema ORDER BY 1, 2")

    def test_cache_columns_are_checked_and_usage_stays_append_only(self):
        for conn in (self.fresh, self.migrated):
            with self.assertRaises(sqlite3.IntegrityError):
                conn.execute("INSERT INTO llm_usage (occurred_at, role, provider, model, calls, prompt_tokens, "
                             "completion_tokens, result, cache_write_tokens) VALUES ('t', 'planner', 'claude', 'm', 1, 1, 1, 'answer', -5)")
            uid = conn.execute("INSERT INTO llm_usage (occurred_at, role, provider, model, calls, prompt_tokens, "
                               "completion_tokens, result) VALUES ('t', 'planner', 'claude', 'm', 1, 1, 1, 'answer')").lastrowid
            with self.assertRaisesRegex(sqlite3.DatabaseError, "DENY"):
                conn.execute("UPDATE llm_usage SET cache_read_tokens = 1 WHERE id = ?", (uid,))


class KnowledgeEmbeddingTests(DatabaseTestCase):
    """knowledge_embeddings: float32 vectors tied to their chunk, never readable by the AI."""

    def add(self, chunk_id=900, dims=2, blob=None):
        self.conn.execute("INSERT OR IGNORE INTO knowledge_chunks (id, source_path, heading, content, token_count, sha256) "
                          "VALUES (?, 'knowledge/x.md', 'X', 'texto', 1, 'abc')", (chunk_id,))
        blob = bytes(4 * dims) if blob is None else blob
        self.conn.execute("INSERT INTO knowledge_embeddings (chunk_id, model, dimensions, sha256, embedding, created_at) "
                          "VALUES (?, 'm', ?, 'abc', ?, '2026-11-11T10:00:00Z')", (chunk_id, dims, blob))

    def test_blob_length_matches_dimensions(self):
        with self.assertRaises(sqlite3.IntegrityError):
            self.add(blob=bytes(7))
        with self.assertRaises(sqlite3.IntegrityError):
            self.add(dims=0, blob=b"")
        self.add()

    def test_vector_goes_with_its_chunk(self):
        self.add()
        self.conn.commit()
        self.conn.execute("PRAGMA foreign_keys = OFF")  # the trigger works without foreign keys too
        self.conn.execute("DELETE FROM knowledge_chunks WHERE id = 900")
        self.assertEqual(self.conn.execute("SELECT count(*) FROM knowledge_embeddings").fetchone(), (0,))

    def test_ai_cannot_read_vectors(self):
        self.add()
        self.conn.commit()
        ai = sqlite3.connect(":memory:")
        try:
            self.conn.backup(ai)
            ai.set_authorizer(ai_authorizer)
            with self.assertRaises(sqlite3.DatabaseError):
                ai.execute("SELECT embedding FROM knowledge_embeddings").fetchall()
        finally:
            ai.close()


class LlmUsageTests(DatabaseTestCase):
    """llm_usage: append-only accounting, restricted values, never readable by the AI."""

    def add(self, **kw):
        row = {"occurred_at": "2026-11-11T10:00:00Z", "user_id": 1, "warehouse_id": 1, "role": "planner",
               "provider": "claude", "model": "claude-sonnet-5-5", "calls": 3, "prompt_tokens": 12000,
               "completion_tokens": 2000, "result": "proposals"}
        row.update(kw)
        cols = ", ".join(row)
        return self.conn.execute(f"INSERT INTO llm_usage ({cols}) VALUES ({', '.join('?' * len(row))})",
                                 tuple(row.values())).lastrowid

    def test_usage_is_append_only(self):
        uid = self.add()
        self.assertDenied("UPDATE llm_usage SET prompt_tokens = 0 WHERE id = ?", (uid,))
        self.assertDenied("DELETE FROM llm_usage WHERE id = ?", (uid,))

    def test_values_are_restricted(self):
        for kw in ({"role": "chat"}, {"provider": "gemini"}, {"prompt_tokens": -1}, {"result": "ok"}, {"model": ""}):
            with self.subTest(kw=kw), self.assertRaises(sqlite3.IntegrityError):
                self.add(**kw)
        self.add(role="matcher", provider="local", model="nexus-matcher", result="no_match")

    def test_ai_cannot_read_usage(self):
        self.add()
        self.conn.commit()
        ai = sqlite3.connect(":memory:")
        try:
            self.conn.backup(ai)
            ai.set_authorizer(ai_authorizer)
            with self.assertRaises(sqlite3.DatabaseError):
                ai.execute("SELECT model FROM llm_usage").fetchall()
        finally:
            ai.close()


class RecipeTests(DatabaseTestCase):
    """recipes: demoted not deleted, fixed template and origin, one recipe per structure, never managed by the AI."""

    def add_recipe(self, name="mover_teste", signature="move_stock(from,qty,sku,to)#2", status="candidate"):
        return self.conn.execute(
            "INSERT INTO recipes (name, description, intent, plan_template_json, params_schema_json, dsl_version, "
            "skill_version, status, signature, created_at) VALUES (?, 'Teste', 'putaway', '{\"steps\":[]}', '{}', '1.0', "
            "'armazenagem@1', ?, ?, '2026-11-11T10:00:00Z')", (name, status, signature)).lastrowid

    def test_recipes_are_demoted_never_deleted(self):
        rid = self.add_recipe()
        self.assertDenied("DELETE FROM recipes WHERE id = ?", (rid,))

    def test_template_parameters_intent_and_origin_are_fixed(self):
        rid = self.add_recipe()
        for sql in ("UPDATE recipes SET plan_template_json = '{}' WHERE id = ?",
                    "UPDATE recipes SET params_schema_json = '{\"type\":\"object\"}' WHERE id = ?",
                    "UPDATE recipes SET intent = 'outra' WHERE id = ?",
                    "UPDATE recipes SET signature = 'x' WHERE id = ?",
                    "UPDATE recipes SET source_plan_id = 1 WHERE id = ?",
                    "UPDATE recipes SET created_at = '2020-01-01T00:00:00Z' WHERE id = ?"):
            with self.subTest(sql=sql):
                self.assertDenied(sql, (rid,))
        self.conn.execute("UPDATE recipes SET status = 'active', name = 'mover_ok', success_count = 3, "
                          "failure_streak = 1, status_reason = 'Promovida' WHERE id = ?", (rid,))

    def test_one_recipe_per_structure(self):
        self.add_recipe()
        with self.assertRaises(sqlite3.IntegrityError):
            self.add_recipe(name="outro_nome")

    def test_ai_role_cannot_get_recipe_manage(self):
        # BEFORE INSERT: the trigger fires before the existing DENY row could make it a duplicate.
        self.assertDenied("INSERT INTO role_permissions (role_id, permission, effect) VALUES (?, 'recipe.manage', 'ALLOW')",
                          (AI_ROLE_ID,))

    def test_ai_reads_only_active_recipes_with_their_template(self):
        self.add_recipe()
        self.conn.commit()  # an open write transaction would make the backup wait forever
        ai = sqlite3.connect(":memory:")
        try:
            self.conn.backup(ai)
            ai.set_authorizer(ai_authorizer)
            rows = ai.execute("SELECT name, skill, plan_template_json FROM v_ai_recipes").fetchall()
            self.assertEqual([r[0] for r in rows], ["armazenar_da_doca"])
            self.assertIn("$sku", rows[0][2])
            with self.assertRaises(sqlite3.DatabaseError):
                ai.execute("SELECT name FROM recipes").fetchall()
        finally:
            ai.close()


class MemoryTests(DatabaseTestCase):
    """memories: archived not deleted, fixed identity, CHECKs, full-text index in step with content, supervisor-only manage."""

    CONTENT = "Produtos refrigerados (ALI-*) vão para a zona C, nunca para a doca por mais de 30 minutos."

    def add_memory(self, content=CONTENT, scope="sku", scope_ref="ALI-300", kind="fact", confidence=0.5, origin="ai"):
        # warehouse 1 (CD-01); created_by 2 (sup.maria).
        return self.conn.execute(
            "INSERT INTO memories (warehouse_id, kind, scope, scope_ref, content, confidence, origin, created_by) "
            "VALUES (1, ?, ?, ?, ?, ?, ?, 2)", (kind, scope, scope_ref, content, confidence, origin)).lastrowid

    def test_scope_kind_and_origin_are_restricted(self):
        for kwargs in ({"scope": "mundo"}, {"kind": "opiniao"}, {"origin": "desconhecida"}):
            with self.subTest(kwargs=kwargs), self.assertRaises(sqlite3.IntegrityError):
                self.add_memory(**kwargs)
        self.add_memory(scope="customer", scope_ref="1")

    def test_content_is_limited_to_1000_characters(self):
        with self.assertRaises(sqlite3.IntegrityError):
            self.add_memory(content="x" * 1001)
        with self.assertRaises(sqlite3.IntegrityError):
            self.add_memory(content="")
        self.add_memory(content="x" * 1000)

    def test_confidence_is_between_0_and_1(self):
        for value in (1.5, -0.1):
            with self.subTest(confidence=value), self.assertRaises(sqlite3.IntegrityError):
                self.add_memory(confidence=value)

    def test_scope_ref_is_null_or_1_to_60_characters(self):
        with self.assertRaises(sqlite3.IntegrityError):
            self.add_memory(scope_ref="x" * 61)
        with self.assertRaises(sqlite3.IntegrityError):
            self.add_memory(scope_ref="")
        self.add_memory(scope="global", scope_ref=None)

    def test_use_count_is_not_negative(self):
        with self.assertRaises(sqlite3.IntegrityError):
            self.conn.execute("INSERT INTO memories (warehouse_id, kind, scope, content, use_count, origin) "
                              "VALUES (1, 'fact', 'global', 'Regra.', -1, 'ai')")

    def test_memories_are_archived_never_deleted(self):
        mid = self.add_memory()
        self.assertDenied("DELETE FROM memories WHERE id = ?", (mid,))
        self.assertDenied("DELETE FROM memories")

    def test_identity_warehouse_origin_and_creation_are_fixed(self):
        mid = self.add_memory()
        for sql in ("UPDATE memories SET id = 99 WHERE id = ?",
                    "UPDATE memories SET warehouse_id = 2 WHERE id = ?",
                    "UPDATE memories SET origin = 'user' WHERE id = ?",
                    "UPDATE memories SET created_by = 1 WHERE id = ?",
                    "UPDATE memories SET created_at = '2026-01-01T00:00:00Z' WHERE id = ?"):
            with self.subTest(sql=sql):
                self.assertDenied(sql, (mid,))

    def test_content_scope_status_and_counters_may_change(self):
        mid = self.add_memory()
        self.conn.execute(
            "UPDATE memories SET scope = 'zone', scope_ref = 'C', confidence = 0.9, use_count = use_count + 1, "
            "status = 'archived', updated_at = '2026-11-09T09:00:00Z', last_used_at = '2026-11-09T09:00:00Z' "
            "WHERE id = ?", (mid,))
        self.assertEqual(self.conn.execute("SELECT scope, status, use_count FROM memories WHERE id = ?", (mid,)).fetchone(),
                         ("zone", "archived", 1))

    def test_full_text_index_follows_content_changes(self):
        mid = self.add_memory(content="Paletes bananeiras ficam no corredor D.")
        self.conn.execute("UPDATE memories SET content = ? WHERE id = ?", ("Caixas frágeis vão para a zona B.", mid))
        found = lambda term: [r[0] for r in self.conn.execute(
            "SELECT rowid FROM memories_fts WHERE memories_fts MATCH ?", (term,)).fetchall()]
        self.assertEqual(found("bananeiras"), [])
        self.assertEqual(found("frágeis"), [mid])

    def test_only_the_supervisor_holds_memory_manage(self):
        self.assertEqual(self.conn.execute(
            "SELECT role_id FROM v_effective_permissions WHERE permission = 'memory.manage' ORDER BY role_id").fetchall(),
            [(2,)])
        for role_id in (1, 3, 4, 5):
            self.assertFalse(is_allowed(self.conn, role_id, "memory.manage"), role_id)

    def test_memory_write_is_unchanged(self):
        self.assertEqual(self.conn.execute(
            "SELECT role_id FROM v_effective_permissions WHERE permission = 'memory.write' ORDER BY role_id").fetchall(),
            [(1,), (2,), (5,)])

    def test_ai_role_cannot_be_granted_memory_manage(self):
        self.assertDenied(
            "INSERT INTO role_permissions (role_id, permission, effect) VALUES (?, 'memory.manage', 'ALLOW')",
            (AI_ROLE_ID,))


if __name__ == "__main__":
    unittest.main()
