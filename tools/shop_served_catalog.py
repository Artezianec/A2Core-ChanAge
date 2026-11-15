# -*- coding: utf-8 -*-
"""Build the served BM HTTP catalog from the official capture + client BMShopGoods.

The official capture (tools/data/official_shop.json) stays an untouched reference.
This writes tools/data/official_shop.served.json, which Aion.Host / Aion.World embed
and serve on goods/v2.0/goods.

  python tools/shop_served_catalog.py

Rules (private Quna-only server):
- Keep every official field for a goods_id that exists in the capture; only prices change.
- Add on-sale BMShopGoods rows the capture is missing, in the official v2.0 shape.
- Off-sale client rows are omitted from the synthesized set.
- Cash and Mileage prices become Quna (group 145). Kina (159) and Quna (145) stay.
- Hide Quna top-up (Daily_RechargeQuna / grant of item 535300006) and currency group 261.
- platform_goods.json GameCash prices win when present (e.g. membership 25000).
"""
from __future__ import annotations

import json
import sys
from collections import defaultdict
from copy import deepcopy
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
from data_json import write_data_json

OFFICIAL = ROOT / "tools/data/official_shop.json"
BMSHOP = ROOT / "tools/data/bmshop_goods.json"
PLATFORM = ROOT / "tools/data/platform_goods.json"
BOXES = ROOT / "tools/data/boxes.json"
OUT = ROOT / "tools/data/official_shop.served.json"

QUNA_ITEM_ID = 535300006
CASH_GROUP = "1"
QUNA_GROUP = "145"
MILEAGE_GROUP = "158"
KINA_GROUP = "159"
GROUP_261 = "261"
MILEAGE_MULTIPLIER = 1
FAR_FROM = "2025-01-01T05:00:00+09:00"
FAR_TO = "2123-12-31T05:00:00+09:00"
BLOCKED_CATEGORIES = {"Daily_RechargeQuna", "D_Daily_RechargeQuna"}

CURRENCY_TO_GROUP = {
    "Cash": CASH_GROUP,
    "GameCash": QUNA_GROUP,
    "GoldCombined": KINA_GROUP,
    "Mileage": MILEAGE_GROUP,
}


