import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))

import ludra_arkanis_data  # noqa: E402

DATA = ROOT / 'tools/data'


class LudraArkanisDataTests(unittest.TestCase):
    def test_catalogs_are_up_to_date(self):
        self.assertEqual(0, ludra_arkanis_data.main(['--check']))

    def test_boss_kit_skills_have_damage_rows(self):
        skills = json.loads((DATA / 'world-npcs.json').read_text(encoding='utf-8'))['npc_skills']
        for skill in ('1802760', '1801840', '1803140', '1606010', '1606440', '1801640'):
            self.assertGreater(skills[skill]['effect'], 0, skill)

    def test_ratio_rows_follow_the_kit_skills(self):
        ratio = json.loads((DATA / 'skill-effect-engine.json').read_text(encoding='utf-8'))['ratio_damage']
        self.assertEqual(7000, ratio['1803203']['ratio'])
        self.assertEqual(8000, ratio['1801943']['ratio'])

    def test_ludra_hard_map_is_derived_from_normal_with_hard_ids(self):
        maps = json.loads((DATA / 'world-npcs.json').read_text(encoding='utf-8'))['maps']
        normal, hard = maps['600082'], maps['600083']
        self.assertEqual(len(normal), len(hard))
        ids = {row[1] for row in hard}
        self.assertTrue({2300282, 2300283, 2300290} <= ids)
        self.assertFalse({2301006, 2301007, 2301014} & ids)

    def test_encounter_files_cover_every_boss_once(self):
        seen = {}
        for path in sorted((DATA / 'boss-mechanics').glob('*.json')):
            for ids in json.loads(path.read_text(encoding='utf-8'))['npcs'].values():
                for npc in ids:
                    self.assertNotIn(npc, seen, f'{npc} in {path.name} and {seen.get(npc)}')
                    seen[npc] = path.name
        for npc in (2301006, 2300282, 2301007, 2300283, 2301014, 2300290,
                    2330707, 2350708, 2353709, 2356709):
            self.assertIn(npc, seen)


if __name__ == '__main__':
    unittest.main()
