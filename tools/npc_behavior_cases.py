"""NpcBehaviorCase (+ GimmickGauge, MonsterPartsData) from the client export -> tools/data/npc-behavior-cases.json.

Rows are keyed by MapData spawn name (NpcName) and resolved NpcData ids from tools/data/world-npcs.json.
Case kinds match ENpcBehaviorCase (CombatStart, Casting, SkillUse, Abnormal, Die, …); Value holds skill id,
abnormal id, or auxiliary data; Prob is weight out of 10000.

    python tools/npc_behavior_cases.py
    python tools/npc_behavior_cases.py --check
"""
from __future__ import annotations

import json
import sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
from client_export import load_rows  # noqa: E402

OUT = ROOT / "tools/data/npc-behavior-cases.json"
WORLD = ROOT / "tools/data/world-npcs.json"
BOSS_PHASES = ROOT / "tools/data/boss-phases.json"

CASE_SHORT = {
    "ENpcBehaviorCase::CombatStart": "combat_start",
    "ENpcBehaviorCase::Idle": "idle",
    "ENpcBehaviorCase::Casting": "casting",
    "ENpcBehaviorCase::Die": "die",
    "ENpcBehaviorCase::SkillUse": "skill_use",
    "ENpcBehaviorCase::Abnormal": "abnormal",
    "ENpcBehaviorCase::FuncInteractionStart": "func_start",
    "ENpcBehaviorCase::FuncInteractionEnd": "func_end",
    "ENpcBehaviorCase::SpawnStart": "spawn_start",
    "ENpcBehaviorCase::SpawnEnd": "spawn_end",
}


def spawn_index(world: dict) -> tuple[dict[str, set[int]], dict[int, set[str]]]:
    """Spawn name -> npc ids; npc id -> spawn names."""
    by_name: dict[str, set[int]] = defaultdict(set)
    by_npc: dict[int, set[str]] = defaultdict(set)
    for _map, spawns in (world.get("maps") or {}).items():
        for name, npc_id, *_ in spawns:
            by_name[name].add(int(npc_id))
            by_npc[int(npc_id)].add(name)
    return dict(by_name), dict(by_npc)


def row_entry(row: dict) -> dict:
    case = CASE_SHORT.get(row.get("Case") or "", (row.get("Case") or "").split("::")[-1].lower())
    value = row.get("Value") or ""
    prob = int(row.get("Prob") or 10000)
    talk = row.get("Talk") or ""
    act = row.get("Act") if row.get("Act") not in (None, "None", "") else None
    skill = int(value) if case in ("casting", "skill_use") and str(value).isdigit() else None
    abnormal = int(value) if case == "abnormal" and str(value).isdigit() else None
    return {
        "case": case,
        "value": value,
        "prob": prob,
        "talk": talk,
        **({"skill": skill} if skill else {}),
        **({"abnormal": abnormal} if abnormal else {}),
        **({"act": act} if act else {}),
    }


def build_cases(rows: list, by_name: dict[str, set[int]], by_npc: dict[int, set[str]]):
    by_spawn: dict[str, list] = defaultdict(list)
    by_npc_out: dict[str, list] = defaultdict(list)
    for row in rows:
        name = row.get("NpcName") or ""
        if not name:
            continue
        entry = row_entry(row)
        by_spawn[name].append(entry)
        for npc in by_name.get(name, ()):
            by_npc_out[str(npc)].append(entry)
    # Dedupe npc lists (same row via multiple spawn aliases)
    for npc, items in list(by_npc_out.items()):
        seen = set()
        deduped = []
        for item in items:
            key = (item["case"], item.get("skill"), item.get("abnormal"), item["talk"], item["value"])
            if key in seen:
                continue
            seen.add(key)
            deduped.append(item)
        by_npc_out[npc] = deduped
    return dict(sorted(by_spawn.items())), dict(sorted(by_npc_out.items(), key=lambda kv: int(kv[0])))


def build_gimmick(rows: list) -> dict[str, dict]:
    out = {}
    for row in rows:
        rid = row["ID"]["Value"]
        thresholds = [int(x) for x in (row.get("GaugeValueList") or []) if str(x).isdigit()]
        out[str(rid)] = {
            "type": (row.get("GaugeType") or "").split("::")[-1],
            "show": (row.get("GaugeShowType") or "").split("::")[-1],
            "name_key": (row.get("GaugeName") or {}).get("Key"),
            "thresholds": thresholds,
        }
    return out


def build_parts(rows: list) -> dict[str, list]:
    by_npc: dict[str, list] = defaultdict(list)
    for row in rows:
        npc = row.get("NpcId", {}).get("Value")
        if not npc:
            continue
        by_npc[str(npc)].append({
            "parts_id": int(row.get("PartsId") or 0),
            "max_hp": int(row.get("MaxHP") or 0),
            "desc_key": (row.get("PartsDesc") or {}).get("Key"),
        })
    return dict(sorted(by_npc.items(), key=lambda kv: int(kv[0])))


def main(argv: list[str]) -> int:
    world = json.loads(WORLD.read_text(encoding="utf-8"))
    by_name, by_npc = spawn_index(world)
    behavior_rows = load_rows("NpcBehaviorCase")
    by_spawn, by_npc_cases = build_cases(behavior_rows, by_name, by_npc)
    gimmick = build_gimmick(load_rows("GimmickGauge"))
    parts = build_parts(load_rows("MonsterPartsData"))
    scripted = set((json.loads(BOSS_PHASES.read_text(encoding="utf-8")).get("bosses") or {}))
    with_cases = {npc for npc in scripted if npc in by_npc_cases}
    data = {
        "source": "NpcBehaviorCase, GimmickGauge, MonsterPartsData (client export 2026-11-22; tools/npc_behavior_cases.py)",
        "by_spawn_name": by_spawn,
        "by_npc_id": by_npc_cases,
        "gimmick_gauges": gimmick,
        "monster_parts": parts,
        "stats": {
            "behavior_rows": len(behavior_rows),
            "spawn_names_with_cases": len(by_spawn),
            "npc_ids_with_cases": len(by_npc_cases),
            "scripted_bosses_with_cases": len(with_cases),
        },
    }
    text = json.dumps(data, indent=2, ensure_ascii=False) + "\n"
    if "--check" in argv:
        if not OUT.exists() or OUT.read_text(encoding="utf-8") != text:
            print(f"{OUT.relative_to(ROOT)} is stale: run python tools/npc_behavior_cases.py")
            return 1
        return 0
    OUT.write_text(text, encoding="utf-8")
    print(f"behavior spawns={len(by_spawn)} npc_ids={len(by_npc_cases)} "
          f"parts={len(parts)} gauges={len(gimmick)} -> {OUT.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))