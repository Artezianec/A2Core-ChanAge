"""Client export drives BM shop item uses, skins, and vehicle ids."""
import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class ClientExportShopDataTests(unittest.TestCase):
    def test_bm_vehicle_items_use_client_ride_ids(self):
        uses = json.loads((ROOT / 'tools/data/boxes.json').read_text(encoding='utf-8'))['uses']
        self.assertEqual(uses['534760255'], ['GetVehicle', 1, '1245'])
        self.assertEqual(uses['534760257'], ['GetVehicle', 1, '1250'])
        self.assertEqual(uses['534760341'], ['GetVehicle', 1, '1363'])
        self.assertEqual(uses['534760342'], ['GetVehicle', 1, '1343'])
        rides = json.loads((ROOT / 'tools/data/boxes.json').read_text(encoding='utf-8'))['rides']
        self.assertNotIn('1300', rides)
        self.assertIn('1245', rides)
        self.assertEqual(rides['1245']['item'], 534760255)

    def test_skin_set_midnight_slasher_from_export(self):
        uses = json.loads((ROOT / 'tools/data/boxes.json').read_text(encoding='utf-8'))['uses']
        skins = json.loads((ROOT / 'tools/data/skins.json').read_text(encoding='utf-8'))['skins']
        self.assertEqual(uses['535510221'][:3], ['GetSkinSet', 1, 'AR_Apron_02'])
        sets = json.loads((ROOT / 'tools/data/skins.json').read_text(encoding='utf-8'))['sets']
        self.assertIn('AR_Apron_02', sets)

    def test_packages_include_reward_package_item_export(self):
        packages = json.loads((ROOT / 'tools/data/boxes.json').read_text(encoding='utf-8'))['packages']
        self.assertGreater(len(packages), 40_000)
        self.assertIn('PKG_Box_MagicStone_A_c_01a_b', packages)

    def test_bmshop_goods_match_export_row_count(self):
        from tools.client_export import load_rows

        committed = json.loads((ROOT / 'tools/data/bmshop_goods.json').read_text(encoding='utf-8'))
        self.assertEqual(len(committed['goods']), len(load_rows('BMShopGoods')))


if __name__ == '__main__':
    unittest.main()