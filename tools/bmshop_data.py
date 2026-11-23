"""BM (Quna/cash) shop catalog from the client's BMShopGoods.dat + BMShopCategory.dat.

The in-game BM shop UI (Shop) asks GET /local-api-gate/goods/v2.0/goods and matches each
returned goods_id against BMShopGoods.dat IDs; everything else (names, icons, prices, currency,
category tab) comes from the client table. Rebuild:

  python tools/bmshop_data.py

or legacy WorldProbe tables:

  python tools/bmshop_data.py research/local/bmshop-tables-YYYYMMDD research/local/<probe-with-Item.dat>

Writes tools/data/bmshop_goods.json (consumed by tools/shop_platform_data.py).
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from data_json import write_data_json

OUT = ROOT / 'tools/data/bmshop_goods.json'
CLIENT_EXPORT = ROOT / 'research' / 'client-export' / '2026-11-22'


def load_tables(d: Path) -> dict:
    summary = json.loads((d / 'summary.json').read_text(encoding='utf-8'))
    out = {}
    for p in summary['packages']:
        if p.get('status') == 'table_decoded':
            name = Path(p['path']).stem
            out[name] = json.loads((d / p['file']).read_text(encoding='utf-8'))['Properties']['Data']
    return out


def enum(v):
    return str(v).split('::')[-1] if v is not None else None


def build_client_export(export_dir: Path | None = None):
    from client_export import load_rows
    from client_export_dat import cell_id, export_available, export_version

    export_dir = export_dir or CLIENT_EXPORT
    if not export_available(export_dir):
        raise FileNotFoundError(f'client export missing under {export_dir}')
    item_ids = {r['Name']: cell_id(r['ID']) for r in load_rows('Item', export_dir)}
    goods = []
    for r in load_rows('BMShopGoods', export_dir):
        goods.append({
            'goods_id': cell_id(r['ID']),
            'category_light': r['CategoryLight'],
            'category_dark': r['CategoryDark'],
            'race': enum(r['Race']),
            'on_sale': bool(r['bOnSale']),
            'item_name': r['ItemName'],
            'item_id': item_ids.get(r['ItemName']),
            'item_count': r['ItemCount'],
            'currency': enum(r['SaleCurrencyType']),
            'price': r['SaleCurrencyPrice'],
            'display_order': r['DisplayOrder'],
        })
    cats = [{
        'category': c['Category'], 'sub_category': c['SubCategory'], 'group': enum(c['CategoryGroup']),
        'shop_type': enum(c['ShopType']), 'show': bool(c['bIsShow']), 'order': c['Order'],
    } for c in load_rows('BMShopCategory', export_dir)]
    gver = export_version('BMShopGoods', export_dir)
    return {
        'source': f'client BMShopGoods.dat v{gver} / BMShopCategory.dat (export 2026-11-22, tools/bmshop_data.py)',
        'goods': sorted(goods, key=lambda g: g['goods_id']),
        'categories': cats,
    }


def build_probe(shop_dir: Path, item_dir: Path | None):
    t = load_tables(shop_dir)
    item_ids = {}
    if item_dir:
        items = load_tables(item_dir).get('Item') or []
        item_ids = {r['Name']: r['ID']['Value'] for r in items}
    goods = []
    for r in t['BMShopGoods']:
        goods.append({
            'goods_id': r['ID']['Value'],
            'category_light': r['CategoryLight'],
            'category_dark': r['CategoryDark'],
            'race': enum(r['Race']),
            'on_sale': bool(r['bOnSale']),
            'item_name': r['ItemName'],
            'item_id': item_ids.get(r['ItemName']),
            'item_count': r['ItemCount'],
            'currency': enum(r['SaleCurrencyType']),
            'price': r['SaleCurrencyPrice'],
            'display_order': r['DisplayOrder'],
        })
    cats = [{
        'category': c['Category'], 'sub_category': c['SubCategory'], 'group': enum(c['CategoryGroup']),
        'shop_type': enum(c['ShopType']), 'show': bool(c['bIsShow']), 'order': c['Order'],
    } for c in t['BMShopCategory']]
    return {
        'source': 'client BMShopGoods.dat / BMShopCategory.dat (WorldProbe tables)',
        'goods': sorted(goods, key=lambda g: g['goods_id']),
        'categories': cats,
    }


def main(shop_dir: str | None = None, item_dir: str | None = None):
    from client_export_dat import export_available

    if shop_dir:
        data = build_probe(Path(shop_dir), Path(item_dir) if item_dir else None)
    elif export_available(CLIENT_EXPORT):
        data = build_client_export()
    else:
        raise SystemExit('Pass bmshop table dir or add research/client-export/2026-11-22')
    write_data_json(OUT, data)
    goods = data['goods']
    print(f"{len(goods)} BM goods ({sum(g['on_sale'] for g in goods)} on sale), "
          f"{sum(1 for g in goods if g['item_id'])} with item id, {len(data['categories'])} categories -> {OUT.relative_to(ROOT)}")


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else None, sys.argv[2] if len(sys.argv) > 2 else None)