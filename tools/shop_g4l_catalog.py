"""Gamers4Life cash shop snapshot → G4L products, schedules, BM supplements, audits.

Parses tools/data/snapshots/gamers4life-shop.txt (text export; not the live HTML).
Cross-checks deliverable item ids against local boxes/bmshop and compact Aion2Data skins
fetched at generation time (optional --no-fetch).

  python tools/shop_g4l_catalog.py

Writes:
  tools/data/g4l_shop_products.json
  tools/data/shop_schedule.json
  tools/data/bmshop_g4l_supplements.json
"""
from __future__ import annotations

import json
import re
import sys
import urllib.request
from collections import defaultdict
from datetime import datetime
from pathlib import Path
from zoneinfo import ZoneInfo

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
from data_json import write_data_json

SNAPSHOT = ROOT / "tools/data/snapshots/gamers4life-shop.txt"
BMSHOP = ROOT / "tools/data/bmshop_goods.json"
BOXES = ROOT / "tools/data/boxes.json"
SERVED = ROOT / "tools/data/official_shop.served.json"
CONFIG = ROOT / "tools/data/shop_catalog_config.json"

OUT_PRODUCTS = ROOT / "tools/data/g4l_shop_products.json"
OUT_SCHEDULE = ROOT / "tools/data/shop_schedule.json"
OUT_SUPPLEMENTS = ROOT / "tools/data/bmshop_g4l_supplements.json"

KST = ZoneInfo("Asia/Seoul")
MONTHS = {
    "ene": 1,
    "feb": 2,
    "mar": 3,
    "abr": 4,
    "may": 5,
    "jun": 6,
    "jul": 7,
    "ago": 8,
    "sep": 9,
    "oct": 10,
    "nov": 11,
    "dic": 12,
}
DATE_RE = re.compile(
    r"(\d{1,2})\s+de\s+(\w+)\s+de\s+(\d{4})\s*→\s*(\d{1,2})\s+de\s+(\w+)\s+de\s+(\d{4})"
)
G4L_CAT_SLUG = {
    "Daily_Recharge_Quna": "Daily_RechargeQuna",
    "Daily_Skin": "Daily_Skin",
    "Goods_Special": "Goods_Special",
    "Goods_Ticket": "Goods_Ticket",
    "Goods_Usable": "Goods_Usable",
    "Mileage_Special": "Mileage_Special",
    "Mileage_Usable": "Mileage_Usable",
    "Shop_Alch": "Shop_Alch",
    "Shop_Black": "Shop_Black",
    "Shop_Jewel": "Shop_Jewel",
    "Shop_Special": "Shop_Special",
    "Shop_Tail": "Shop_Tail",
    "Shop_Usable": "Shop_Usable",
}
for _weapon in (
    "Bow",
    "Dagger",
    "Earring",
    "Eye_Wear",
    "Greatsword",
    "Helmet",
    "Mace",
    "Magicbook",
    "Necklace",
    "Orb",
    "Shoulder_Wear",
    "Skin_Set",
    "Staff",
    "Sword",
):
    G4L_CAT_SLUG[f"Skin_Shop_{_weapon}"] = f"Skin_Shop_{_weapon}"
    G4L_CAT_SLUG[f"Skin_Shop_{_weapon.replace('_', ' ')}"] = f"Skin_Shop_{_weapon}"

CURRENCY_MAP = {
    "Cash": "Cash",
    "Game Cash": "GameCash",
    "Mileage": "Mileage",
    "Gold Combined": "GoldCombined",
    "Quna": "GameCash",
}

# item_id -> (skin internal name, category for slug helper)
ACCESSORY_SKIN = {
    536310003: "DECO_SDW_Squirrel_01",
    536310004: "DECO_SDW_ElimKid_01",
    536310014: "DECO_EW_0007",
}

WEAPON_SKIN_FROM_A2D = {
    535710921: "WP_Beloved_01_GS",
    535710922: "WP_Beloved_01_SW",
    535710923: "WP_Beloved_01_DA",
    535710924: "WP_Beloved_01_BW",
    535710925: "WP_Beloved_01_MB",
    535710926: "WP_Beloved_01_OR",
    535710927: "WP_Beloved_01_MA",
    535710928: "WP_Beloved_01_ST",
    535710937: "WP_Maid_01_GS",
    535710938: "WP_Maid_01_SW",
    535710939: "WP_Maid_01_DA",
    535710940: "WP_Maid_01_BW",
    535710941: "WP_Maid_01_MB",
    535710942: "WP_Maid_01_OR",
    535710943: "WP_Maid_01_MA",
    535710944: "WP_Maid_01_ST",
}


