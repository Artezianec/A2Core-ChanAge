"""tools/data/bot-rotations.json (ported from ckrealm/A2Core) against our class data (docs/PLAYERBOTS.md)."""
import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ROTATIONS = ROOT / 'tools/data/bot-rotations.json'
CLASS_SKILLS = ROOT / 'tools/data/class-skills.json'
STIGMAS = ROOT / 'tools/data/stigmas.json'

TARGETS = {'enemy', 'self', 'leader', 'ally', 'tank', 'dead_ally'}
ROLES = {'tank', 'dps', 'heal'}
CONDITIONS = {
    'target', 'in_combat', 'buff_missing', 'self_hp_below', 'self_mp_above', 'self_mp_below', 'target_hp_below',
    'target_hp_above', 'target_casting', 'target_stunned', 'target_slowed', 'target_elite', 'target_boss', 'target_on_self',
    'target_effect_missing', 'target_has', 'target_distance_above', 'enemies_at_least', 'after', 'after_within_ms',
    'after_critical', 'after_block', 'after_evasion', 'after_dodge',
}


class BotRotationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.rotations = json.loads(ROTATIONS.read_text(encoding='utf-8'))
        cls.classes = json.loads(CLASS_SKILLS.read_text(encoding='utf-8'))['classes']
        cls.stigmas = json.loads(STIGMAS.read_text(encoding='utf-8'))['generated']['classes']

    def playable(self):
        return [name for name in self.rotations['classes'] if name in self.classes]

    def test_header(self):
        self.assertGreater(self.rotations['global_cooldown_ms'], 0)
        self.assertGreater(self.rotations['tank_hate_multiplier'], 1)
        self.assertGreaterEqual(len(self.playable()), 8)

    def test_every_step_is_a_kit_skill_at_its_unlock_level_or_a_class_stigma(self):
        for name in self.playable():
            unlocks = {u[0]: u[1] for u in self.classes[name]['unlocks']}
            stigmas = set(self.stigmas.get(name, []))
            for step in self.rotations['classes'][name]['rotation']:
                with self.subTest(name=name, skill=step['skill']):
                    if step['skill'] in unlocks:
                        self.assertEqual(step['level'], unlocks[step['skill']])
                    else:
                        self.assertIn(step['skill'], stigmas)

    def test_conditions_roles_and_areas_are_known(self):
        for name, entry in self.rotations['classes'].items():
            with self.subTest(name):
                self.assertGreater(entry['range'], 0)
                self.assertTrue(set(entry['roles']) <= ROLES)
                for step in entry['rotation']:
                    when = step.get('when', {'target': 'enemy'})
                    self.assertIn(when['target'], TARGETS)
                    self.assertTrue(set(when) <= CONDITIONS, set(when) - CONDITIONS)
                    self.assertTrue(set(step.get('roles', [])) <= ROLES)
                    self.assertIn(step.get('area'), (None, 'self', 'target'))

    def test_every_rotation_ends_on_an_unconditional_level_one_attack(self):
        for name in self.playable():
            last = self.rotations['classes'][name]['rotation'][-1]
            with self.subTest(name):
                self.assertEqual(last['when'], {'target': 'enemy'})
                self.assertEqual(last['level'], 1)


if __name__ == '__main__':
    unittest.main()
