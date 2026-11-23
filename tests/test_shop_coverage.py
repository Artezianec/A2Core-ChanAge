"""Shop coverage script smoke test."""
import json
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class ShopCoverageTests(unittest.TestCase):
    def test_coverage_reports_full_served_tabs(self):
        out = subprocess.check_output(
            ['python3', str(ROOT / 'tools/shop_coverage.py'), '--json', str(ROOT / 'reports/shop-coverage.json')],
            cwd=ROOT,
            text=True,
        )
        report = json.loads((ROOT / 'reports/shop-coverage.json').read_text(encoding='utf-8'))
        self.assertIn(f"served={report['served_goods']}", out)
        self.assertGreaterEqual(report['served_goods'], 248)
        self.assertEqual([], report['missing_on_sale'])
        recharge = next(t for t in report['tabs'] if t['tab'] == 'Daily_RechargeQuna')
        self.assertEqual(0, recharge['served'])


if __name__ == '__main__':
    unittest.main()