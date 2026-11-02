import json
import unittest
from pathlib import Path

from tools import loot_data


class LootDataEnvMappingTests(unittest.TestCase):
    def test_godfragment_envs_match_object_names(self):
        loot = json.loads(Path('tools/data/loot.json').read_text(encoding='utf-8'))
        envs = loot.get('envs', {})
        self.assertEqual(['reward_manual_E_L1_HQ_GodFragment_01'], envs['1201290'])
        self.assertEqual(['reward_manual_E_D1_HQ_GodFragment_02_001'], envs['1301134'])
        l1 = loot['tables']['reward_manual_E_L1_HQ_GodFragment_01']['items'][0][0]
        d1 = loot['tables']['reward_manual_E_D1_HQ_GodFragment_02_001']['items'][0][0]
        self.assertEqual(631900001, l1)
        self.assertEqual(631900007, d1)

    def test_build_env_loot_from_world_objects(self):
        tables = {'reward_manual_E_L1_HQ_GodFragment_01': {'items': [[1, 1, 1, 1]]}}
        envs = loot_data.build_env_loot(tables)
        self.assertEqual(['reward_manual_E_L1_HQ_GodFragment_01'], envs['1201290'])

    def test_apply_env_patches_keeps_rewarddata_envs_and_adds_s1_treasure(self):
        data = json.loads(Path('tools/data/loot.json').read_text(encoding='utf-8'))
        before = len(data.get('envs', {}))
        data = loot_data.apply_env_patches(data)
        self.assertGreaterEqual(len(data['envs']), before)
        self.assertIn('1201290', data['envs'])
        self.assertIn('1100130', data['envs'])
        self.assertIn('reward_manual_FieldEvent_L_TreasureBox_1100130', data['tables'])


if __name__ == '__main__':
    unittest.main()
