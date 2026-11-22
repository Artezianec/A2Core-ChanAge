"""Fill tools/data/boxes.json uses gaps for BM shop items (when Item.dat export lags).

Heuristics from bmshop_goods item_name until `python tools/box_data.py` rebuilds from catalogue.
"""
from __future__ import annotations

import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BMSHOP = ROOT / 'tools/data/bmshop_goods.json'
BOXES = ROOT / 'tools/data/boxes.json'

SKIN_SET = re.compile(r'^BM_Skinshop_SkinSet_(.+)_b$')
REWARD_BOX = re.compile(r'^(?:G_)?(?:BM_)?Box_', re.I)


def infer_use(item_id: int, item_name: str, rides: dict) -> list | None:
    name = (item_name or '').strip()
    if not name:
        return None
    if match := SKIN_SET.match(name):
        return ['GetSkinSet', 1, match.group(1)]
    if name.startswith('BM_Skinshop_Skin_') and name.endswith('_b'):
        # UseEffectValues are Skin.dat internal names; only add when already mapped elsewhere.
        return None
    if REWARD_BOX.match(name):
        group = name if name.startswith('G_') else f'G_{name}' if name.startswith('BM_') else name
        return ['RewardBox', 1, group]
    if name.startswith('Vehicle_') and name.endswith('_b'):
        for ride_id, row in rides.items():
            if row.get('item') == item_id:
                return ['GetVehicle', 1, ride_id]
    return None


def merge_bmshop_uses(uses: dict, rides: dict) -> int:
    if not BMSHOP.is_file():
        return 0
    goods = json.loads(BMSHOP.read_text(encoding='utf-8')).get('goods') or []
    added = 0
    seen = set()
    for row in goods:
        if not row.get('on_sale'):
            continue
        item_id = row.get('item_id')
        if not item_id or item_id in seen:
            continue
        seen.add(item_id)
        key = str(item_id)
        if key in uses:
            continue
        use = infer_use(int(item_id), row.get('item_name') or '', rides)
        if use:
            uses[key] = use
            added += 1
    return added


def apply(path: Path = BOXES) -> dict:
    data = json.loads(path.read_text(encoding='utf-8'))
    uses = data.setdefault('uses', {})
    cool = data.setdefault('cool', {})
    rides = data.get('rides') or {}
    added = merge_bmshop_uses(uses, rides)
    for key in list(uses):
        cool.setdefault(key, [0, 'None', 0])
    data['shop_use_gaps_filled'] = added
    return data


def main():
    data = apply()
    try:
        from .data_json import write_data_json
    except ImportError:
        from data_json import write_data_json
    write_data_json(BOXES, data)
    print(f'Added {data.get("shop_use_gaps_filled", 0)} BM shop uses -> {BOXES.relative_to(ROOT)}')


if __name__ == '__main__':
    main()