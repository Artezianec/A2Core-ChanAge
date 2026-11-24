"""Amplify (◆ ExceedEnchant) export for the server.

Sources (first match wins per section):
  - research/local/exceed-enchant-*/tables/table-000.json  ExceedEnchant.dat (steps / odds / costs)
  - research/local/exceed-enchant-*/tables/table-001.json AdditionalStat.dat (per-◆ stat rows)
  - research/local/game-catalog-v2/entries.jsonl             Item.ExceedEnchantGroup (optional items map)
  - research/client-export/2026-11-22/Item.json.gz         Item.ExceedEnchantGroup (checked in)

When ExceedEnchant.dat is absent, step ladders marked ``candidate_steps`` use the pinned Global ◆0..◆5
ladder (aion2.gg / existing exceed_enchant.json). Effects without AdditionalStat use ``candidate_effects``
scaled from ``effect_reference`` (same as pre-export GameSession.ExceedEnchantTables).

Rebuild: ``python tools/exceed_enchant_data.py`` → tools/data/exceed_enchant.json
"""
from __future__ import annotations

import json
import os
import re
import sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / "tools/data/exceed_enchant.json"
ENCHANT_ITEMS = ROOT / "tools/data/enchant.json"
CATALOG = ROOT / "research/local/game-catalog-v2/entries.jsonl"
LOCAL = Path(os.environ.get("AION_RESEARCH_LOCAL", ROOT / "research/local"))

PINNED_STEPS = [
    [6600, 500, 0, 600_000, 6],
    [5000, 400, 0, 900_000, 9],
    [3300, 300, 0, 1_400_000, 12],
    [2500, 200, 0, 2_000_000, 15],
    [2000, 100, 0, 3_000_000, 20],
]
MAX_SURPASS_BY_ITEM_LEVEL = [[54, 1], [70, 3], [86, 5]]
AMPLIFY_STONE = 930100024
EFFECT_REFERENCE_LEVEL = 102
EFFECT_REFERENCE = {
    "Weapon": {
        "WeaponMinDamage": 73,
        "WeaponDamage": 73,
        "AmplifyWeaponDamage": 500,
    },
    "Armor": {"ArmorDefense": 149, "HPMax": 149},
    "Accessory": {
        "WeaponDamage": 36,
        "ArmorDefense": 73,
        "AmplifyAllDamage": 250,
    },
}


def _value(v):
    return v["Value"] if isinstance(v, dict) and "Value" in v else v


def _short(value) -> str:
    return str(value).split("::")[-1]


def _rows(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))["Properties"]["Data"]


def _optional_tables():
    dirs = sorted(LOCAL.glob("exceed-enchant-*/tables"))
    return dirs[-1] if dirs else None


def _max_surpass(item_level: int) -> int:
    cap = 0
    for threshold, max_v in sorted(MAX_SURPASS_BY_ITEM_LEVEL):
        if item_level >= threshold:
            cap = max_v
    return cap


def _ilvl_from_group(name: str) -> int | None:
    m = re.search(r"_(\d+)(?:_|$)", name)
    return int(m.group(1)) if m else None


def _effect_bucket(group_or_category: str) -> str:
    name = group_or_category
    if name.startswith("Accessory_") or name in (
        "Belt",
        "Amulet",
        "Necklace",
        "Ring",
        "Earring",
        "Bracelet",
        "Pendant",
        "Brooch",
    ):
        return "Accessory"
    if name.startswith("Armor_"):
        return "Armor"
    if name.startswith("Weapon_") or name in (
        "Greatsword",
        "Longsword",
        "Shortsword",
        "Mace",
        "Orb",
        "Spellbook",
        "Bow",
        "Gun",
        "Harp",
        "Keyblade",
    ):
        return "Weapon"
    return "Armor"


def _load_exceed_items_from_client() -> dict[str, list[str]]:
    sys.path.insert(0, str(ROOT / "tools"))
    from client_export import load_rows  # noqa: WPS433

    enchantable = set(json.loads(ENCHANT_ITEMS.read_text(encoding="utf-8"))["items"])
    items: dict[str, list[str]] = {}
    for row in load_rows("Item"):
        iid = str(_value(row["ID"]))
        if iid not in enchantable:
            continue
        group = _short(row.get("ExceedEnchantGroup") or "None")
        if group in ("None", ""):
            continue
        effect = _short(row.get("EnchantEffectGroup") or group)
        items[iid] = [group, effect]
    return items


