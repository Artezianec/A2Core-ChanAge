#!/usr/bin/env python3
"""List achievement objective types in achievements.json with no server emitter call site.

Emitters are Achieve(\"Type\"), AchieveSet(\"Type\"), or Emit(new AchievementEvent(\"Type\" in server/Aion.World.
"""
from __future__ import annotations

import json
import re
import sys
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / "tools/data/achievements.json"
SERVER = ROOT / "server/Aion.World"
PAT = re.compile(
    r'(?:Achieve(?:Set)?\(\s*"([^"]+)"|Emit\(new\s+AchievementEvent\(\s*"([^"]+)"|'
    r'new\s+AchievementEvent\(\s*"([^"]+)")'
)


def emitters() -> set[str]:
    found: set[str] = set()
    for path in SERVER.rglob("*.cs"):
        text = path.read_text(encoding="utf-8")
        for m in PAT.finditer(text):
            for g in m.groups():
                if g:
                    found.add(g)
    return found


def main() -> int:
    data = json.loads(DATA.read_text(encoding="utf-8"))
    by_type = Counter(e["t"] for e in data["achievements"].values())
    reg = emitters()
    missing = sorted(t for t in by_type if t not in reg)
    print(f"{len(by_type)} objective types, {len(reg)} emitter types, {len(missing)} uncovered types\n")
    for t in missing:
        print(f"  {by_type[t]:5d}  {t}")
    covered = sum(by_type[t] for t in by_type if t in reg)
    total = sum(by_type.values())
    print(f"\n{covered}/{total} achievement entries have an emitter type ({100 * covered / total:.1f}%)")
    return 0


if __name__ == "__main__":
    sys.exit(main())