def fetch_a2d_skins() -> dict[int, tuple[str, str]]:
    url = "https://aion2data.com/raw/skins.json"
    req = urllib.request.Request(
        url,
        headers={
            "User-Agent": "A2Core-shop-g4l-catalog/1.0 (+https://github.com/Artezianec/A2Core-ChanAge)",
            "Accept": "application/json",
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=90) as resp:
            data = json.loads(resp.read())
    except OSError:
        return {}
    out: dict[int, tuple[str, str]] = {}
    for row in data.get("records") or []:
        for u in row.get("unlockedBy") or []:
            out[int(u["id"])] = (row.get("name") or "", row.get("category") or "")
    return out


def slug_category(label: str) -> str:
    slug = label.strip().replace(" ", "_")
    return G4L_CAT_SLUG.get(slug, slug)


def parse_price(line: str) -> int | None:
    m = re.search(r"(?:Cash|Game Cash|Mileage|Gold Combined|Quna)\s*·\s*([\d.,]+)", line, re.I)
    if not m:
        return None
    return int(m.group(1).replace(".", "").replace(",", ""))


def parse_currency(line: str) -> str | None:
    for label, code in CURRENCY_MAP.items():
        if label.lower() in line.lower():
            return code
    return None


def span_to_iso(s: str) -> tuple[str, str] | None:
    m = DATE_RE.search(s)
    if not m:
        return None

    def part(d: str, mo: str, y: str, end_of_day: bool) -> str:
        month = MONTHS.get(mo[:3].lower(), 1)
        dt = datetime(int(y), month, int(d), 23 if end_of_day else 5, 59 if end_of_day else 0, 59, tzinfo=KST)
        if not end_of_day:
            dt = dt.replace(hour=5, minute=0, second=0)
        return dt.isoformat(timespec="seconds")

    return part(m.group(1), m.group(2), m.group(3), False), part(
        m.group(4), m.group(5), m.group(6), True
    )


def parse_limit(line: str) -> dict | None:
    m = re.search(r"(\d+)\s*/\s*(\w+)\s*·\s*(\w+)", line)
    if m:
        return {"qty": int(m.group(1)), "period": m.group(2), "scope": m.group(3)}
    m2 = re.search(r"(\d+)\s*/\s*(\w+)", line)
    if m2:
        return {"qty": int(m2.group(1)), "period": m2.group(2), "scope": None}
    return None


def merge_product_row(existing: dict | None, newer: dict) -> dict:
    if existing is None:
        return dict(newer)
    merged = dict(existing)
    for key in ("category", "category_dark", "shop_id", "title", "qty", "limit", "effective_from", "effective_to"):
        if merged.get(key) in (None, "", 0) and newer.get(key) not in (None, "", 0):
            merged[key] = newer[key]
    if newer.get("price"):
        if not merged.get("price") or newer["price"] > merged["price"]:
            merged["price"] = newer["price"]
            if newer.get("currency"):
                merged["currency"] = newer["currency"]
    elif not merged.get("currency") and newer.get("currency"):
        merged["currency"] = newer["currency"]
    return merged


def best_product_by_item(products: list[dict]) -> dict[int, dict]:
    by_item: dict[int, dict] = {}
    for prod in products:
        iid = prod.get("item_id")
        if not iid:
            continue
        by_item[iid] = merge_product_row(by_item.get(iid), prod)
    return by_item


def priced_product(prod: dict | None, bm_rows_by_item: dict[int, list[dict]] | None = None) -> dict | None:
    if not prod:
        return None
    out = dict(prod)
    if (out.get("price") or 0) > 0 and out.get("currency"):
        return out
    iid = out.get("item_id")
    if iid and bm_rows_by_item:
        for row in bm_rows_by_item.get(iid) or []:
            if row.get("price") and row["price"] > 0:
                out["price"] = row["price"]
                out["currency"] = row.get("currency") or out.get("currency")
                return out
    return None


def parse_snapshot(text: str) -> list[dict]:
    lines = text.splitlines()
    current_cat = "misc"
    current_shop: int | None = None
    pending_price_line: str | None = None
    products: list[dict] = []
    seen: set[tuple] = set()
    block: dict | None = None

    def flush() -> None:
        nonlocal block
        if not block:
            return
        if not block.get("item_id") and not block.get("package_name"):
            block = None
            return
        key = (
            block.get("category"),
            block.get("shop_id"),
            block.get("item_id"),
            block.get("package_name"),
            block.get("price"),
            block.get("currency"),
        )
        if key in seen:
            block = None
            return
        seen.add(key)
        products.append(dict(block))
        block = None

    for line in lines:
        t = line.strip()
        if t.startswith("## "):
            flush()
            current_cat = slug_category(t[3:].strip())
            current_shop = None
            pending_price_line = None
            continue
        if t.startswith("On sale Shop "):
            current_shop = int(t.split()[-1])
            continue
        if block is None and "·" in t and any(x in t for x in ("Cash", "Mileage", "Combined", "Quna", "Game")):
            pending_price_line = t
        if t.startswith("### "):
            flush()
            block = {
                "category": current_cat,
                "category_dark": f"D_{current_cat}" if not current_cat.startswith("D_") else current_cat,
                "shop_id": current_shop,
                "title": t[4:].strip(),
                "currency": None,
                "price": None,
                "item_id": None,
                "package_name": None,
                "qty": 1,
                "limit": None,
                "effective_from": None,
                "effective_to": None,
            }
            if pending_price_line:
                block["currency"] = parse_currency(pending_price_line)
                price = parse_price(pending_price_line)
                if price is not None:
                    block["price"] = price
                pending_price_line = None
            continue
        if block is None:
            im_only = re.search(r"Item (\d{9})", t)
            if im_only and pending_price_line:
                block = {
                    "category": current_cat,
                    "category_dark": f"D_{current_cat}" if not current_cat.startswith("D_") else current_cat,
                    "shop_id": current_shop,
                    "title": "",
                    "currency": parse_currency(pending_price_line),
                    "price": parse_price(pending_price_line),
                    "item_id": int(im_only.group(1)),
                    "package_name": None,
                    "qty": 1,
                    "limit": None,
                    "effective_from": None,
                    "effective_to": None,
                }
                pending_price_line = None
                lim = parse_limit(t)
                if lim:
                    block["limit"] = lim
                span = span_to_iso(t)
                if span:
                    block["effective_from"], block["effective_to"] = span
                    flush()
                continue
            continue
        if block["currency"] is None and "·" in t:
            block["currency"] = parse_currency(t)
            price = parse_price(t)
            if price is not None:
                block["price"] = price
        im = re.search(r"Item (\d{9})", t)
        if im:
            block["item_id"] = int(im.group(1))
        elif not block["item_id"] and (re.search(r"\bB[mM]_", t) or t.endswith("_b")):
            block["package_name"] = t.strip()
        lim = parse_limit(t)
        if lim:
            block["limit"] = lim
        span = span_to_iso(t)
        if span:
            block["effective_from"], block["effective_to"] = span

    flush()
    return products


def served_item_ids(served: dict) -> set[int]:
    ids: set[int] = set()
    for g in served.get("goods") or []:
        for it in g.get("items") or []:
            k = it.get("game_item_key") or it.get("item_id")
            if k:
                ids.add(int(k))
    return ids


def verification_sets(fetch_skins: bool) -> tuple[set[int], dict[int, tuple[str, str]]]:
    uses = set(json.loads(BOXES.read_text(encoding="utf-8")).get("uses") or {})
    uses_int = {int(k) for k in uses}
    a2d_skins = fetch_a2d_skins() if fetch_skins else {}
    return uses_int, a2d_skins


def item_verifiable(
    item_id: int,
    *,
    uses_int: set[int],
    a2d_skins: dict,
    bm_item_ids: set[int],
    official_item_ids: set[int],
) -> list[str]:
    reasons: list[str] = []
    if item_id in uses_int:
        reasons.append("local_uses")
    if item_id in bm_item_ids:
        reasons.append("bmshop")
    if item_id in official_item_ids:
        reasons.append("official_shop")
    if item_id in a2d_skins:
        reasons.append("a2d_skins")
    if item_id in WEAPON_SKIN_FROM_A2D or item_id in ACCESSORY_SKIN:
        reasons.append("mapped_weapon_skin")
    return reasons


def build_supplements(
    products: list[dict],
    *,
    addable: dict[int, dict],
    bm_rows: list[dict],
) -> list[dict]:
    by_item: dict[int, list[dict]] = defaultdict(list)
    for row in bm_rows:
        if row.get("item_id"):
            by_item[row["item_id"]].append(row)
    max_gid = max(int(g["goods_id"]) for g in bm_rows)
    next_gid = max_gid + 1
    supplements: list[dict] = []

    for item_id, src in sorted(addable.items()):
        if item_id == 535300006:
            continue
        existing = by_item.get(item_id) or []
        on_sale = next((g for g in existing if g.get("on_sale")), None)
        if on_sale:
            continue
        off = existing[0] if existing else None
        goods_id = int(off["goods_id"]) if off else next_gid
        if off is None:
            next_gid += 1
        price = src.get("price")
        currency = src.get("currency")
        if not price or price <= 0 or not currency:
            continue
        name = off.get("item_name") if off else f"BM_G4L_item_{item_id}_b"
        supplements.append(
            {
                "goods_id": goods_id,
                "category_light": src["category"],
                "category_dark": src.get("category_dark") or f"D_{src['category']}",
                "race": "All",
                "on_sale": True,
                "item_name": name,
                "item_id": item_id,
                "item_count": src.get("qty") or 1,
                "currency": currency,
                "price": price,
                "display_order": 100,
                "_g4l_title": src.get("title"),
            }
        )
    return supplements


def build_schedule(products: list[dict], supplements: list[dict], config: dict) -> dict:
    sched_cfg = config.get("schedule") or {}
    by_item: dict[str, dict] = {}
    by_goods: dict[str, dict] = {}
    for p in products:
        if not p.get("item_id") or not p.get("effective_from"):
            continue
        entry = {
            "effective_from": p["effective_from"],
            "effective_to": p["effective_to"],
        }
        if p.get("limit"):
            entry["limit"] = p["limit"]
        by_item[str(p["item_id"])] = entry
    for s in supplements:
        gid = str(s["goods_id"])
        iid = str(s["item_id"])
        if iid in by_item:
            by_goods[gid] = dict(by_item[iid])
    return {
        "source": "Gamers4Life snapshot sale windows (tools/shop_g4l_catalog.py)",
        "prefer_g4l_windows": sched_cfg.get("prefer_g4l_windows", True),
        "default_always_visible": sched_cfg.get("default_always_visible", True),
        "fallback_effective_from": sched_cfg.get("fallback_effective_from"),
        "fallback_effective_to": sched_cfg.get("fallback_effective_to"),
        "by_item_id": by_item,
        "by_goods_id": by_goods,
    }


def build_item_use_patches(supplements: list[dict]) -> dict:
    uses: dict[str, list] = {}
    for row in supplements:
        iid = int(row["item_id"])
        if skin := WEAPON_SKIN_FROM_A2D.get(iid):
            uses[str(iid)] = ["GetSkin", 1, skin]
        elif skin := ACCESSORY_SKIN.get(iid):
            uses[str(iid)] = ["GetSkin", 1, skin]
    return uses


def audit_lists(
    products: list[dict],
    served_items: set[int],
    *,
    uses_int: set[int],
    a2d_skins: dict,
    bm_item_ids: set[int],
    official_item_ids: set[int],
) -> dict:
    all_items = {p["item_id"] for p in products if p.get("item_id")}
    missing = sorted(all_items - served_items)
    kr_only: list[int] = []
    addable_ids: list[int] = []
    for iid in missing:
        if item_verifiable(
            iid,
            uses_int=uses_int,
            a2d_skins=a2d_skins,
            bm_item_ids=bm_item_ids,
            official_item_ids=official_item_ids,
        ):
            addable_ids.append(iid)
        else:
            kr_only.append(iid)
    packages_no_item = [p for p in products if not p.get("item_id") and p.get("package_name")]
    return {
        "g4l_unique_item_ids": len(all_items),
        "g4l_products_deduped": len(products),
        "g4l_packages_without_item_id": len(packages_no_item),
        "missing_from_served_before_build": len(missing),
        "addable_item_ids": addable_ids,
        "kr_only_or_unverified_item_ids": kr_only,
        "packages_without_item_id_titles": sorted({p["title"] for p in packages_no_item}),
    }


def main(argv: list[str] | None = None) -> dict:
    import argparse

    p = argparse.ArgumentParser()
    p.add_argument("--no-fetch", action="store_true", help="skip Aion2Data skins fetch")
    args = p.parse_args(argv)

    products = parse_snapshot(SNAPSHOT.read_text(encoding="utf-8"))
    bm = json.loads(BMSHOP.read_text(encoding="utf-8"))
    config = json.loads(CONFIG.read_text(encoding="utf-8")) if CONFIG.is_file() else {}

    bm_item_ids = {g["item_id"] for g in bm["goods"] if g.get("item_id")}
    base_on_sale_items = {
        g["item_id"] for g in bm["goods"] if g.get("on_sale") and g.get("item_id")
    }
    official_item_ids: set[int] = set()
    official_path = ROOT / "tools/data/official_shop.json"
    if official_path.is_file():
        for g in json.loads(official_path.read_text(encoding="utf-8")).get("goods") or []:
            for it in g.get("items") or []:
                k = it.get("game_item_key") or it.get("item_id")
                if k:
                    official_item_ids.add(int(k))

    uses_int, a2d_skins = verification_sets(fetch_skins=not args.no_fetch)

    bm_by_item: dict[int, list[dict]] = defaultdict(list)
    for row in bm["goods"]:
        if row.get("item_id"):
            bm_by_item[row["item_id"]].append(row)

    by_item_product = best_product_by_item(products)
    covered_items = base_on_sale_items | official_item_ids
    missing_before = set(by_item_product) - covered_items
    addable: dict[int, dict] = {}
    excluded_no_price: list[dict] = []
    for iid in sorted(missing_before):
        reasons = item_verifiable(
            iid,
            uses_int=uses_int,
            a2d_skins=a2d_skins,
            bm_item_ids=bm_item_ids,
            official_item_ids=official_item_ids,
        )
        if not reasons or iid == 535300006:
            continue
        priced = priced_product(by_item_product.get(iid), bm_by_item)
        if priced:
            addable[iid] = priced
        else:
            excluded_no_price.append({
                "item_id": iid,
                "title": (by_item_product.get(iid) or {}).get("title"),
                "reason": "no_g4l_or_bmshop_price",
            })

    supplements = build_supplements(products, addable=addable, bm_rows=bm["goods"])
    served_items = set()
    if SERVED.is_file():
        served_items = served_item_ids(json.loads(SERVED.read_text(encoding="utf-8")))
    audit = audit_lists(
        products,
        served_items or covered_items,
        uses_int=uses_int,
        a2d_skins=a2d_skins,
        bm_item_ids=bm_item_ids,
        official_item_ids=official_item_ids,
    )
    audit["excluded_verifiable_no_g4l_price"] = excluded_no_price

    use_patches = build_item_use_patches(supplements)
    write_data_json(
        OUT_PRODUCTS,
        {
            "source": "derived from tools/data/snapshots/gamers4life-shop.txt",
            "product_count": len(products),
            "products": products,
            "audit": audit,
        },
    )
    write_data_json(OUT_SCHEDULE, build_schedule(products, supplements, config))
    write_data_json(
        OUT_SUPPLEMENTS,
        {
            "source": "G4L products missing from served catalog but verifiable locally (tools/shop_g4l_catalog.py)",
            "goods": supplements,
            "uses": use_patches,
            "audit": audit,
        },
    )

    print(
        f"g4l products={len(products)} supplements={len(supplements)} "
        f"addable={len(addable)} excluded_no_price={len(excluded_no_price)} "
        f"kr_only={len(audit['kr_only_or_unverified_item_ids'])} "
        f"packages_no_item={audit['g4l_packages_without_item_id']}"
    )
    return {
        "products": len(products),
        "supplements": len(supplements),
        "audit": audit,
    }


if __name__ == "__main__":
    main()