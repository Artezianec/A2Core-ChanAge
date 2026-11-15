"""Catalogue import and starter-profile setup used by start-all.cmd and install.sh."""
import io
from pathlib import Path
import tempfile
import unittest
from types import SimpleNamespace
from unittest import mock

from tools import postgres_dev, start_all_windows
from tools.postgres_dev import MISSING_CATALOG_MESSAGE, NO_STARTERS_MESSAGE, ensure_game_data


class FakeSql:
    def __init__(self, *, builds=0, enabled=0, build_id='build-1'):
        self.builds = builds
        self.enabled = enabled
        self.build_id = build_id
        self.queries = []

    def __call__(self, text, **kwargs):
        self.queries.append(text)
        if 'FROM catalog.builds;' in text and 'count(*)' in text:
            return SimpleNamespace(stdout=f'{self.builds}\n')
        if 'FROM game.starter_profiles' in text:
            return SimpleNamespace(stdout=f'{self.enabled}\n')
        if 'FROM catalog.builds ORDER BY imported_at' in text:
            return SimpleNamespace(stdout=f'{self.build_id}\n')
        raise AssertionError(f'unexpected sql: {text}')


def catalog_dir(root):
    path = Path(root) / 'game-catalog-v2'
    path.mkdir()
    (path / 'manifest.json').write_text('{}', encoding='utf-8')
    (path / 'entries.jsonl').write_text('', encoding='utf-8')
    return path


class EnsureGameDataTests(unittest.TestCase):
    def test_missing_catalog_warns_and_skips_tools(self):
        sql = FakeSql(builds=0)
        runner = mock.Mock()
        with tempfile.TemporaryDirectory() as temp, mock.patch.object(postgres_dev, 'sql', sql), \
                mock.patch.object(postgres_dev, 'import_catalog') as imported:
            log = io.StringIO()
            result = ensure_game_data(Path(temp) / 'missing', log=log.write, runner=runner)
        self.assertEqual(result, {'imported': False, 'configured': False, 'enabled': 0, 'build_id': None})
        imported.assert_not_called()
        runner.assert_not_called()
        message = log.getvalue()
        self.assertIn('profile_unavailable', message)
        self.assertIn('python tools/game_catalog.py build', message)
        self.assertIn('WorldProbe', message)
        self.assertIn('aion-server-data.tar.gz', message)
        self.assertEqual(MISSING_CATALOG_MESSAGE, message)

    def test_empty_builds_imports_then_configures(self):
        sql = FakeSql(builds=0, enabled=0)
        calls = []

        def import_catalog(directory):
            calls.append(('import', Path(directory)))
            sql.builds = 1

        def runner(command, **kwargs):
            calls.append(tuple(Path(part).name if isinstance(part, str) and part.endswith('.py') else part
                               for part in command))
            sql.enabled = 8
            return SimpleNamespace(returncode=0, stdout=f'ok {command[-3] if len(command) >= 3 else command[-1]}\n', stderr='')

        with tempfile.TemporaryDirectory() as temp, mock.patch.object(postgres_dev, 'sql', sql), \
                mock.patch.object(postgres_dev, 'import_catalog', import_catalog):
            directory = catalog_dir(temp)
            log = io.StringIO()
            result = ensure_game_data(directory, log=log.write, runner=runner)
        self.assertEqual(result['imported'], True)
        self.assertEqual(result['configured'], True)
        self.assertEqual(result['enabled'], 8)
        self.assertEqual(result['build_id'], 'build-1')
        self.assertEqual(calls[0], ('import', directory))
        scripts = [c[1] for c in calls[1:]]
        self.assertEqual(scripts, ['configure_starters.py', 'class_skills.py', 'equipment_rules.py'])
        self.assertIn('--build-id', calls[1])
        self.assertIn('build-1', calls[1])
        self.assertIn('install', calls[2])
        self.assertIn('Starter profiles ready: 8 enabled.', log.getvalue())

    def test_existing_catalog_without_profiles_configures_without_import(self):
        sql = FakeSql(builds=1, enabled=0)
        runner = mock.Mock(return_value=SimpleNamespace(returncode=0, stdout='', stderr=''))

        def after_tools(*args, **kwargs):
            sql.enabled = 4
            return SimpleNamespace(returncode=0, stdout='', stderr='')

        runner.side_effect = after_tools
        with tempfile.TemporaryDirectory() as temp, mock.patch.object(postgres_dev, 'sql', sql), \
                mock.patch.object(postgres_dev, 'import_catalog') as imported:
            result = ensure_game_data(catalog_dir(temp), log=lambda *_: None, runner=runner)
        imported.assert_not_called()
        self.assertEqual(runner.call_count, 3)
        self.assertTrue(result['configured'])
        self.assertFalse(result['imported'])
        self.assertEqual(result['enabled'], 4)

    def test_existing_profiles_skipped_unless_forced(self):
        sql = FakeSql(builds=1, enabled=8)
        runner = mock.Mock(return_value=SimpleNamespace(returncode=0, stdout='', stderr=''))
        with tempfile.TemporaryDirectory() as temp, mock.patch.object(postgres_dev, 'sql', sql), \
                mock.patch.object(postgres_dev, 'import_catalog') as imported:
            directory = catalog_dir(temp)
            skipped = ensure_game_data(directory, log=lambda *_: None, runner=runner)
            forced = ensure_game_data(directory, log=lambda *_: None, runner=runner, force=True)
        imported.assert_not_called()
        self.assertFalse(skipped['configured'])
        self.assertTrue(forced['configured'])
        self.assertEqual(runner.call_count, 3)
        self.assertEqual(skipped['enabled'], 8)
        self.assertEqual(forced['enabled'], 8)

    def test_tools_leave_no_enabled_profiles_warns(self):
        sql = FakeSql(builds=1, enabled=0)
        runner = mock.Mock(return_value=SimpleNamespace(returncode=0, stdout='', stderr=''))
        with tempfile.TemporaryDirectory() as temp, mock.patch.object(postgres_dev, 'sql', sql), \
                mock.patch.object(postgres_dev, 'import_catalog'):
            log = io.StringIO()
            result = ensure_game_data(catalog_dir(temp), log=log.write, runner=runner)
        self.assertEqual(result['enabled'], 0)
        self.assertIn('profile_unavailable', log.getvalue())
        self.assertIn(NO_STARTERS_MESSAGE, log.getvalue())

    def test_tool_failure_raises(self):
        sql = FakeSql(builds=1, enabled=0)
        runner = mock.Mock(return_value=SimpleNamespace(returncode=1, stdout='', stderr='boom'))
        with tempfile.TemporaryDirectory() as temp, mock.patch.object(postgres_dev, 'sql', sql), \
                mock.patch.object(postgres_dev, 'import_catalog'), self.assertRaises(RuntimeError) as raised:
            ensure_game_data(catalog_dir(temp), log=lambda *_: None, runner=runner)
        self.assertIn('configure_starters.py failed: boom', str(raised.exception))


class StartAllWindowsTests(unittest.TestCase):
    def test_postgres_up_reapplies_starters_after_migrate(self):
        source = Path(start_all_windows.__file__).read_text(encoding='utf-8')
        migrate_at = source.find('def postgres_up')
        self.assertNotEqual(migrate_at, -1)
        ensure_at = source.find('ensure_game_data', migrate_at)
        self.assertIn('postgres_dev.ensure_game_data(log=_msg, force=True)', source[migrate_at:ensure_at + 80])
        self.assertLess(source.find('migrate()', migrate_at), ensure_at)


if __name__ == '__main__':
    unittest.main()
