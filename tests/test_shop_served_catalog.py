"""Served BM catalog: every on-sale client tab is filled, Quna top-up is hidden."""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import shop_served_catalog as served

def _official_free_goods_ids() -> set[int]:
    return served.official_intentional_free_goods()


def test_served_goods_have_positive_price_unless_official_free():
    data = json.loads((ROOT / "tools/data/official_shop.served.json").read_text(encoding="utf-8"))
    free_ids = _official_free_goods_ids()
    offenders = []
    for g in data["goods"] + data["me_goods"]:
        gid = int(g["goods_id"])
        pols = g.get("goods_basic_price_policies") or []
        if not pols:
            continue
        price = served.dec_price(pols[0].get("goods_basic_price"))
        if price is not None and price <= 0 and gid not in free_ids:
            offenders.append(gid)
    assert offenders == [], offenders


def test_served_catalog_covers_on_sale_tabs_and_hides_quna_topup():
    # Rebuild is idempotent; the committed file must match a fresh run.
    stats = served.build()
    data = json.loads((ROOT / "tools/data/official_shop.served.json").read_text(encoding="utf-8"))
    bm = json.loads((ROOT / "tools/data/bmshop_goods.json").read_text(encoding="utf-8"))
    ids = {int(g["goods_id"]) for g in data["goods"]}
    assert stats["missing_on_sale"] == []
    assert data["revision_id"].endswith("-served")
    assert len(data["goods"]) >= 190
    assert len(data["me_goods"]) == 18
    # Membership is Quna at the platform price.
    membership = next(g for g in data["goods"] if g["goods_id"] == "100024")
    assert membership["goods_basic_price_policies"][0] == {
        "currency_group_id": "145", "goods_basic_price": "25000.0000"
    }
    # No Quna top-up remains.
    assert "100003" not in {g["goods_id"] for g in data["goods"] + data["me_goods"]}
    assert "100133" not in {g["goods_id"] for g in data["goods"]}
    for g in data["goods"]:
        for it in g.get("items") or []:
            assert it.get("game_item_key") != "535300006"
    # Every on-sale client tab except Daily_RechargeQuna has at least one visible row.
    on_sale = [g for g in bm["goods"] if g.get("on_sale")]
    by = {}
    for g in on_sale:
        by.setdefault(g.get("category_light") or "None", []).append(g)
    free_ids = _official_free_goods_ids()
    # Tabs the served catalog fills (an official capture row may fill the tab; the client's duplicate row is folded).
    served_tabs = set()
    for g in data["goods"]:
        for part in str(g.get("goods_data") or "").split(","):
            if part.startswith(("catl:", "catd:")):
                served_tabs.add(part.split(":", 1)[1])
    for cat, rows in by.items():
        visible = sum(1 for g in rows if int(g["goods_id"]) in ids)
        if cat in ("Daily_RechargeQuna", "D_Daily_RechargeQuna"):
            assert visible == 0
            continue
        # Client rows with no G4L/official price stay out of the served catalog (not freebies).
        priced_rows = [
            g for g in rows
            if int(g.get("price") or 0) > 0 or int(g["goods_id"]) in free_ids
        ]
        if priced_rows:
            assert visible >= 1 or cat in served_tabs, (cat, len(priced_rows), visible)
    groups = { (g.get("goods_basic_price_policies") or [{}])[0].get("currency_group_id") for g in data["goods"] }
    assert groups <= {"145", "159"}
    for g in data["goods"] + data["me_goods"]:
        group = (g.get("goods_basic_price_policies") or [{}])[0].get("currency_group_id")
        if group == "145":
            assert not g.get("pgs"), g.get("goods_id")
    for gid, item in (("100158", "536310034"), ("100167", "535910138"), ("100125", "536310092")):
        row = next(g for g in data["goods"] if g["goods_id"] == gid)
        assert [it.get("game_item_key") for it in row["items"]] == [item]
        assert row["goods_basic_price_policies"][0]["currency_group_id"] == "145"
        assert not row.get("pgs")


def test_served_goods_do_not_repeat_an_item_price_and_tab():
    """No two products show the same items at the same price in the same tabs (the _p_/_s_ pairs are folded)."""
    data = json.loads((ROOT / "tools/data/official_shop.served.json").read_text(encoding="utf-8"))
    seen: dict[tuple, str] = {}
    for g in data["goods"]:
        sig = served.served_signature(g)
        assert sig not in seen, (seen.get(sig), g["goods_id"], sig)
        seen[sig] = g["goods_id"]
