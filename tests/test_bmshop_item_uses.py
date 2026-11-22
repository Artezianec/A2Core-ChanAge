import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class BmshopItemUsesTests(unittest.TestCase):
    def test_midnight_slasher_skin_set_has_get_skin_set_use(self):
        uses = json.loads((ROOT / 'tools/data/boxes.json').read_text(encoding='utf-8'))['uses']
        self.assertEqual(uses['535510221'][:3], ['GetSkinSet', 1, 'AR_221'])
        boxes = json.loads((ROOT / 'tools/data/boxes.json').read_text(encoding='utf-8'))['boxes']
        self.assertIn('G_BM_Box_Package_WP_004_b', boxes)

    def test_merge_bmshop_adds_skin_set_without_catalog(self):
        from tools.bmshop_item_uses import infer_use

        self.assertEqual(infer_use(535510221, 'BM_Skinshop_SkinSet_AR_221_b', {}), ['GetSkinSet', 1, 'AR_221'])
        self.assertEqual(
            infer_use(503700094, 'G_BM_Box_Package_WP_004_b', {})[:2],
            ['RewardBox', 1],
        )


if __name__ == '__main__':
    unittest.main()