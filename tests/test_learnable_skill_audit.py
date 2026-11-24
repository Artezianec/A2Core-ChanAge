import unittest
from pathlib import Path

from tools import learnable_skill_audit


class LearnableSkillAuditScriptTests(unittest.TestCase):
    def test_committed_report_matches_harness(self):
        if not (Path(__file__).resolve().parents[1] / 'docs' / 'skills-audit.md').exists():
            self.skipTest('docs/skills-audit.md not generated yet')
        self.assertEqual(learnable_skill_audit.main(['--check']), 0)


if __name__ == '__main__':
    unittest.main()