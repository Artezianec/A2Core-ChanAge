import json
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))

import spawn_respawn  # noqa: E402


class SpawnRespawnExportTests(unittest.TestCase):
    def test_respawn_ms_seconds_and_ms(self):
        self.assertEqual(spawn_respawn._to_ms(12), 12_000)
        self.assertEqual(spawn_respawn._to_ms(45_000), 45_000)
        self.assertIsNone(spawn_respawn._to_ms(0))

    def test_spawn_actor_props(self):
        self.assertEqual(spawn_respawn.respawn_ms_from_props({"RespawnTime": 15}), 15_000)
        self.assertEqual(spawn_respawn.respawn_ms_from_props({"DespawnDelayMS": 8000}), 8000)

    def test_build_notes_missing_probe(self):
        with tempfile.TemporaryDirectory() as tmp:
            data = Path(tmp) / "spawn-respawn.json"
            old_data = spawn_respawn.DATA
            spawn_respawn.DATA = data
            try:
                payload = spawn_respawn.build()
                self.assertTrue(any("spawn-actors" in x for x in payload["limitations"]))
            finally:
                spawn_respawn.DATA = old_data


if __name__ == "__main__":
    unittest.main()