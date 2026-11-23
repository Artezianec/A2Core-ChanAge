"""Fill tools/data/boxes.json uses gaps for BM shop items (when Item.dat export lags).

Heuristics from bmshop_goods item_name until `python tools/box_data.py` rebuilds from catalogue.
Also applies tools/data/bmshop_item_use_overrides.json (skins/rides/boxes supplements).
"""
from __future__ import annotations

import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BMSHOP = ROOT / 'tools/data/bmshop_goods.json'
BOXES = ROOT / 'tools/data/boxes.json'
SKINS = ROOT / 'tools/data/skins.json'
OVERRIDES = ROOT / 'tools/data/bmshop_item_use_overrides.json'
G4L_SUPPLEMENTS = ROOT / 'tools/data/bmshop_g4l_supplements.json'
BMSHOP_G4L = G4L_SUPPLEMENTS

SKIN_SET = re.compile(r'^BM_Skinshop_SkinSet_(.+)_b$')
REWARD_BOX = re.compile(r'^(?:G_)?(?:BM_)?Box_', re.I)
SKIN_ACC = re.compile(r'^BM_Skinshop_Skin_ACC_(\d+)_b$')
SKIN_DECO = re.compile(r'^BM_Skinshop_Skin_DECO_(\d+)_b$')
SKIN_AR = re.compile(r'^BM_Skinshop_Skin_AR_Part_(\d+)_b$')


def load_overrides() -> dict:
    data = {}
    if OVERRIDES.is_file():
        data = json.loads(OVERRIDES.read_text(encoding='utf-8'))
    if G4L_SUPPLEMENTS.is_file():
        sup = json.loads(G4L_SUPPLEMENTS.read_text(encoding='utf-8'))
        for key, use in (sup.get('uses') or {}).items():
            data.setdefault('uses', {})[key] = use
    return data


def merge_skin_supplements(skins_path: Path = SKINS, overrides: dict | None = None) -> int:
    overrides = overrides if overrides is not None else load_overrides()
    skin_rows = overrides.get('skins') or {}
    if not skin_rows:
        return 0
    data = json.loads(skins_path.read_text(encoding='utf-8'))
    added = 0
    for name, sid in skin_rows.items():
        if name not in data.get('skins', {}):
            data.setdefault('skins', {})[name] = int(sid)
            added += 1
    for sid, cat in (overrides.get('skin_categories') or {}).items():
        data.setdefault('categories', {})[str(sid)] = cat
    try:
        from .data_json import write_data_json
    except ImportError:
        from data_json import write_data_json
    write_data_json(skins_path, data)
    return added


def infer_use(item_id: int, item_name: str, rides: dict, skins: dict) -> list | None:
    name = (item_name or '').strip()
    if not name:
        return None
    if match := SKIN_SET.match(name):
        return ['GetSkinSet', 1, match.group(1)]
    if m := SKIN_ACC.match(name):
        suffix = int(m.group(1))
        for candidate in (f'ACC_ER_{suffix:04d}', f'ACC_NL_{suffix:04d}'):
            if candidate in skins:
                return ['GetSkin', 1, candidate]
        return None
    if m := SKIN_DECO.match(name):
        suffix = int(m.group(1))
        for candidate in (f'DECO_EW_{suffix:04d}', f'DECO_MW_{suffix:04d}', f'DECO_SDW_{suffix:04d}'):
            if candidate in skins:
                return ['GetSkin', 1, candidate]
        return None
    if m := SKIN_AR.match(name):
        suffix = int(m.group(1))
        hat = f'AR_Part_HalloweenHat_{suffix - 100:02d}' if 100 <= suffix <= 104 else f'AR_Part_{suffix:03d}'
        if hat in skins:
            return ['GetSkin', 1, hat]
        return None
    if name.startswith('BM_Skinshop_Skin_') and name.endswith('_b'):
        return None
    if REWARD_BOX.match(name) or name.startswith('G_event_'):
        group = name if name.startswith('G_') else f'G_{name}' if name.startswith('BM_') else name
        return ['RewardBox', 0, group]
    if name.startswith('Vehicle_') and name.endswith('_b'):
        for ride_id, row in rides.items():
            if row.get('item') == item_id:
                return ['GetVehicle', 1, ride_id]
    return None


def apply_overrides(data: dict, overrides: dict) -> int:
    uses = data.setdefault('uses', {})
    cool = data.setdefault('cool', {})
    rides = data.setdefault('rides', {})
    boxes = data.setdefault('boxes', {})
    added = 0
    for key, use in (overrides.get('uses') or {}).items():
        if key not in uses:
            uses[key] = use
            cool.setdefault(key, [0, 'None', 0])
            added += 1
    for rid, row in (overrides.get('rides') or {}).items():
        if rid not in rides:
            rides[rid] = row
    for box, row in (overrides.get('boxes') or {}).items():
        boxes.setdefault(box, row)
    return added


def merge_bmshop_uses(uses: dict, rides: dict, skins: dict) -> int:
    if not BMSHOP.is_file():
        return 0
    bm = json.loads(BMSHOP.read_text(encoding='utf-8'))
    goods = list(bm.get('goods') or [])
    if BMSHOP_G4L.is_file():
        by_id = {g['goods_id']: g for g in goods}
        for row in json.loads(BMSHOP_G4L.read_text(encoding='utf-8')).get('goods') or []:
            gid = row['goods_id']
            by_id.setdefault(gid, row)
            if row.get('on_sale'):
                by_id[gid] = {**by_id[gid], **row, 'on_sale': True}
        goods = list(by_id.values())
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
        use = infer_use(int(item_id), row.get('item_name') or '', rides, skins)
        if use:
            uses[key] = use
            added += 1
    return added


def apply(path: Path = BOXES) -> dict:
    overrides = load_overrides()
    merge_skin_supplements(SKINS, overrides)
    skin_names = set(json.loads(SKINS.read_text(encoding='utf-8')).get('skins', {}))
    data = json.loads(path.read_text(encoding='utf-8'))
    uses = data.setdefault('uses', {})
    cool = data.setdefault('cool', {})
    rides = data.get('rides') or {}
    added = apply_overrides(data, overrides)
    added += merge_bmshop_uses(uses, data.get('rides') or rides, skin_names)
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