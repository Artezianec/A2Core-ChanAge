"""Per-spawn mob respawn delays from client map-cell AionSpawnUnit actors (HOME_TODO O3).

Sources (first match wins per spawn placement):
- WorldProbe packages summarized with tools/summarize_world.py (research/local/spawn-actors-*/ etc.):
  RespawnTime / MinRespawnDelay on AionSpawnUnit* observed_properties, keyed by actor Name.
- MapData.dat SpawnInfoList rows when respawn fields are present on a record (optional; same probe tree).

Joins to server placements via tools/data/world-npcs.json compact rows: map id, spawn name, UniqueKey.

Units (CANDIDATE until live capture): RespawnTime and MinRespawnDelay values in (0, 600] are treated as
seconds and converted to ms; larger integers are treated as milliseconds. DespawnDelayMS is always ms.

    python tools/spawn_respawn.py build     # writes tools/data/spawn-respawn.json

When no spawn-actor probe is in the repo, build writes an empty map table and documents the missing export.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from data_json import write_data_json
from summarize_world import summarize

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / "tools/data/spawn-respawn.json"
WORLD_NPCS = ROOT / "tools/data/world-npcs.json"
LOCAL = ROOT / "research/local"
ALL_MAPDATA = LOCAL / "all-mapdata-full"
SPAWN_ACTOR_DIRS = (
    LOCAL / "spawn-actors-20261029",
)

RESPAWN_PROP_KEYS = ("RespawnTime", "MinRespawnDelay", "DespawnDelayMS")


def _to_ms(value) -> int | None:
    if value is None:
        return None
    try:
        v = float(value)
    except (TypeError, ValueError):
        return None
    if v <= 0:
        return None
    # CANDIDATE: designer-facing seconds for small values; ms for large dat integers.
    if v <= 600:
        return int(round(v * 1000))
    return int(round(v))


def respawn_ms_from_props(props: dict) -> int | None:
    for key in RESPAWN_PROP_KEYS:
        if key in props:
            ms = _to_ms(props[key])
            if ms:
                return ms
    return None


def load_world_index(path=WORLD_NPCS):
    """(map_id, name) -> [unique_key, ...] from compact world-npcs.json."""
    data = json.loads(path.read_text(encoding="utf-8"))
    index: dict[tuple[int, str], list[int]] = {}
    for map_id, rows in data.get("maps", {}).items():
        mid = int(map_id)
        for row in rows:
            name = row[0]
            unique = int(row[6]) & 0xFFFFFFFF if len(row) > 6 else 0
            index.setdefault((mid, name), []).append(unique)
    return index


def from_spawn_actor_dirs(dirs=SPAWN_ACTOR_DIRS):
    """{spawn_name: respawn_ms} from spawn-actor probe summaries."""
    by_name: dict[str, int] = {}
    for base in dirs:
        if not base.is_dir():
            continue
        for summary in base.glob("**/summary.json"):
            for spawn in summarize(summary.parent).get("spawns", []):
                props = spawn.get("observed_properties") or {}
                ms = respawn_ms_from_props(props)
                if not ms:
                    continue
                name = spawn.get("name") or props.get("Name") or spawn.get("Name")
                if isinstance(name, str) and name:
                    by_name.setdefault(name, ms)
    return by_name


def from_mapdata(path=ALL_MAPDATA):
    """{(map_id, spawn_name): respawn_ms} from SpawnInfoList when fields exist."""
    out: dict[tuple[int, str], int] = {}
    if not path.is_dir():
        return out
    for summary in sorted(path.glob("*/summary.json")):
        for package in json.loads(summary.read_text(encoding="utf-8")).get("packages", []):
            if not str(package.get("path", "")).endswith("MapData.dat"):
                continue
            table = summary.parent / package["file"]
            try:
                mapdata = json.loads(table.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                continue
            data = mapdata.get("Properties", {}).get("Data") or mapdata
            map_id = data.get("MapId")
            if map_id is None:
                continue
            for record in data.get("SpawnInfoList") or []:
                ms = respawn_ms_from_props(record)
                if not ms:
                    continue
                name = record.get("Name")
                if name:
                    out[(int(map_id), str(name))] = ms
    return out


def build():
    limitations = []
    actor_by_name = from_spawn_actor_dirs()
    if not any(d.is_dir() for d in SPAWN_ACTOR_DIRS):
        limitations.append(
            "research/local/spawn-actors-20261029/ (AionSpawnUnit WorldProbe) not in repo; "
            "run summarize_world on probe output then rebuild."
        )
    mapdata = from_mapdata()
    if not ALL_MAPDATA.is_dir():
        limitations.append("research/local/all-mapdata-full/ absent; MapData SpawnInfoList respawn fields skipped.")

    index = load_world_index()
    by_name: dict[str, dict[str, int]] = {}
    by_unique: dict[str, dict[str, int]] = {}

    def assign(map_id: int, name: str, ms: int):
        by_name.setdefault(str(map_id), {})[name] = ms
        for uk in index.get((map_id, name), []):
            by_unique.setdefault(str(map_id), {})[str(uk)] = ms

    for (map_id, name), ms in mapdata.items():
        assign(map_id, name, ms)

    if actor_by_name:
        for (map_id, name), _keys in index.items():
            if name in actor_by_name:
                assign(map_id, name, actor_by_name[name])

    return {
        "source": (
            "tools/spawn_respawn.py: AionSpawnUnit actors (summarize_world) and MapData SpawnInfoList "
            "when present; matched to tools/data/world-npcs.json by map + name / UniqueKey."
        ),
        "limitations": limitations,
        "maps": by_name,
        "by_unique_key": by_unique,
    }


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("command", choices=["build"])
    args = p.parse_args()
    if args.command == "build":
        payload = build()
        write_data_json(DATA, payload)
        named = sum(len(v) for v in payload["maps"].values())
        print(json.dumps({"maps": len(payload["maps"]), "named_spawns": named, "limitations": len(payload["limitations"])}))


if __name__ == "__main__":
    main()