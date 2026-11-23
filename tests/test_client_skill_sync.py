"""Smoke tests for client export sync."""
import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class ClientSkillSyncTests(unittest.TestCase):
    def test_passive_abnormals_have_client_source(self):
        boxes = json.loads((ROOT / 'tools/data/boxes.json').read_text(encoding='utf-8'))
        passives = boxes.get('passives') or {}
        abnormals = boxes.get('abnormals') or {}
        missing = []
        for sid, ids in passives.items():
            if not (11_000_000 <= int(sid) < 20_000_000):
                continue
            for ab in ids:
                row = abnormals.get(str(ab))
                if not row or row.get('source') != 'client':
                    missing.append(ab)
        self.assertEqual(missing, [], f'passive abnormals without client specs: {missing[:5]}')

    def test_abnormal_coverage_summary(self):
        text = (ROOT / 'tools/reports/abnormal-coverage.md').read_text(encoding='utf-8')
        self.assertIn('exported 118/118 (100%)', text)


if __name__ == '__main__':
    unittest.main()