import os
from pathlib import Path
import secrets
import tempfile
import unittest
import uuid
from concurrent.futures import ThreadPoolExecutor

from tools.postgres_dev import ROOT, literal, migration_script, sql, migrate, role_statement


class MigrationTests(unittest.TestCase):
    def test_sql_literal(self):
        self.assertEqual(literal("O'Brien"), "'O''Brien'")

    def test_migrations_are_checked_and_transactional(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / '001.sql'
            path.write_text('SELECT 1;', encoding='utf-8')
            script = migration_script([path])
        self.assertTrue(script.startswith('BEGIN;'))
        self.assertIn('pg_advisory_xact_lock', script)
        self.assertIn('Applied migration changed', script)
        self.assertIn('\\if :apply', script)
        self.assertTrue(script.endswith('COMMIT;'))


@unittest.skipUnless(os.environ.get('AION_POSTGRES_TESTS') == '1', 'Requires local PostgreSQL and imported catalogue')
class PostgreSQLTests(unittest.TestCase):
    def setUp(self):
        self.account, self.character = str(uuid.uuid4()), str(uuid.uuid4())
        self.build = sql('SELECT build_id FROM catalog.builds ORDER BY imported_at LIMIT 1;').stdout.strip()
        self.assertTrue(self.build)
        sql(f"INSERT INTO auth.accounts(account_id,provider,subject) VALUES('{self.account}','integration-test','{self.account}');"
            f"INSERT INTO game.characters(character_id,account_id,world_id,name,creation_id,race,gender) VALUES('{self.character}','{self.account}',-999,'{self.character}',1,1,1);")
        self.addCleanup(self.cleanup_fixture)

    def cleanup_fixture(self):
        sql(f"BEGIN; DELETE FROM game.item_grants WHERE instance_id IN(SELECT instance_id FROM game.item_instances WHERE character_id='{self.character}');"
            f"DELETE FROM audit.events WHERE character_id='{self.character}';"
            f"DELETE FROM game.item_instances WHERE character_id='{self.character}';"
            f"DELETE FROM game.characters WHERE character_id='{self.character}';"
            f"DELETE FROM auth.accounts WHERE account_id='{self.account}'; COMMIT;")

    def grant(self, request, quantity=1):
        return sql(f"SELECT game.grant_item('{self.character}','{self.build}',110160001,{quantity},'{request}');", role='aion_app').stdout.strip()

    def test_concurrent_grant_retry_returns_one_item(self):
        request = str(uuid.uuid4())
        with ThreadPoolExecutor(max_workers=2) as pool:
            results = list(pool.map(lambda _: self.grant(request), range(2)))
        self.assertEqual(results[0], results[1])
        self.assertEqual(sql(f"SELECT count(*) FROM game.item_instances WHERE character_id='{self.character}';").stdout.strip(), '1')
        self.assertEqual(sql(f"SELECT count(*) FROM audit.events WHERE character_id='{self.character}';").stdout.strip(), '1')
        with self.assertRaisesRegex(RuntimeError, 'different arguments'):
            self.grant(request, 2)

    def test_invalid_grant_rolls_back(self):
        request = str(uuid.uuid4())
        result = sql(f"SELECT game.grant_item('{self.character}','{self.build}',-999,1,'{request}');", role='aion_app', check=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(sql(f"SELECT count(*) FROM game.item_grants WHERE request_id='{request}';").stdout.strip(), '0')
        with self.assertRaisesRegex(RuntimeError, 'positive'):
            self.grant(request, 0)

    def test_runtime_cannot_assign_roles_change_catalog_or_delete_audit(self):
        statements = [f"INSERT INTO auth.account_roles VALUES('{self.account}','admin');",
                      "UPDATE catalog.items SET name='changed' WHERE false;", 'DELETE FROM audit.events WHERE false;']
        for statement in statements:
            self.assertNotEqual(sql(statement, role='aion_app', check=False).returncode, 0)

    def test_revisions_reject_stale_save_and_equipment_slot_conflict(self):
        result = sql(f"UPDATE game.characters SET level=2 WHERE character_id='{self.character}' AND revision=0 RETURNING revision;", role='aion_app')
        self.assertEqual(result.stdout.strip(), '1')
        stale = sql(f"UPDATE game.characters SET level=3 WHERE character_id='{self.character}' AND revision=0 RETURNING revision;", role='aion_app')
        self.assertEqual(stale.stdout.strip(), '')
        first, second = self.grant(str(uuid.uuid4())), self.grant(str(uuid.uuid4()))
        sql(f"UPDATE game.item_instances SET location='equipment',slot=1 WHERE instance_id='{first}';", role='aion_app')
        conflict = sql(f"UPDATE game.item_instances SET location='equipment',slot=1 WHERE instance_id='{second}';", role='aion_app', check=False)
        self.assertNotEqual(conflict.returncode, 0)

    def test_repeat_migrations_and_checksum_mismatch(self):
        migrate()
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / '001_initial.sql'
            path.write_text('-- changed', encoding='utf-8')
            result = sql(migration_script([path]), check=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Applied migration changed', result.stderr)


class RoleStatementTests(unittest.TestCase):
    def test_role_statement_targets_one_local_account(self):
        grant = role_statement('staffuser', 'admin')
        self.assertIn("provider='local' AND subject='staffuser'", grant)
        self.assertIn("'admin'", grant)
        self.assertIn('ON CONFLICT DO NOTHING', grant)
        self.assertTrue(role_statement('staffuser', 'gamemaster', revoke=True).startswith('DELETE FROM auth.account_roles'))

    def test_role_statement_rejects_unsafe_input(self):
        for username, role in [("x' OR '1'='1", 'admin'), ('staffuser', 'owner'), ('', 'gamemaster'), ('a' * 33, 'gamemaster'), ('staffuser', 'gm')]:
            with self.assertRaises(ValueError):
                role_statement(username, role)


@unittest.skipUnless(os.environ.get('AION_POSTGRES_TESTS') == '1', 'Requires local PostgreSQL')
class QunaWalletSqlTests(unittest.TestCase):
    """Migration 111 (the account Quna wallet, ported from KiskEmu) on a disposable database: the one-time move of the
    item stacks, the ledger's rules, concurrent spends, a stack credited later, staff changes and their audit."""

    QUNA = 535300006

    @classmethod
    def setUpClass(cls):
        cls.db = 'aion_quna_' + secrets.token_hex(6)
        sql(f'CREATE DATABASE {cls.db} OWNER aion_owner;', database='postgres')
        sql(f'REVOKE ALL ON DATABASE {cls.db} FROM PUBLIC; GRANT CONNECT ON DATABASE {cls.db} TO aion_app,aion_auth;', database=cls.db)
        files = sorted((ROOT / 'database/migrations').glob('*.sql'))
        sql(migration_script([f for f in files if f.name < '111']), database=cls.db)
        # The state of a live server before 111: Quna as item stacks.
        cls.a, cls.b, cls.c = (str(uuid.uuid4()) for _ in range(3))
        cls.a1, cls.a2, cls.b1 = (str(uuid.uuid4()) for _ in range(3))
        cls.run_sql(
            "INSERT INTO catalog.builds(build_id,manifest) VALUES('test','{}');"
            f"INSERT INTO catalog.items(build_id,template_id,name,category,definition) VALUES('test',{cls.QUNA},'Quna','currency','{{}}'),('test',110160001,'Sword','weapon','{{}}');"
            + ''.join(f"INSERT INTO auth.accounts(account_id,provider,subject) VALUES('{x}','integration-test','{x}');" for x in (cls.a, cls.b, cls.c))
            + ''.join(f"INSERT INTO game.characters(character_id,account_id,world_id,name,creation_id,race,gender) VALUES('{ch}','{acc}',1,'{ch[:12]}',1,1,1);"
                      for ch, acc in ((cls.a1, cls.a), (cls.a2, cls.a), (cls.b1, cls.b)))
            + cls.stack(cls.a1, 1_000) + cls.stack(cls.a2, 500, 'storage') + cls.stack(cls.a1, 200, 'account_storage', owner=cls.a)
            + cls.stack(cls.b1, 70) + cls.stack(cls.a1, 9, 'trade_escrow')
            + f"INSERT INTO game.item_instances(character_id,build_id,template_id,quantity) VALUES('{cls.a1}','test',110160001,1);")
        sql(migration_script(files), database=cls.db)

    @classmethod
    def tearDownClass(cls):
        sql(f'DROP DATABASE {cls.db} WITH (FORCE);', database='postgres')

    @classmethod
    def run_sql(cls, text, role='aion_admin', check=True):
        return sql(text, role=role, database=cls.db, check=check)

    @staticmethod
    def stack(character, quantity, location='bag', owner=None):
        return (f"INSERT INTO game.item_instances(character_id,build_id,template_id,quantity,location,account_storage_owner) "
                f"VALUES('{character}','test',535300006,{quantity},'{location}',{literal(owner) if owner else 'NULL'});")

    def scalar(self, text, role='aion_admin'):
        return self.run_sql(text, role=role).stdout.strip()

    def fresh(self):
        """A new account with one character (each test its own wallet)."""
        account, character = str(uuid.uuid4()), str(uuid.uuid4())
        self.run_sql(f"INSERT INTO auth.accounts(account_id,provider,subject) VALUES('{account}','integration-test','{account}');"
                     f"INSERT INTO game.characters(character_id,account_id,world_id,name,creation_id,race,gender) VALUES('{character}','{account}',1,'{character[:12]}',1,1,1);")
        return account, character

    def apply(self, account, request, delta, reason='purchase'):
        return self.scalar(f"SELECT ok, balance, COALESCE(error,''), repeated FROM game.quna_apply('{account}','{request}',{delta},'{reason}',NULL,NULL,NULL);",
                           role='aion_app').split('|')

    def test_existing_stacks_moved_once_without_double_counting(self):
        self.assertEqual(self.scalar(f"SELECT balance FROM game.quna_wallets WHERE account_id='{self.a}';"), '1700')
        self.assertEqual(self.scalar(f"SELECT balance FROM game.quna_wallets WHERE account_id='{self.b}';"), '70')
        self.assertEqual(self.scalar(f"SELECT count(*) FROM game.quna_wallets WHERE account_id='{self.c}';"), '0')
        self.assertEqual(self.scalar("SELECT string_agg(request_id || ':' || delta, ',' ORDER BY request_id) FROM game.quna_ledger WHERE reason='migration';"),
                         ','.join(sorted([f'migration:111:{self.a}:1700', f'migration:111:{self.b}:70'])))
        # Only the stack outside the old balance (trade escrow) is still an item; other items are untouched.
        self.assertEqual(self.scalar(f"SELECT string_agg(location || ':' || quantity, ',') FROM game.item_instances WHERE template_id={self.QUNA};"), 'trade_escrow:9')
        self.assertEqual(self.scalar("SELECT count(*) FROM game.item_instances WHERE template_id=110160001;"), '1')
        # Re-running the migrations applies nothing again.
        files = sorted((ROOT / 'database/migrations').glob('*.sql'))
        self.run_sql(migration_script(files))
        self.assertEqual(self.scalar("SELECT count(*) FROM game.quna_ledger WHERE reason='migration';"), '2')

    def test_ledger_is_idempotent_refuses_overdraft_and_is_append_only(self):
        acc, _ = self.fresh()
        self.assertEqual(self.apply(acc, f'g-{acc}', 1_000, 'grant'), ['t', '1000', '', 'f'])
        self.assertEqual(self.apply(acc, f's-{acc}', -400), ['t', '600', '', 'f'])
        self.assertEqual(self.apply(acc, f's-{acc}', -400), ['t', '600', '', 't'])
        self.assertEqual(self.apply(acc, f'x-{acc}', -601), ['f', '600', 'insufficient', 'f'])
        self.assertEqual(self.apply(acc, f's-{acc}', -1)[2], 'request_conflict')
        refund = self.scalar(f"SELECT ok, balance FROM game.quna_refund('{acc}','s-{acc}',NULL);", role='aion_app')
        self.assertEqual(refund, 't|1000')
        again = self.scalar(f"SELECT ok, balance, repeated FROM game.quna_refund('{acc}','s-{acc}',NULL);", role='aion_app')
        self.assertEqual(again, 't|1000|t')
        self.assertEqual(self.scalar(f"SELECT count(*) FROM game.quna_ledger WHERE account_id='{acc}';"), '3')
        self.assertNotEqual(self.run_sql(f"DELETE FROM game.quna_ledger WHERE account_id='{acc}';", check=False).returncode, 0)
        self.assertNotEqual(self.run_sql(f"UPDATE game.quna_ledger SET delta=5 WHERE account_id='{acc}';", check=False).returncode, 0)
        self.assertNotEqual(self.run_sql(f"UPDATE game.quna_wallets SET balance=-1 WHERE account_id='{acc}';", check=False).returncode, 0)

    def test_concurrent_spends_never_overdraw(self):
        acc, _ = self.fresh()
        self.apply(acc, f'top-{uuid.uuid4()}', 1_000, 'grant')
        with ThreadPoolExecutor(max_workers=4) as pool:
            results = list(pool.map(lambda i: self.apply(acc, f'race-{i}-{uuid.uuid4()}', -300), range(6)))
        self.assertEqual(sum(r[0] == 't' for r in results), 3)
        self.assertTrue(all(r[2] == 'insufficient' for r in results if r[0] == 'f'))
        self.assertEqual(self.scalar(f"SELECT balance FROM game.quna_wallets WHERE account_id='{acc}';"), '100')
        # The same request id from two callers at once is one spend.
        request = f'twice-{uuid.uuid4()}'
        self.apply(acc, f'top2-{uuid.uuid4()}', 900, 'grant')
        with ThreadPoolExecutor(max_workers=2) as pool:
            twice = list(pool.map(lambda _: self.apply(acc, request, -500), range(2)))
        self.assertEqual(sorted(r[3] for r in twice), ['f', 't'])
        self.assertEqual(self.scalar(f"SELECT balance FROM game.quna_wallets WHERE account_id='{acc}';"), '500')

    def test_a_stack_found_later_is_credited_once(self):
        acc, character = self.fresh()
        other, _ = self.fresh()
        self.apply(acc, f'open-{acc}', 100, 'grant')
        instance = self.scalar(f"INSERT INTO game.item_instances(character_id,build_id,template_id,quantity) VALUES('{character}','test',{self.QUNA},25) RETURNING instance_id;").splitlines()[0]
        # Another account cannot absorb it.
        self.assertEqual(self.scalar(f"SELECT ok, error FROM game.quna_absorb_stack('{other}','{instance}',NULL);", role='aion_app'), 'f|not_found')
        first = self.scalar(f"SELECT ok, balance FROM game.quna_absorb_stack('{acc}','{instance}',1);", role='aion_app')
        self.assertEqual(first, 't|125')
        self.assertEqual(self.scalar(f"SELECT ok, error FROM game.quna_absorb_stack('{acc}','{instance}',1);", role='aion_app'), 'f|not_found')
        self.assertEqual(self.scalar(f"SELECT count(*) FROM game.item_instances WHERE instance_id='{instance}';"), '0')
        self.assertEqual(self.scalar(f"SELECT reason FROM game.quna_ledger WHERE request_id='stack:{instance}';"), 'grant')

    def test_staff_changes_are_audited_once(self):
        (acc, _), (actor, _) = self.fresh(), self.fresh()
        request = f'staff:{uuid.uuid4().hex}'
        for _ in range(2):
            self.scalar(f"SELECT ok FROM game.quna_staff_apply('{actor}','{acc}','{request}',250,'compensation');", role='aion_app')
        self.assertEqual(self.scalar(f"SELECT count(*) FROM audit.events WHERE action='quna_staff_grant' AND details->>'request'='{request}';"), '1')
        self.assertEqual(self.scalar(f"SELECT details->>'note' FROM game.quna_ledger WHERE request_id='{request}';"), 'compensation')
        take = f'staff:{uuid.uuid4().hex}'
        self.assertEqual(self.scalar(f"SELECT ok FROM game.quna_staff_apply('{actor}','{acc}','{take}',-50,'');", role='aion_app'), 't')
        self.assertEqual(self.scalar(f"SELECT ok FROM game.quna_staff_refund('{actor}','{acc}','{take}');", role='aion_app'), 't')
        self.assertEqual(self.scalar(f"SELECT count(*) FROM audit.events WHERE action IN ('quna_staff_take','quna_staff_refund') AND details->>'account'='{acc}';"), '2')


@unittest.skipUnless(os.environ.get('AION_POSTGRES_TESTS') == '1', 'Requires local PostgreSQL')
class WorldRegistrySqlTests(unittest.TestCase):
    """Migration 120 (the world registry, ported from KiskEmu): a single-host database gets one primary row matching the
    built-in login rows and nothing else changes; the registry's constraints hold."""

    @classmethod
    def setUpClass(cls):
        cls.db = 'aion_worlds_' + secrets.token_hex(6)
        sql(f'CREATE DATABASE {cls.db} OWNER aion_owner;', database='postgres')
        sql(f'REVOKE ALL ON DATABASE {cls.db} FROM PUBLIC; GRANT CONNECT ON DATABASE {cls.db} TO aion_app,aion_auth;', database=cls.db)
        files = sorted((ROOT / 'database' / 'migrations').glob('*.sql'))
        sql(migration_script([f for f in files if f.name < '120']), database=cls.db)
        cls.account, cls.character = str(uuid.uuid4()), str(uuid.uuid4())
        sql(f"INSERT INTO auth.accounts(account_id,provider,subject) VALUES('{cls.account}','integration-test','{cls.account}');"
            f"INSERT INTO game.characters(character_id,account_id,world_id,name,creation_id,race,gender) VALUES('{cls.character}','{cls.account}',1204,'Single',1,1,1);",
            database=cls.db)
        cls.before = cls.dump()
        sql(migration_script(files), database=cls.db)

    @classmethod
    def tearDownClass(cls):
        sql(f'DROP DATABASE {cls.db} WITH (FORCE);', database='postgres')

    @classmethod
    def dump(cls):
        return sql("SELECT character_id,account_id,world_id,name FROM game.characters ORDER BY character_id;", database=cls.db).stdout

    def scalar(self, text, role='aion_admin', check=True):
        result = sql(text, role=role, database=self.db, check=check)
        return result.stdout.strip() if check else result

    def test_single_host_gets_one_primary_row_and_characters_are_untouched(self):
        self.assertEqual(self.scalar("SELECT world_id,light_server_id,dark_server_id,kind,enabled FROM game.worlds WHERE world_id=1204 OR kind='primary';"), '1204|1361|2361|primary|t')
        self.assertEqual(self.dump(), self.before)
        self.assertEqual(self.scalar("SELECT count(*) FROM auth.client_handoffs WHERE race IS NOT NULL;"), '0')

    def test_registry_constraints(self):
        self.scalar("INSERT INTO game.worlds(world_id,name,light_server_id,dark_server_id,kind,game_port,internal_port) "
                    "VALUES(1205,'Second',1362,2362,'managed',13329,8781);", role='aion_app')
        self.assertEqual(self.scalar("SELECT enabled FROM game.worlds WHERE world_id=1205;"), 'f')
        for bad in (
            "INSERT INTO game.worlds(world_id,name,light_server_id,dark_server_id,kind) VALUES(1206,'Two',1363,2363,'primary');",
            "INSERT INTO game.worlds(world_id,name,light_server_id,dark_server_id,kind,game_port) VALUES(1207,'second',1364,2364,'managed',13330);",
            "INSERT INTO game.worlds(world_id,name,light_server_id,dark_server_id,kind,game_port) VALUES(1208,'Row',1361,2365,'managed',13331);",
            "INSERT INTO game.worlds(world_id,name,light_server_id,dark_server_id,kind) VALUES(1209,'NoPort',1366,2366,'managed');",
        ):
            self.assertNotEqual(self.scalar(bad, role='aion_app', check=False).returncode, 0, bad)
