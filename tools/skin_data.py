"""Skins for the server: Skin.dat and SkinSet.dat (research/local/skin-tables/out, WorldProbe) -> tools/data/skins.json.

- skins: Skin.dat Name -> ID (GetSkin items name the skin: UseEffectValues ["AR_0067_T05_Torso"]).
- sets: SkinSet.dat Name -> [skin IDs] (GetSkinSet items name the set).
- categories: skin ID -> ESkinCategory name (Torso, Pants, Greatsword, ...).
The client keeps the collection as SkinInfo_NT 0x8d5b (OBSERVED empty at the official world entry: no skins, no
points, preset 1) and equips with SkinEquip_RQ 0x8d5e; an equipped skin shows as EquipBroad _skin_id.
Rebuild with `python tools/skin_data.py`.
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/skins.json'
TABLES = ROOT / 'research/local/skin-tables/out'
CLIENT_EXPORT = ROOT / 'research' / 'client-export' / '2026-11-22'


def _rows(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))['Properties']['Data']


def build_client_export(export_dir: Path | None = None):
    try:
        from .client_export import load_rows
        from .client_export_dat import cell_id, export_available, export_version, short
    except ImportError:
        from client_export import load_rows
        from client_export_dat import cell_id, export_available, export_version, short

    export_dir = export_dir or CLIENT_EXPORT
    if not export_available(export_dir):
        raise FileNotFoundError(f'client export missing under {export_dir}')
    skins, categories = {}, {}
    for row in load_rows('Skin', export_dir):
        skin_id = cell_id(row['ID'])
        skins[row['Name']] = skin_id
        categories[str(skin_id)] = short(row.get('SkinCategory'))
    sets = {}
    for row in load_rows('SkinSet', export_dir):
        ids = [skins[name] for name in row.get('Skins') or [] if name in skins]
        if ids:
            sets[row['Name']] = ids
    ver = export_version('Skin', export_dir)
    return {
        'source': f'Skin.dat v{ver} + SkinSet.dat (client export 2026-11-22, tools/skin_data.py)',
        'skins': skins,
        'sets': sets,
        'categories': categories,
    }


def build(tables=TABLES):
    summary = json.loads((tables / 'summary.json').read_text(encoding='utf-8'))
    files = {p['path'].split('/')[-1]: p['file'] for p in summary['packages']}
    skins, categories = {}, {}
    for row in _rows(tables / files['Skin.dat']):
        skin_id = row['ID']['Value']
        skins[row['Name']] = skin_id
        categories[str(skin_id)] = str(row.get('SkinCategory', '')).split('::')[-1]
    sets = {}
    for row in _rows(tables / files['SkinSet.dat']):
        ids = [skins[name] for name in row.get('Skins') or [] if name in skins]
        if ids:
            sets[row['Name']] = ids
    return {'source': 'Skin.dat + SkinSet.dat (tools/skin_data.py)', 'skins': skins, 'sets': sets, 'categories': categories}


def main():
    try:
        from .client_export_dat import export_available
    except ImportError:
        from client_export_dat import export_available

    data = build_client_export() if export_available(CLIENT_EXPORT) else build()
    try:
        from .data_json import write_data_json
    except ImportError:
        from data_json import write_data_json
    write_data_json(DATA, data)
    print(f"{len(data['skins'])} skins, {len(data['sets'])} sets -> {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
