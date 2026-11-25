import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ZONES = ROOT / "tools/data/pc-zones.json"


class PcZonesTests(unittest.TestCase):
    def test_bittercold_wind_uses_client_export_damage(self):
        zone = json.loads(ZONES.read_text(encoding="utf-8"))["zones"]["15280000"]
        self.assertEqual("Bittercold Wind", zone["name"])
        self.assertEqual(10_000, zone["lifetime_ms"])
        self.assertEqual(2920011, zone["npc"])
        dmg = zone["damage"]
        self.assertEqual([238, 238], dmg["flat"])
        self.assertEqual([23225, 23225], dmg["ratio"])
        self.assertEqual("7", dmg["value4"])
        self.assertEqual(1528000211, dmg["effect_id"])


if __name__ == "__main__":
    unittest.main()