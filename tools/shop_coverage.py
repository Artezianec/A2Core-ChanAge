"""BM / NPC shop tab coverage report.

  python tools/shop_coverage.py
  python tools/shop_coverage.py --json reports/shop-coverage.json
"""
from __future__ import annotations

import argparse
import json
import sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))

BMSHOP = ROOT / 'tools/data/bmshop_goods.json'
BMSHOP_G4L = ROOT / 'tools/data/bmshop_g4l_supplements.json'
SERVED = ROOT / 'tools/data/official_shop.served.json'
BOXES = ROOT / 'tools/data/boxes.json'
G4L_PRODUCTS = ROOT / 'tools/data/g4l_shop_products.json'


def catl_of(goods_data: str) -> str:
    for part in str(goods_data).split(','):
        if part.startswith('catl:'):
            return part[5:]
    return 'None'


def load_bmshop_goods():
    data = json.loads(BMSHOP.read_text(encoding='utf-8'))
    goods = list(data.get('goods') or [])
    if BMSHOP_G4L.is_file():
        by_id = {g['goods_id']: g for g in goods}
        for row in json.loads(BMSHOP_G4L.read_text(encoding='utf-8')).get('goods') or []:
            gid = row['goods_id']
            by_id[gid] = {**(by_id.get(gid) or {}), **row}
        goods = sorted(by_id.values(), key=lambda g: g['goods_id'])
    return goods


def main():
    p = argparse.ArgumentParser(description='BM shop tab coverage vs served HTTP catalog')
    p.add_argument('--json', type=Path, help='write machine-readable report')
    args = p.parse_args()

    import shop_served_catalog as served_mod
    build_stats = served_mod.build()

    bm = load_bmshop_goods()
    served = json.loads(SERVED.read_text(encoding='utf-8'))
    uses = json.loads(BOXES.read_text(encoding='utf-8')).get('uses') or {}
    g4l_audit = {}
    if G4L_PRODUCTS.is_file():
        g4l_audit = json.loads(G4L_PRODUCTS.read_text(encoding='utf-8')).get('audit') or {}

    served_ids = {int(g['goods_id']) for g in served.get('goods') or []}
    on_sale = [g for g in bm if g.get('on_sale')]
    by_tab = defaultdict(list)
    for g in on_sale:
        by_tab[g.get('category_light') or 'None'].append(g)

    tabs = []
    for tab, rows in sorted(by_tab.items()):
        visible = [g for g in rows if int(g['goods_id']) in served_ids]
        missing = [g for g in rows if int(g['goods_id']) not in served_ids]
        no_use = [g for g in rows if g.get('item_id') and str(g['item_id']) not in uses]
        tabs.append({
            'tab': tab,
            'on_sale': len(rows),
            'served': len(visible),
            'missing_goods_ids': sorted(int(g['goods_id']) for g in missing),
            'items_without_use': sorted({g['item_id'] for g in no_use if g.get('item_id')}),
        })

    def needs_use(item_id: int) -> bool:
        return item_id is not None and (
            503_700_000 <= item_id < 504_000_000
            or 534_760_000 <= item_id < 535_000_000
            or 535_510_000 <= item_id < 536_500_000
            or 553_710_000 <= item_id < 554_000_000
        )

    no_use_served = []
    for g in on_sale:
        gid = int(g['goods_id'])
        if gid not in served_ids:
            continue
        iid = g.get('item_id')
        if iid and needs_use(int(iid)) and str(iid) not in uses:
            no_use_served.append(iid)

    report = {
        'served_goods': len(served_ids),
        'on_sale_goods': len(on_sale),
        'missing_on_sale': build_stats.get('missing_on_sale') or [],
        'hidden_count': len(build_stats.get('hidden') or []),
        'served_on_sale_without_item_use': sorted(set(no_use_served)),
        'g4l_audit': g4l_audit,
        'tabs': tabs,
    }

    print(f"served={report['served_goods']} on_sale={report['on_sale_goods']} "
          f"missing_on_sale={len(report['missing_on_sale'])} "
          f"g4l_kr_only={len(g4l_audit.get('kr_only_or_unverified_item_ids') or [])}")
    for row in tabs:
        if row['served'] < row['on_sale'] and row['tab'] not in ('Daily_RechargeQuna', 'D_Daily_RechargeQuna'):
            print(f"  GAP {row['tab']}: served {row['served']}/{row['on_sale']} "
                  f"missing={row['missing_goods_ids'][:8]}{'...' if len(row['missing_goods_ids']) > 8 else ''}")

    if args.json:
        args.json.parent.mkdir(parents=True, exist_ok=True)
        args.json.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
        print(f"wrote {args.json}")


if __name__ == '__main__':
    main()