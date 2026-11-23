"""Fill empty RewardBox groups referenced by on-sale BM items using RewardPackage (client export).

BoxReward.dat is not in the 2026-11-22 export; some G_* shop boxes can still be wired when a
RewardPackage row references PKG_* entries present in RewardPackageItem.

  python tools/shop_box_packages.py

Patches tools/data/boxes.json in place (merge only; never removes rows).
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))

from client_export import load_rows
from client_export_dat import export_available
from data_json import write_data_json

BOXES = ROOT / 'tools/data/boxes.json'
BMSHOP = ROOT / 'tools/data/bmshop_goods.json'


def on_sale_item_ids() -> set[int]:
    goods = json.loads(BMSHOP.read_text(encoding='utf-8')).get('goods') or []
    return {int(g['item_id']) for g in goods if g.get('on_sale') and g.get('item_id')}


def patch_boxes(data: dict) -> int:
    uses = data.get('uses') or {}
    boxes = data.setdefault('boxes', {})
    packages = data.get('packages') or {}
    shop_items = on_sale_item_ids()
    reward_by_name = {r['Name']: r for r in load_rows('RewardPackage') if r.get('Name')}
    changed = 0

    for key, use in uses.items():
        if not use or use[0] != 'RewardBox' or len(use) < 3:
            continue
        try:
            iid = int(key)
        except ValueError:
            continue
        if iid not in shop_items:
            continue
        group = str(use[2])
        box = boxes.setdefault(group, {'items': [], 'select': [], 'packages': [], 'max': 100})
        if box.get('items') or box.get('select') or box.get('packages'):
            continue
        rp = reward_by_name.get(group)
        if not rp:
            continue
        pkg_items = []
        for entry in rp.get('PackageItems') or []:
            pname = entry.get('Item')
            prob = int(entry.get('Prob') or 0)
            count = int(entry.get('Count') or 1)
            if pname in packages:
                pkg_items.append([pname, prob, count])
        if not pkg_items:
            continue
        box['packages'] = pkg_items
        box['max'] = max(box.get('max') or 1, 1)
        changed += 1
    return changed


def main() -> int:
    if not export_available() or not BOXES.is_file():
        print('missing export or boxes.json', file=sys.stderr)
        return 1
    data = json.loads(BOXES.read_text(encoding='utf-8'))
    n = patch_boxes(data)
    if n:
        write_data_json(BOXES, data)
    print(f'filled {n} empty shop box groups -> {BOXES.relative_to(ROOT)}')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())