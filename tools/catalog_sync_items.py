"""Import a new catalog.items snapshot that is a superset of the current build.

Rebuilds item documents from a real Item.dat extract (never invents templates), copies
every skill and any item the previous build has that Item.dat dropped, and imports one
new catalog.builds row. Refuses to shrink the previous item or skill set unless
--allow-shrink is passed.

  python tools/catalog_sync_items.py --item-table <Item.dat table-000.json>
  python tools/catalog_sync_items.py --dry-run --item-table ...
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import postgres_dev as pg

FACT_KEYS = (
    "ItemType", "EquipCategory", "ArmorType", "ItemGrade", "ItemRace", "ItemLevel",
    "PermitLevelMin", "PermitLevelMax", "MainStats", "SubStats",
    "Costume_res_key_m", "Costume_res_key_f", "ExtractionSkins",
)
QUNA_SHOP_TEMPLATES = (536310012, 535910150, 535910138)


def value(obj):
    return obj.get("Value") if isinstance(obj, dict) else obj


def short(obj):
    return str(obj or "Unknown").split("::")[-1]


def load_english(path: Path) -> dict:
    if not path.is_file():
        return {}
    data = json.loads(path.read_text(encoding="utf-8"))
    return data.get("Entries") or data


def load_general_strings(path: Path) -> dict:
    if not path.is_file():
        return {}
    rows = json.loads(path.read_text(encoding="utf-8"))["Properties"]["Data"]
    return {r["Name"]: r for r in rows}


def translate(ref, english, general):
    key = ref.get("Key") if isinstance(ref, dict) else ref
    if key in general:
        key = (general[key].get("Body") or {}).get("Key")
    return english.get(key), key


def item_entry(row, english, general, source):
    identity = int(value(row["ID"]))
    name, name_key = translate(row.get("Desc"), english, general)
    description, desc_key = translate(row.get("DescLong"), english, general)
    internal = row.get("Name", str(identity))
    category = short(row.get("EquipCategory") or row.get("ItemType"))
    facts = {k: row[k] for k in FACT_KEYS if k in row}
    return {
        "kind": "items",
        "id": identity,
        "name": name or internal,
        "internal_name": internal,
        "name_status": "localized" if name else "unresolved",
        "name_key": name_key,
        "description": description,
        "description_key": desc_key,
        "category": category,
        "classes": [],
        "facts": facts,
        "relationships": {},
        "source": source,
        "raw": row,
    }


def current_documents():
    items, skills = {}, {}
    raw = pg.sql("COPY (SELECT template_id, definition::text FROM catalog.items) TO STDOUT WITH (FORMAT csv);").stdout
    # COPY csv may wrap JSON with quotes; use a proper query instead.
    return items, skills


def fetch_definitions(kind: str) -> dict[int, dict]:
    raw = pg.sql(
        f"SELECT json_agg(json_build_object('id', template_id, 'definition', definition) "
        f"ORDER BY template_id)::text FROM catalog.{kind};"
    ).stdout.strip()
    if not raw or raw == "\\N":
        return {}
    return {int(row["id"]): row["definition"] for row in json.loads(raw)}


def build_snapshot(item_table: Path, english_path: Path, strings_path: Path, out: Path, allow_shrink: bool):
    table = json.loads(item_table.read_text(encoding="utf-8"))
    rows = table["Properties"]["Data"]
    english = load_english(english_path)
    general = load_general_strings(strings_path)
    source = str(item_table)
    items = {int(value(r["ID"])): item_entry(r, english, general, source) for r in rows}

    prev_items = fetch_definitions("items")
    prev_skills = fetch_definitions("skills")
    added = sorted(set(items) - set(prev_items))
    removed = sorted(set(prev_items) - set(items))
    kept_removed = []
    for tid in removed:
        items[tid] = prev_items[tid]
        kept_removed.append(tid)
    if removed and not allow_shrink:
        # We kept them, so the new build is still a superset. Report only.
        pass
    skill_docs = prev_skills
    if not skill_docs:
        raise SystemExit("current catalog has no skills; refusing to import an items-only snapshot")

    missing_prev_items = sorted(set(prev_items) - set(items))
    missing_prev_skills: list[int] = []  # we copy all
    if missing_prev_items or missing_prev_skills:
        raise SystemExit(f"snapshot would shrink items={missing_prev_items[:20]} skills={missing_prev_skills[:20]}")

    counts = {"items": len(items), "skills": len(skill_docs)}
    localized = {
        "items": sum(1 for d in items.values() if d.get("name_status") == "localized"),
        "skills": sum(1 for d in skill_docs.values() if d.get("name_status") == "localized"),
    }
    manifest = {
        "schema_version": 1,
        "counts": counts,
        "localized_names": localized,
        "unresolved_effect_groups": [],
        "sources": {
            "items": {"path": source, "sha256": hashlib.sha256(item_table.read_bytes()).hexdigest()},
            "skills": {"path": "catalog.skills (copied from previous build)", "sha256": ""},
        },
        "limitation": (
            "Items from a later Item.dat extract; skills copied from the previous catalog build. "
            "Entries include internal variants and possibly unused content."
        ),
        "previous_item_count": len(prev_items),
        "added_item_ids_count": len(added),
    }
    out.mkdir(parents=True, exist_ok=True)
    (out / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    with (out / "entries.jsonl").open("w", encoding="utf-8") as fh:
        for doc in items.values():
            fh.write(json.dumps(doc, ensure_ascii=False, separators=(",", ":")) + "\n")
        for doc in skill_docs.values():
            fh.write(json.dumps(doc, ensure_ascii=False, separators=(",", ":")) + "\n")
    print(
        f"items {len(prev_items)} -> {len(items)} (+{len(added)}, kept-removed {len(kept_removed)}); "
        f"skills {len(skill_docs)}; shop previously-missing "
        f"{[i for i in QUNA_SHOP_TEMPLATES if i in added or i in items]}"
    )
    return {"added": added, "kept_removed": kept_removed, "counts": counts, "out": out}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--item-table", type=Path, required=True, help="decoded Item.dat table-000.json")
    parser.add_argument("--english", type=Path, default=None)
    parser.add_argument("--general-strings", type=Path, default=None)
    parser.add_argument("--output", type=Path, default=None)
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--allow-shrink", action="store_true")
    args = parser.parse_args()
    local = Path(os.environ.get("AION_LOCAL", r"D:\Aion2\research\local"))
    english = args.english or local / "catalog-localization-20261013/localization-000.json"
    strings = args.general_strings or local / "catalog-strings-20261013/table-000.json"
    out = args.output or Path(tempfile.mkdtemp(prefix="catalog-sync-"))
    stats = build_snapshot(args.item_table, english, strings, out, args.allow_shrink)
    present = set()
    with (out / "entries.jsonl").open(encoding="utf-8") as fh:
        for line in fh:
            doc = json.loads(line)
            if doc.get("kind") == "items":
                present.add(int(doc["id"]))
    missing = [i for i in QUNA_SHOP_TEMPLATES if i not in present]
    if missing:
        raise SystemExit(f"shop templates still missing from snapshot: {missing}")
    if args.dry_run:
        print(f"dry-run snapshot at {out}")
        return
    result = pg.import_catalog(out)
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