def _load_exceed_items_from_catalog(ids: dict[str, int]) -> dict[str, list[str]]:
    if not CATALOG.is_file():
        return {}
    enchantable = set(json.loads(ENCHANT_ITEMS.read_text(encoding="utf-8"))["items"])
    items: dict[str, list[str]] = {}
    with CATALOG.open(encoding="utf-8") as source:
        for line in source:
            entry = json.loads(line)
            if entry.get("kind") != "items":
                continue
            iid = str(entry["id"])
            if iid not in enchantable:
                continue
            raw = entry.get("raw") or {}
            group = raw.get("ExceedEnchantGroup")
            if not group or group == "None":
                continue
            effect = raw.get("EnchantEffectGroup") or group
            items[iid] = [group, effect]
    return items


def _parse_step_row(row, ids: dict[str, int]) -> list:
    costs = [
        [ids[_short(c["Item"])], int(c["Count"])]
        for c in row.get("CostItems") or []
        if _short(c.get("Item")) in ids and int(c.get("Count") or 0) > 0
    ]
    level = row.get("CurrentLevel", row.get("SurpassLevel", row.get("Level")))
    return [
        int(row["SuccessProb"]),
        int(row.get("FailCorrectionProb") or 0),
        int(row.get("FailPenalty") or 0),
        int(row.get("CostGold") or 0),
        costs,
        int(level) if level is not None else 0,
    ]


def _build_groups_from_dat(tables: Path, ids: dict[str, int]) -> tuple[dict[str, list], dict[str, int]]:
    path = tables / "table-000.json"
    if not path.is_file():
        return {}, {}
    groups: dict[str, list] = defaultdict(list)
    caps: dict[str, int] = {}
    for row in sorted(_rows(path), key=lambda r: (r["Group"], _value(r.get("CurrentLevel", r.get("SurpassLevel", 0))))):
        group = row["Group"]
        parsed = _parse_step_row(row, ids)
        groups[group].append(parsed[:5])
        cap_field = row.get("MaxLevel", row.get("MaxSurpassLevel"))
        if cap_field is not None:
            caps[group] = int(_value(cap_field))
    return dict(groups), caps


def _build_effects_from_dat(tables: Path) -> dict[str, list]:
    path = tables / "table-001.json"
    if not path.is_file():
        return {}
    effects: dict[str, list] = defaultdict(list)
    for row in sorted(_rows(path), key=lambda r: (r["Group"], r.get("Level", 0))):
        stats = [
            [_short(s["StatType"]), int(s["StatValue"])]
            for s in row.get("StatList") or row.get("AdditionalStatInfo") or []
        ]
        level = int(row.get("Level") or row.get("SurpassLevel") or 0)
        rows = effects.setdefault(row["Group"], [])
        while len(rows) < level - 1:
            rows.append([0, []])
        rows.append([int(row.get("ItemLevelValue") or 0), stats])
    return dict(effects)


def _candidate_effects(effect_group: str, cap: int, ref_level: int) -> list:
    bucket = _effect_bucket(effect_group)
    ref = EFFECT_REFERENCE[bucket]
    ref_cap = _max_surpass(ref_level)
    rows = []
    for surpass in range(1, cap + 1):
        stats = [
            [k, int(round(v * ref_level / EFFECT_REFERENCE_LEVEL / ref_cap * surpass))]
            for k, v in ref.items()
        ]
        rows.append([0, stats])
    return rows


