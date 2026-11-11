"""tools/boss_phases.py (ported from ckrealm/A2Core c52fc1a tests/test_boss_phases.py, AGPL-3.0; client-data annotations ours)."""
import json
import unittest

from tools import boss_phases


class BossPhaseTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.events, cls.world = boss_phases.load()
        cls.built = boss_phases.annotate(boss_phases.build(cls.events, cls.world), boss_phases.client_areas(), cls.world['npc_skills'])
        cls.tracked = json.loads(boss_phases.OUT.read_text(encoding='utf-8'))

    def slams(self):
        for npc, script in self.tracked['bosses'].items():
            for slam in [script.get('telegraph')] + [p.get('telegraph') for p in script.get('phases', [])]:
                if slam:
                    yield npc, slam

    def test_tracked_file_is_current(self):
        self.assertEqual(boss_phases.OUT.read_text(encoding='utf-8'), boss_phases.text(self.built), 'run python tools/boss_phases.py')

    def test_valid(self):
        self.assertEqual(boss_phases.validate(self.tracked, self.world), [])

    def test_every_season_one_final_boss_is_scripted(self):
        finals = {str(room['cube_boss']) for room in self.events['dungeons'].values()
                  if room.get('cube_boss') and any(room['name'].startswith(f + '_') for f in boss_phases.EXPEDITIONS)
                  and room['name'].rsplit('_', 1)[1] in boss_phases.MODE}
        self.assertEqual(len(finals), 18)
        for npc in finals:
            with self.subTest(npc):
                self.assertEqual(self.tracked['bosses'][npc]['role'], 'final')

    def test_frenzy_only_on_hard_modes(self):
        for npc, script in self.tracked['bosses'].items():
            with self.subTest(npc):
                hard = script['dungeon'].endswith('_Hard') and script['role'] == 'final'
                self.assertEqual('frenzy' in script, hard)
                if hard:
                    self.assertEqual(script['frenzy']['after_ms'], 240000)

    def test_slams_use_the_bosses_own_long_casts(self):
        krao = self.tracked['bosses']['2320171']['telegraph']
        self.assertEqual((krao['skill'], krao['wind_up_ms'], krao['wind_up_from']), (1800140, 8000, 'client cast'))
        durbati = self.tracked['bosses']['2301014']['telegraph']
        self.assertEqual((durbati['wind_up_ms'], durbati['wind_up_from']), (boss_phases.FALLBACK_WIND_UP_MS, 'OURS'))

    def test_every_slam_says_where_its_radius_and_wind_up_come_from(self):
        areas = boss_phases.client_areas()
        for npc, slam in self.slams():
            with self.subTest(npc=npc, skill=slam['skill']):
                self.assertIn(slam['radius_from'], ('skill-areas', 'OURS'))
                self.assertIn(slam['wind_up_from'], ('client cast', 'OURS'))
                self.assertEqual(slam['radius_from'] == 'skill-areas', slam['skill'] in areas)

    def test_every_notice_has_a_russian_text(self):
        for npc, script in self.tracked['bosses'].items():
            for item in [script.get('frenzy')] + script.get('phases', []):
                if item and item.get('notice'):
                    with self.subTest(npc):
                        self.assertTrue(item.get('notice_ru'))
        for npc, slam in self.slams():
            if slam.get('warning'):
                self.assertTrue(slam.get('warning_ru'))

    def test_raid_waves_are_named_spawns_and_horn_den_is_hand_made(self):
        self.assertEqual(sorted(self.tracked['waves']), ['FierceHornCave_Easy_Wave_01', 'FierceHornCave_Hard_Wave_01', 'FierceHornCave_Wave_01'])
        names = {s[0] for s in self.world['maps']['600082']}
        self.assertIn('ReforgedAbyss_1PartyWave_09_2', names)


if __name__ == '__main__':
    unittest.main()
