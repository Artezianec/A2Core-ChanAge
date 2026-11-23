"""Verify tools/data/bosschallenge.json against BossChallenge* client export tables.

    python tools/bosschallenge_export_verify.py
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
from client_export import load_rows  # noqa: E402

DATA = ROOT / "tools/data/bosschallenge.json"


def group_id(row: dict) -> int:
    return int(row["ID"]["Value"])


def main() -> int:
    if not DATA.is_file():
        print("Missing bosschallenge.json; run tools/bosschallenge_data.py build", file=sys.stderr)
        return 1
    built = json.loads(DATA.read_text(encoding="utf-8"))
    export_groups = {group_id(r): r for r in load_rows("BossChallengeGroup") if r.get("bApply")}
    export_contents: dict[str, dict[str, dict]] = {}
    for row in load_rows("BossChallengeContents"):
        if not row.get("bApply"):
            continue
        gname = row["GroupName"]
        export_contents.setdefault(gname, {})[str(row["Level"])] = row
    problems = []
    for gid, g in built.get("groups", {}).items():
        name = g.get("n")
        if name not in {r["Name"] for r in export_groups.values()}:
            problems.append(f"group id {gid}: name {name!r} not in export groups")
    for gname, levels in built.get("contents", {}).items():
        exp_levels = export_contents.get(gname)
        if exp_levels is None:
            problems.append(f"contents {gname}: missing from export")
            continue
        for level, spec in levels.items():
            if level not in exp_levels:
                problems.append(f"{gname} level {level}: missing in export")
    if problems:
        print("\n".join(problems[:40]))
        if len(problems) > 40:
            print(f"... and {len(problems) - 40} more")
        return 1
    n_levels = sum(len(v) for v in built.get("contents", {}).values())
    print(f"OK: {len(built.get('groups', {}))} groups, {n_levels} ladder levels match client export")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())