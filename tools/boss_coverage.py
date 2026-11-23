"""Per-boss coverage: client behavior cases, phase script source, mechanics engine path, parts, gimmick gauges.

engine: "authored" (tools/data/boss-mechanics encounter), "compiled" (boss-phases.json script run as engine rules) or
"legacy" (a script naming a hand-coded mechanic with no authored encounter; docs/BOSS-MECHANICS-ENGINE.md).

    python tools/boss_coverage.py
    python tools/boss_coverage.py --json tools/data/boss-coverage.json
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BOSS_PHASES = ROOT / "tools/data/boss-phases.json"
BEHAVIOR = ROOT / "tools/data/npc-behavior-cases.json"
WORLD = ROOT / "tools/data/world-npcs.json"
ENCOUNTERS = ROOT / "tools/data/boss-mechanics"


def authored_npcs() -> dict[str, str]:
    """NpcData id (as a string) to the encounter file that scripts it."""
    out = {}
    for path in sorted(ENCOUNTERS.glob("*.json")):
        enc = json.loads(path.read_text(encoding="utf-8"))
        for ids in (enc.get("npcs") or {}).values():
            for npc in ids:
                out[str(npc)] = path.name
    return out


def engine_path(npc_id: str, script: dict, authored: dict[str, str]) -> str:
    if npc_id in authored:
        return "authored"
    slams = [script.get("telegraph")] + list(script.get("telegraphs") or []) + [p.get("telegraph") for p in script.get("phases") or []]
    return "legacy" if any(s and s.get("mechanic") for s in slams) else "compiled"


def spawn_names_for_npc(world: dict, npc_id: str) -> set[str]:
    names = set()
    for spawns in (world.get("maps") or {}).values():
        for s in spawns:
            if str(s[1]) == npc_id:
                names.add(s[0])
    return names


def phase_source(script: dict) -> str:
    src = script.get("source") or "OURS"
    if src == "official-overlay":
        return "official-overlay"
    if script.get("behavior_client"):
        return "client-behavior+OURS"
    return "OURS"


def build_report() -> dict:
    bosses = json.loads(BOSS_PHASES.read_text(encoding="utf-8"))["bosses"]
    behavior = json.loads(BEHAVIOR.read_text(encoding="utf-8")) if BEHAVIOR.is_file() else {"by_npc_id": {}, "monster_parts": {}, "gimmick_gauges": {}}
    by_npc = behavior.get("by_npc_id") or {}
    parts = behavior.get("monster_parts") or {}
    world = json.loads(WORLD.read_text(encoding="utf-8"))
    rows = []
    counts = {"OURS": 0, "official-overlay": 0, "client-behavior+OURS": 0, "unscripted": 0}
    authored = authored_npcs()
    engine_counts = {"authored": 0, "compiled": 0, "legacy": 0}
    for npc_id, script in sorted(bosses.items(), key=lambda kv: int(kv[0])):
        spawns = sorted(spawn_names_for_npc(world, npc_id))
        cases = by_npc.get(npc_id) or []
        case_kinds = sorted({c["case"] for c in cases})
        src = phase_source(script)
        counts[src] = counts.get(src, 0) + 1
        engine = engine_path(npc_id, script, authored)
        engine_counts[engine] += 1
        rows.append({
            "npc_id": int(npc_id),
            "spawn_names": spawns,
            "dungeon": script.get("dungeon"),
            "role": script.get("role"),
            "phase_source": src,
            "engine": engine,
            "encounter_file": authored.get(npc_id),
            "behavior_case_kinds": case_kinds,
            "behavior_case_count": len(cases),
            "monster_parts": parts.get(npc_id),
            "has_client_behavior": bool(cases),
        })
    return {
        "source": "tools/boss_coverage.py",
        "boss_count": len(rows),
        "phase_source_counts": counts,
        "engine_counts": engine_counts,
        "bosses": rows,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", help="Write report JSON to this path")
    args = parser.parse_args()
    if not BEHAVIOR.is_file():
        print("Run tools/npc_behavior_cases.py first", file=sys.stderr)
        return 1
    report = build_report()
    if args.json:
        path = Path(args.json)
        path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"wrote {path.relative_to(ROOT)} ({report['boss_count']} bosses)")
    else:
        for src, n in sorted(report["phase_source_counts"].items()):
            print(f"{src}: {n}")
        for path, n in report["engine_counts"].items():
            print(f"engine {path}: {n}")
        with_behavior = sum(1 for b in report["bosses"] if b["has_client_behavior"])
        print(f"bosses={report['boss_count']} with_behavior_cases={with_behavior}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())