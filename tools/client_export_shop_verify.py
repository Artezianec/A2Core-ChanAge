"""Compare client export GameShop* / BMShop* tables to committed tools/data JSON.

  python tools/client_export_shop_verify.py
  python tools/client_export_shop_verify.py --json reports/client-export-shop-verify.json
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))

from client_export import load_rows
from client_export_dat import cell_id, export_available, short

SHOPS = ROOT / 'tools/data/shops.json'
BMSHOP = ROOT / 'tools/data/bmshop_goods.json'


def count_goods(path: Path) -> int:
    return len(json.loads(path.read_text(encoding='utf-8')).get('goods') or [])


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--json', type=Path, help='write report JSON')
    args = parser.parse_args()
    if not export_available():
        print('client export not found', file=sys.stderr)
        return 1

    report = {
        'export': {
            'GameShop': len(load_rows('GameShop')),
            'GameShopGoods': len(load_rows('GameShopGoods')),
            'GameShopGoodsList': len(load_rows('GameShopGoodsList')),
            'BMShopGoods': len(load_rows('BMShopGoods')),
            'BMShopCategory': len(load_rows('BMShopCategory')),
        },
        'committed': {},
        'notes': [
            'shops.json still built from WorldProbe (PR #574 may extend); this script only compares counts.',
            'bmshop_goods.json is rebuilt from export via tools/bmshop_data.py.',
        ],
    }
    if SHOPS.is_file():
        shops = json.loads(SHOPS.read_text(encoding='utf-8'))
        report['committed']['shops_goods'] = len(shops.get('goods') or {})
        report['committed']['shops_shops'] = len(shops.get('shops') or {})
    if BMSHOP.is_file():
        report['committed']['bmshop_goods'] = count_goods(BMSHOP)
        report['committed']['bmshop_on_sale'] = sum(
            1 for g in json.loads(BMSHOP.read_text(encoding='utf-8')).get('goods') or [] if g.get('on_sale')
        )

    export_on_sale = sum(1 for r in load_rows('BMShopGoods') if r.get('bOnSale'))
    report['bmshop_on_sale_match'] = report.get('committed', {}).get('bmshop_on_sale') == export_on_sale

    print(json.dumps({k: v for k, v in report.items() if k != 'notes'}, indent=2))
    if args.json:
        args.json.parent.mkdir(parents=True, exist_ok=True)
        args.json.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
        print(f'wrote {args.json}')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())