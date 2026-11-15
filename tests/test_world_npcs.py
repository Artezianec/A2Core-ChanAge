import unittest

from tools import combat, world_npcs


class WorldNpcTests(unittest.TestCase):
    def test_poeta_spawns_are_present_and_keyed_after_the_dummy(self):
        targets = world_npcs.map_targets(1000, combat.Target)
        self.assertEqual(len(targets), 274)
        self.assertEqual(min(targets), world_npcs.FIRST_KEY)
        self.assertNotIn(combat.TRAINING_DUMMY_KEY, targets)
        self.assertEqual(world_npcs.map_targets(9999, combat.Target), {})

    def test_hp_follows_official_values_and_interpolates(self):
        for level, hp in world_npcs.HP_BY_LEVEL.items():
            self.assertEqual(world_npcs.hp_for_level(level), hp)
        self.assertEqual(world_npcs.hp_for_level(6), 532)    # between 5 (399) and 7 (665)
        self.assertGreater(world_npcs.hp_for_level(40), world_npcs.hp_for_level(30))
        boss = next(t for t in world_npcs.map_targets(210016, combat.Target).values() if t.npc_id == 2701034)
        self.assertEqual(boss.hp_max, 2100)   # CANDIDATE hero unit 700 (was OBSERVED 1000×3 at level 5)
        self.assertEqual(world_npcs.hp_for(6, 'EliteMonster', 1), 2400)
        self.assertEqual((world_npcs.hp_for(7, 'NormalMonster', 1), world_npcs.hp_for(4, 'NormalMonster', 2), world_npcs.hp_for(8)), (250, 150, 798))

    def test_appear_bytes_follow_npc_data(self):
        targets = world_npcs.map_targets(1000, combat.Target).values()
        citizen = next(t for t in targets if t.relationship == 3)          # NPC_Light
        aggressive = next(t for t in targets if t.aggressive)              # AggroType Aggro_A
        citizen_body = combat.build_npc_appear(citizen)
        # Tail: relationship, 00, 01, 00, level i32, spawn tag u32, 00.
        self.assertEqual(citizen_body[-13:-9], bytes([3, 0, 1, 0]))
        mask_at = 2 + len(combat.encode_varint(aggressive.key))
        self.assertEqual(combat.build_npc_appear(aggressive)[mask_at:mask_at + 2], b'\x0c\x20')  # 0x200c
        self.assertIn(combat.encode_varint(citizen.hp_max) * 2, citizen_body)


class UntouchableTests(unittest.TestCase):
    def test_npcdata_attack_and_target_flags(self):
        # NpcData bCanBeAttacked false or NpcTargetAble not Able: players cannot hit it.
        self.assertFalse(world_npcs.untouchable({'bCanBeAttacked': True, 'NpcTargetAble': 'ENpcTargetAble::Able'}))
        self.assertTrue(world_npcs.untouchable({'bCanBeAttacked': False, 'NpcTargetAble': 'ENpcTargetAble::Able'}))
        self.assertTrue(world_npcs.untouchable({'bCanBeAttacked': True, 'NpcTargetAble': 'ENpcTargetAble::Unable'}))
        self.assertTrue(world_npcs.untouchable({'bCanBeAttacked': True, 'NpcTargetAble': 'ENpcTargetAble::Onlypicking'}))

    def test_hit_count_npcs_take_one_hp_per_hit(self):
        self.assertEqual(world_npcs.hit_count({'bHitCountBased': False}), {})
        self.assertEqual(world_npcs.hit_count({'bHitCountBased': True, 'NpcType': 'ENpcType::Monster'}),
                         {'hp_max': 7, 'hit_count': True})
        self.assertEqual(world_npcs.hit_count({'bHitCountBased': True, 'NpcType': 'ENpcType::Summon'}),
                         {'hp_max': 5, 'hit_count': True})

    def test_dra_totem_template_is_seven_hits(self):
        import json
        from pathlib import Path
        data = json.loads(Path('tools/data/world-npcs.json').read_text(encoding='utf-8'))
        totem = data['templates']['2290017']
        self.assertTrue(totem['hit_count'])
        self.assertEqual(totem['hp_max'], 7)
        self.assertEqual(sum(1 for t in data['templates'].values() if t.get('hit_count')), 242)

    def test_world_npcs_templates_mark_untouchable(self):
        from pathlib import Path
        import json
        data = json.loads(Path('tools/data/world-npcs.json').read_text(encoding='utf-8'))
        self.assertGreater(sum(1 for t in data['templates'].values() if t.get('untouchable')), 2000)


if __name__ == '__main__':
    unittest.main()
