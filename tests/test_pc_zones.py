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
        self.assertEqual("Sorcerer_Skill025_LvUp", dmg["level_group"])
        cast = zone["cast_effects"]
        self.assertEqual(3, len(cast))
        self.assertEqual(1528000011, cast[0]["effect_id"])
        self.assertEqual("self", cast[0]["target"])
        self.assertEqual(1528000021, cast[1]["effect_id"])
        self.assertEqual("marker", cast[1]["target"])
        self.assertEqual(1528000011, cast[2]["effect_id"])
        self.assertEqual("marker", cast[2]["target"])

    def test_divine_aura_uses_client_export_pulse_rows(self):
        zone = json.loads(ZONES.read_text(encoding="utf-8"))["zones"]["17150000"]
        self.assertEqual("Divine Aura", zone["name"])
        self.assertEqual(9_500, zone["lifetime_ms"])
        self.assertEqual(800, zone["tick_ms"])
        self.assertEqual([171500031, 171500041], zone["ally_abnormals"])
        dmg = zone["damage"]
        self.assertEqual([394, 394], dmg["flat"])
        self.assertEqual(1715000211, dmg["effect_id"])


if __name__ == "__main__":
    unittest.main()