def build():
    ids: dict[str, int] = {}
    if CATALOG.is_file():
        with CATALOG.open(encoding="utf-8") as source:
            for line in source:
                entry = json.loads(line)
                if entry.get("kind") == "items":
                    ids[entry["internal_name"]] = entry["id"]

    items = _load_exceed_items_from_catalog(ids)
    if not items:
        items = _load_exceed_items_from_client()

    tables = _optional_tables()
    dat_groups, dat_caps = ({}, {})
    dat_effects: dict[str, list] = {}
    missing_tables = ["ExceedEnchant.dat", "AdditionalStat.dat"]
    if tables:
        dat_groups, dat_caps = _build_groups_from_dat(tables, ids)
        dat_effects = _build_effects_from_dat(tables)
        missing_tables = [t for t, p in (
            ("ExceedEnchant.dat", tables / "table-000.json"),
            ("AdditionalStat.dat", tables / "table-001.json"),
        ) if not p.is_file()]

    used_groups = {g for g, _ in items.values()}
    group_item_levels: dict[str, list[int]] = defaultdict(list)
    sys.path.insert(0, str(ROOT / "tools"))
    from client_export import load_rows  # noqa: WPS433

    level_by_id = {str(_value(r["ID"])): int(_value(r.get("ItemLevel") or 0)) for r in load_rows("Item")}
    for iid, (grp, _) in items.items():
        if level_by_id.get(iid):
            group_item_levels[grp].append(level_by_id[iid])

    groups: dict[str, list] = {}
    group_caps: dict[str, int] = {}
    effects: dict[str, list] = {}
    candidate_groups: list[str] = []
    candidate_effects: list[str] = []

    for group in sorted(used_groups):
        if group in dat_groups:
            groups[group] = dat_groups[group]
            if group in dat_caps:
                group_caps[group] = dat_caps[group]
        else:
            groups[group] = [step[:] for step in PINNED_STEPS]
            candidate_groups.append(group)

        cap = dat_caps.get(group)
        if cap is None:
            ilvl = _ilvl_from_group(group) or (
                max(group_item_levels[group]) if group_item_levels[group] else EFFECT_REFERENCE_LEVEL
            )
            cap = _max_surpass(ilvl)
        group_caps[group] = cap

        effect_group = next(e for g, e in items.values() if g == group)
        if effect_group in dat_effects:
            effects[effect_group] = dat_effects[effect_group]
        elif effect_group not in effects:
            effects[effect_group] = _candidate_effects(effect_group, group_caps[group], ilvl if (ilvl := _ilvl_from_group(group)) else EFFECT_REFERENCE_LEVEL)
            candidate_effects.append(effect_group)

    source_bits = [
        "tools/exceed_enchant_data.py",
        "Item.ExceedEnchantGroup via client-export Item.json.gz",
    ]
    if tables:
        source_bits.append(f"dat tables {tables.relative_to(ROOT)}")
    if candidate_groups:
        source_bits.append(
            f"CANDIDATE: {len(candidate_groups)} group step ladders use pinned Global ◆0..◆5 until ExceedEnchant.dat"
        )
    if missing_tables:
        source_bits.append(f"missing WorldProbe export: {', '.join(missing_tables)} under research/local/exceed-enchant-*/tables/")

    return {
        "source": "; ".join(source_bits),
        "amplify_stone_item": AMPLIFY_STONE,
        "unique_enchant_group_prefix": "Enchant_Unique_",
        "items": items,
        "groups": groups,
        "effects": effects,
        "group_caps": group_caps,
        "max_surpass_by_item_level": MAX_SURPASS_BY_ITEM_LEVEL,
        "steps": PINNED_STEPS,
        "effect_reference_level": EFFECT_REFERENCE_LEVEL,
        "effect_reference": EFFECT_REFERENCE,
        "export_notes": {
            "item_count": len(items),
            "group_count": len(groups),
            "effect_group_count": len(effects),
            "dat_group_count": len([g for g in used_groups if g in dat_groups]),
            "candidate_step_groups": candidate_groups,
            "candidate_effect_groups": candidate_effects,
            "missing_tables": missing_tables,
            "rebuild_probe": "WorldProbe ExceedEnchant.dat + AdditionalStat.dat → research/local/exceed-enchant-<date>/tables/",
        },
    }


def main():
    sys.path.insert(0, str(ROOT / "tools"))
    from data_json import write_data_json  # noqa: WPS433

    data = build()
    write_data_json(DATA, data)
    notes = data["export_notes"]
    print(
        f"{notes['item_count']} items, {notes['group_count']} exceed groups "
        f"({notes['dat_group_count']} from dat, {len(notes['candidate_step_groups'])} CANDIDATE steps) "
        f"→ {DATA.relative_to(ROOT)}"
    )


if __name__ == "__main__":
    main()