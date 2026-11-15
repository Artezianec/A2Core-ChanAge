"""Leftover NCGuard stop files must not shut down the next start-all."""
from __future__ import annotations

import os
import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock

from tools import start_all_windows, stop_file


class StopFileHelperTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / 'ncguard-stop'

    def test_clear_stale_deletes_file_older_than_start(self):
        self.path.touch()
        older = time.time() - 60
        os.utime(self.path, (older, older))
        self.assertTrue(stop_file.clear_stale_stop_file(self.path, started_at=time.time()))
        self.assertFalse(self.path.exists())

    def test_clear_stale_keeps_file_written_after_start(self):
        started = time.time() - 5
        self.path.touch()
        now = time.time()
        os.utime(self.path, (now, now))
        self.assertFalse(stop_file.clear_stale_stop_file(self.path, started_at=started))
        self.assertTrue(self.path.exists())
        self.assertTrue(stop_file.is_stop_requested(self.path, started))

    def test_clear_stale_missing_is_ok(self):
        self.assertFalse(stop_file.clear_stale_stop_file(self.path, started_at=time.time()))
        self.assertFalse(stop_file.is_stop_requested(self.path, time.time()))
        stop_file.clear_stop_file(self.path)
        stop_file.clear_stop_file(None)

    def test_clear_stop_file_removes_unconditionally(self):
        self.path.touch()
        stop_file.clear_stop_file(self.path)
        self.assertFalse(self.path.exists())

    def test_loopback_uses_same_stale_helpers(self):
        source = Path(stop_file.__file__).with_name('loopback_tls_mitm.py').read_text(encoding='utf-8')
        self.assertIn('clear_stale_stop_file(args.stop_file', source)
        self.assertIn('is_stop_requested(args.stop_file, started_at)', source)


class StartAllStopFileTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.runtime = Path(self.temp.name)
        self.stop = self.runtime / 'ncguard-stop'
        self.fixture = self.runtime / 'fixture.json'
        self.fixture.write_text('{}', encoding='utf-8')

    def test_ncguard_host_env_deletes_stale_stop_file(self):
        self.stop.touch()
        env = start_all_windows.ncguard_host_env(self.runtime, self.fixture)
        self.assertFalse(self.stop.exists())
        self.assertEqual(Path(env['Aion__NcGuard__StopFile']), self.stop.resolve())

    def test_launch_ncguard_clears_stop_file_before_elevated_start(self):
        self.stop.touch()
        dll = self.runtime / 'Aion.Host.dll'
        dll.write_bytes(b'')
        seen = []

        def fake_start(host_dll, env, directory):
            seen.append(Path(env['Aion__NcGuard__StopFile']).exists())

        with mock.patch.object(start_all_windows, 'find_ncguard_fixture', return_value=self.fixture), \
                mock.patch.object(start_all_windows, 'HOST_DLL', dll), \
                mock.patch.object(start_all_windows, 'wait_ports', return_value=0), \
                mock.patch.dict('sys.modules', {'elevated_dotnet_host': mock.Mock(start_elevated_host=fake_start)}):
            code = start_all_windows.launch_ncguard(self.runtime)
        self.assertEqual(code, 0)
        self.assertEqual(seen, [False])
        self.assertFalse(self.stop.exists())

    def test_stop_listeners_removes_stop_file_after_port_closes(self):
        with mock.patch.object(start_all_windows, 'RUNTIME', self.runtime), \
                mock.patch.object(start_all_windows, '_kill_port_listeners') as killed:
            start_all_windows.stop_listeners({'ncguard_port': 1}, stop_wait=1.0)
        killed.assert_any_call(1)
        self.assertFalse(self.stop.exists())

    def test_stop_listeners_still_removes_file_if_port_stays_open(self):
        with mock.patch.object(start_all_windows, 'RUNTIME', self.runtime), \
                mock.patch.object(start_all_windows, '_kill_port_listeners'), \
                mock.patch.object(start_all_windows, 'wait_port_closed', return_value=False), \
                mock.patch.object(start_all_windows, '_msg') as msg:
            start_all_windows.stop_listeners({'ncguard_port': 443}, stop_wait=0.5)
        self.assertFalse(self.stop.exists())
        self.assertTrue(any('still in use' in str(call) for call in msg.call_args_list))

    def test_wait_port_closed_times_out_while_open(self):
        with mock.patch.object(start_all_windows, '_port_is_open', return_value=True):
            self.assertFalse(start_all_windows.wait_port_closed(443, timeout=0.3))

    def test_wait_port_closed_when_free(self):
        self.assertTrue(start_all_windows.wait_port_closed(1, timeout=1.0))


if __name__ == '__main__':
    unittest.main()
