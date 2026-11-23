"""tools/boss_mechanics_validate.py: the tracked encounters pass; broken ones fail with a readable error."""
import json
import unittest

from tools import boss_mechanics_validate as v


def encounter(rules, **extra):
    enc = {"id": "t", "npcs": {"Normal": [2300171]}, "rules": rules}
    enc.update(extra)
    return enc


def rule(*actions, when=None):
    return {"id": "r", "when": when or {"type": "fight_start"}, "do": list(actions)}


class BossMechanicsValidateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.data = v.ClientData.load()
        cls.validator = v.Validator(cls.data)

    def errors(self, enc):
        return self.validator.encounter(enc, "t.json").errors

    def assertError(self, enc, fragment):
        errors = self.errors(enc)
        self.assertTrue(any(fragment in e for e in errors), f"no error containing {fragment!r} in {errors}")

    def test_tracked_encounters_are_valid(self):
        reports = v.validate_all(data=self.data)
        self.assertGreaterEqual(len(reports), 5)
        for report in reports:
            with self.subTest(report.file):
                self.assertEqual(report.errors, [])

    def test_tracked_encounters_match_the_schema(self):
        try:
            import jsonschema
        except ImportError:
            self.skipTest("jsonschema not installed")
        schema = json.loads(v.SCHEMA.read_text(encoding="utf-8"))
        for path in sorted(v.ENCOUNTERS.glob("*.json")):
            with self.subTest(path.name):
                jsonschema.validate(json.loads(path.read_text(encoding="utf-8")), schema)

    def test_minimal_encounter_is_valid(self):
        self.assertEqual(self.errors(encounter([rule({"type": "notice", "text": "hi"})])), [])

    def test_unknown_names(self):
        self.assertError(encounter([rule(when={"type": "hp_sideways"})]), "unknown trigger")
        self.assertError(encounter([rule({"type": "explode"})]), "unknown action")
        self.assertError(encounter([rule({"type": "zone", "shape": "hexagon"})]), "unknown shape")
        self.assertError(encounter([rule({"type": "telegraph", "shape": "circle", "at": "the_moon"})]), "unknown anchor")
        self.assertError(encounter([rule({"type": "telegraph", "shape": "circle", "at": "mark"})]), "needs a name")
        self.assertError(encounter([], npcs={"Medium": [2300171]}), "unknown difficulty")
        self.assertError(encounter([], colour=3), "unknown top-level field")

    def test_required_fields(self):
        self.assertError(encounter([rule(when={"type": "hp_below"})]), "needs 'pct'")
        self.assertError(encounter([rule({"type": "cast"})]), "needs 'skill'")
        self.assertError(encounter([rule({"type": "shield", "id": "s"})]), "invuln or hp or hp_pct or until")
        self.assertError(encounter([rule({"type": "repeat", "every_ms": 100, "do": []})]), "count or duration_ms or ends")
        self.assertError(encounter([rule({"type": "displace", "mode": "yeet"})]), "displace mode")

    def test_summon_npc_zero_means_the_boss_itself(self):
        self.assertEqual(self.errors(encounter([rule({"type": "summon", "npc": 0, "count": 2})])), [])

    def test_var_references(self):
        self.assertError(encounter([rule({"type": "zone", "shape": "circle", "radius": "$nope"})]), "unknown var '$nope'")
        ok = encounter([rule({"type": "zone", "shape": "circle", "radius": "$r"})], difficulty={"Hard": {"vars": {"r": 5}}})
        self.assertEqual(self.errors(ok), [])

    def test_client_ids_must_exist(self):
        self.assertError(encounter([rule({"type": "cast", "skill": 999999999})]), "skill 999999999")
        self.assertError(encounter([rule({"type": "telegraph", "shape": "circle", "damage": {"skill": 999999998}})]), "skill 999999998")
        self.assertError(encounter([rule({"type": "summon", "npc": 123})]), "npc 123")
        self.assertError(encounter([rule({"type": "env", "env_obj": 42})]), "env_obj 42")
        self.assertError(encounter([rule({"type": "caption", "talk": "NO_SUCH_TALK"})]), "NO_SUCH_TALK")
        self.assertError(encounter([], npcs={"Normal": [1]}), "1 is not in world-npcs")
        self.assertError(encounter([], difficulty={"Hard": {"skill_remap": {"1800040": 999999997}}}), "skill 999999997")

    def test_rules_remove_and_rotation(self):
        self.assertError(encounter([], difficulty={"Hard": {"rules_remove": ["ghost"]}}), "unknown rule 'ghost'")
        self.assertError(encounter([], rotation=[{"type": "notice", "text": "x"}]), "only cast actions may rotate")

    def test_nested_actions_are_checked(self):
        cast = {"type": "cast", "skill": 1800040, "during": [{"type": "zone"}], "on_land": [{"type": "notice"}]}
        errors = self.errors(encounter([rule(cast)]))
        self.assertTrue(any("during" in e and "shape or grid" in e for e in errors), errors)
        self.assertTrue(any("on_land" in e and "text" in e for e in errors), errors)

    def test_duplicate_npcs_across_files(self):
        import tempfile
        from pathlib import Path
        with tempfile.TemporaryDirectory() as tmp:
            a, b = Path(tmp) / "a.json", Path(tmp) / "b.json"
            a.write_text(json.dumps(encounter([], id="a")), encoding="utf-8")
            b.write_text(json.dumps(encounter([], id="b")), encoding="utf-8")
            reports = v.validate_all([a, b], data=self.data)
        self.assertTrue(any("already belongs" in e for e in reports[1].errors))

    def test_vocabulary_comes_from_the_schema(self):
        vocab = v.vocabulary()
        self.assertIn("soak", vocab["actions"])
        self.assertIn("players_in_area", vocab["triggers"])
        self.assertIn("grid", vocab["shapes"])
        self.assertIn("cast_centre", vocab["anchors"])
        self.assertIn("event_player", vocab["anchors"])
        self.assertEqual(set(v.ACTION_NEEDS) - set(vocab["actions"]), set())
        self.assertEqual(set(v.TRIGGER_NEEDS) - set(vocab["triggers"]), set())


if __name__ == "__main__":
    unittest.main()