def load(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def int_or_none(v):
    if v is None or v == "":
        return None
    try:
        return int(v)
    except (TypeError, ValueError):
        return None


def dec_price(v) -> int | None:
    if v is None or v == "":
        return None
    try:
        return int(float(v))
    except (TypeError, ValueError):
        return None


def goods_items(g: dict) -> list[int]:
    ids = []
    for it in g.get("items") or []:
        tid = int_or_none(it.get("game_item_key")) or int_or_none(it.get("item_id"))
        if tid:
            ids.append(tid)
    return ids


def catl_of(g: dict) -> str:
    gd = g.get("goods_data") or ""
    for part in str(gd).split(","):
        if part.startswith("catl:"):
            return part[5:]
    return ""


def build_box_quna_ids(boxes: dict) -> set[int]:
    """Item templates whose RewardBox contents include the Quna currency item (one level)."""
    uses = boxes.get("uses") or {}
    box_rows = boxes.get("boxes") or {}
    packages = boxes.get("packages") or {}
    quna_boxes: set[str] = set()
    quna_packages: set[str] = set()

    def package_has_quna(name: str) -> bool:
        if name in quna_packages:
            return True
        for row in packages.get(name) or []:
            # [item_id, prob, min, max]
            if row and int_or_none(row[0]) == QUNA_ITEM_ID:
                quna_packages.add(name)
                return True
        return False

    for name, box in box_rows.items():
        hit = False
        for item in box.get("items") or []:
            tid = int_or_none(item[0] if isinstance(item, (list, tuple)) else item)
            if tid == QUNA_ITEM_ID:
                hit = True
        for item in box.get("select") or []:
            tid = int_or_none(item[0] if isinstance(item, (list, tuple)) else item)
            if tid == QUNA_ITEM_ID:
                hit = True
        for pkg in box.get("packages") or []:
            pname = pkg[0] if isinstance(pkg, (list, tuple)) else pkg
            if package_has_quna(str(pname)):
                hit = True
        if hit:
            quna_boxes.add(name)

    result = set()
    for key, use in uses.items():
        tid = int_or_none(key)
        if not tid or not use:
            continue
        effect = str(use[0]).split("::")[-1]
        if effect != "RewardBox" or len(use) < 3:
            continue
        group = str(use[2])
        if group in quna_boxes or package_has_quna(group):
            result.add(tid)
    return result


def platform_quna_prices(platform: dict) -> dict[int, int]:
    """Numeric platform prices that should be charged in Quna.

    GameCash is already Quna. Cash and Mileage are sold for Quna on this server
    at the catalog number (membership 100024 is Cash 25000 in platform_goods).
    GoldCombined stays Kina and is not included here.
    """
    prices = {}
    for e in platform.get("entries") or []:
        if e.get("price") and e.get("goods_id") and e.get("currency") in ("GameCash", "Cash", "Mileage"):
            prices[int(e["goods_id"])] = int(e["price"])
    return prices


def blocked(goods_id: int, item_ids: list[int], categories: set[str], group: str | None,
            box_quna: set[int]) -> bool:
    if group == GROUP_261:
        return True
    if categories & BLOCKED_CATEGORIES:
        return True
    if QUNA_ITEM_ID in item_ids:
        return True
    if any(i in box_quna for i in item_ids):
        return True
    return False


def map_price(group: str | None, price: int | None, goods_id: int, platform_prices: dict[int, int]) -> tuple[str, int] | None:
    if goods_id in platform_prices:
        return QUNA_GROUP, platform_prices[goods_id]
    if group in (None, ""):
        return None
    if group == GROUP_261:
        return None
    if price is None or price < 0:
        return None
    if group == CASH_GROUP:
        return QUNA_GROUP, price
    if group == MILEAGE_GROUP:
        return QUNA_GROUP, price * MILEAGE_MULTIPLIER
    if group in (QUNA_GROUP, KINA_GROUP):
        return group, price
    return None


def apply_price_policies(g: dict, goods_id: int, platform_prices: dict[int, int]) -> bool:
    """Rewrite price policies in place. Returns False if the row must be hidden."""
    pols = g.get("goods_basic_price_policies") or []
    group = pols[0].get("currency_group_id") if pols else None
    price = dec_price(pols[0].get("goods_basic_price")) if pols else None
    mapped = map_price(str(group) if group is not None else None, price, goods_id, platform_prices)
    if mapped is None:
        return False
    g["goods_basic_price_policies"] = [{
        "currency_group_id": mapped[0],
        "goods_basic_price": f"{mapped[1]}.0000",
    }]
    if mapped[0] != CASH_GROUP:
        g["goods_basic_currency_price_policies"] = []
    # A non-empty pgs list makes the 2.0.9 client treat the SKU as a GPA/cash purchase
    # even after the price is rewritten to Quna, so it never sends 0x568b.
    if mapped[0] == QUNA_GROUP:
        g["pgs"] = []
    return True


def apply_quota(q: dict, platform_prices: dict[int, int]) -> bool:
    gid = int_or_none(q.get("goods_id"))
    if gid is None:
        return False
    group = str(q.get("currency_group_id") or "")
    price = dec_price(q.get("current_payment_amount"))
    mapped = map_price(group, price, gid, platform_prices)
    if mapped is None:
        return False
    q["currency_group_id"] = mapped[0]
    q["current_payment_amount"] = f"{mapped[1]}.000000"
    return True


def skeleton_from(official_good: dict, bm: dict, item_id: int) -> dict:
    row = {
        "goods_id": str(bm["goods_id"]),
        "app_group_code": official_good.get("app_group_code", "aion2global"),
        "goods_name": bm.get("item_name") or str(bm["goods_id"]),
        "sale_status": "2",
        "effective_from": FAR_FROM,
        "effective_to": FAR_TO,
        "purchase_type": official_good.get("purchase_type", "1"),
        "is_subscribable": "0",
        "is_delivery_confirmation": official_good.get("is_delivery_confirmation", "0"),
        "refund_unit_type": official_good.get("refund_unit_type", "1"),
        "inventory_type": official_good.get("inventory_type", "0"),
        "goods_type": official_good.get("goods_type", "1"),
        "goods_delivery_type": official_good.get("goods_delivery_type", "0"),
        "is_single": "0",
        "items": [{
            "item_id": str(item_id),
            "item_quantity": str(bm.get("item_count") or 1),
            "item_receive_expiration_type": "1",
            "item_name": bm.get("item_name") or "",
            "item_type": "103",
            "game_item_key": str(item_id),
        }],
        "pgs": [],
        "display_names": [{"language_code": "en-US", "goods_display_name": bm.get("item_name") or str(bm["goods_id"])}],
        "display_policies": [],
        "goods_basic_price_policies": [{
            "currency_group_id": CURRENCY_TO_GROUP.get(bm.get("currency"), QUNA_GROUP),
            "goods_basic_price": f"{int(bm.get('price') or 0)}.0000",
        }],
        "goods_basic_currency_price_policies": [],
        "goods_missions": [],
        "goods_data": (
            f"catl:{bm.get('category_light') or 'None'},"
            f"catd:{bm.get('category_dark') or 'None'},"
            f"do:{bm.get('display_order') or 0},os:1"
        ),
        "_source": "client_table",
    }
    return row


def build():
    official = load(OFFICIAL)
    bmshop = load(BMSHOP)
    platform = load(PLATFORM)
    boxes = load(BOXES) if BOXES.is_file() else {}
    box_quna = build_box_quna_ids(boxes)
    plat_prices = platform_quna_prices(platform)

    official_by_id = {int(g["goods_id"]): g for g in official["goods"]}
    me_by_id = {int(g["goods_id"]): g for g in official.get("me_goods") or []}
    templates = defaultdict(list)
    for g in official["goods"]:
        if cat := catl_of(g):
            templates[cat].append(g)
    # A representative official row used as a field template for synthesized goods.
    sample = official["goods"][0]

    bm_on = [g for g in bmshop["goods"] if g.get("on_sale")]
    bm_by_id = {int(g["goods_id"]): g for g in bmshop["goods"]}

    served = []
    hidden = []
    synthesized = 0

    # Official capture first (stable order), then missing on-sale client rows.
    seen = set()
    for gid, g in official_by_id.items():
        bm = bm_by_id.get(gid)
        cats = set()
        if cat := catl_of(g):
            cats.add(cat)
        if bm:
            cats.add(bm.get("category_light") or "")
            cats.add(bm.get("category_dark") or "")
        items = goods_items(g)
        pols = g.get("goods_basic_price_policies") or []
        group = str(pols[0]["currency_group_id"]) if pols else None
        if blocked(gid, items, cats, group, box_quna):
            hidden.append(("official", gid))
            continue
        row = deepcopy(g)
        if not apply_price_policies(row, gid, plat_prices):
            hidden.append(("official-unmap", gid))
            continue
        seen.add(gid)
        served.append(row)

    for bm in bm_on:
        gid = int(bm["goods_id"])
        if gid in seen:
            continue
        item_id = int_or_none(bm.get("item_id"))
        if not item_id:
            hidden.append(("bm-no-item", gid))
            continue
        cats = {bm.get("category_light") or "", bm.get("category_dark") or ""}
        group = CURRENCY_TO_GROUP.get(bm.get("currency"))
        if blocked(gid, [item_id], cats, group, box_quna):
            hidden.append(("bm-block", gid))
            continue
        tmpl = (templates.get(bm.get("category_light") or "") or [sample])[0]
        row = skeleton_from(tmpl, bm, item_id)
        if not apply_price_policies(row, gid, plat_prices):
            hidden.append(("bm-unmap", gid))
            continue
        seen.add(gid)
        synthesized += 1
        served.append(row)

    me_served = []
    for gid, g in me_by_id.items():
        cats = set()
        if cat := catl_of(g):
            cats.add(cat)
        if bm := bm_by_id.get(gid):
            cats.add(bm.get("category_light") or "")
            cats.add(bm.get("category_dark") or "")
        items = goods_items(g)
        pols = g.get("goods_basic_price_policies") or []
        group = str(pols[0]["currency_group_id"]) if pols else None
        if blocked(gid, items, cats, group, box_quna):
            continue
        row = deepcopy(g)
        if not apply_price_policies(row, gid, plat_prices):
            continue
        me_served.append(row)

    quotas = []
    hidden_ids = {gid for _, gid in hidden}
    for q in official.get("quotas") or []:
        gid = int_or_none(q.get("goods_id"))
        if gid in hidden_ids:
            continue
        row = deepcopy(q)
        if apply_quota(row, plat_prices):
            quotas.append(row)

    on_sale_ids = {int(g["goods_id"]) for g in bm_on}
    missing_on_sale = sorted(on_sale_ids - seen - hidden_ids)
    revision = f"{official.get('revision_id', '8484')}-served"

    out = {
        "source": "derived from official_shop.json + bmshop_goods.json + platform_goods.json (tools/shop_served_catalog.py)",
        "revision_id": revision,
        "goods": served,
        "me_goods": me_served,
        "quotas": quotas,
    }
    write_data_json(OUT, out)
    print(
        f"served goods={len(served)} (synthesized={synthesized}) me_goods={len(me_served)} "
        f"quotas={len(quotas)} hidden={len(hidden)} missing_on_sale={missing_on_sale} -> {OUT.relative_to(ROOT)}"
    )
    return {
        "served": len(served),
        "synthesized": synthesized,
        "me_goods": len(me_served),
        "quotas": len(quotas),
        "hidden": hidden,
        "missing_on_sale": missing_on_sale,
        "on_sale": len(on_sale_ids),
        "revision": revision,
    }


if __name__ == "__main__":
    stats = build()
    # Fail the build if an on-sale, non-blocked client row did not make it in.
    if stats["missing_on_sale"]:
        raise SystemExit(f"on-sale goods missing from served catalog: {stats['missing_on_sale']}")
