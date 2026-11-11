import json
import os
import unittest
from pathlib import Path

from tools import stigmas

DATA = json.loads(stigmas.DATA.read_text(encoding='utf-8'))


class StigmaDataTests(unittest.TestCase):
    def test_every_class_has_13_stigmas_with_a_definition(self):
        classes = DATA['generated']['classes']
        self.assertEqual(9, len(classes))
        for cls, ids in classes.items():
            with self.subTest(cls):
                self.assertEqual(13, len(ids))
                for skill in ids:
                    self.assertIn(str(skill), DATA['generated']['skills'])

    def test_no_stigma_is_in_a_class_level_kit(self):
        kit = json.loads((stigmas.ROOT / 'tools/data/class-skills.json').read_text(encoding='utf-8'))['classes']
        for cls, ids in DATA['generated']['classes'].items():
            unlocks = {u[0] for u in kit.get(cls, {}).get('unlocks', [])}
            self.assertFalse(unlocks & set(ids), cls)

    def test_cost_ladder_is_75_shards_to_20(self):
        costs = DATA['params']['costs']
        total = sum((band['to'] - band['from'] + 1) * band['shards'] for band in costs)
        self.assertEqual(75, total)
        self.assertEqual(20, DATA['params']['max_skill_level'])

    def test_self_stigmas_have_no_borrowed_damage(self):
        for skill_id, skill in DATA['generated']['skills'].items():
            if skill['target'] != 'MainTarget':
                self.assertEqual([], skill['damage'], skill_id)

    @unittest.skipUnless(Path(os.environ.get('AION_CATALOG', stigmas.DEFAULT_CATALOG)).exists(), 'needs game-catalog-v2 entries.jsonl')
    def test_matches_the_client_catalogue(self):
        self.assertEqual([], stigmas.check(Path(os.environ.get('AION_CATALOG', stigmas.DEFAULT_CATALOG))))


if __name__ == '__main__':
    unittest.main()
