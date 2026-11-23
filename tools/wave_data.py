"""Client WaveData / WaveStepData -> server wave parts for tracked ClearWave.

Builds tools/data/client-waves.json: per served map, each StartWave / ClearWave name from
map-events.json mapped to ordered spawner parts from WaveStepData (client export). Ludra-style
waves whose map spawns follow ``{wave}_{n}`` are left to runtime discovery (GameSession.Bosses.cs).
Hand-made waves in boss-phases.json are excluded.

Also refreshes tools/data/daily-waves.json for maps 900001-900003 from the same WaveStepData.

    python tools/wave_data.py build
    python tools/wave_data.py --check
    python tools/wave_data.py coverage
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))

from client_export import load_rows
from data_json import write_data_json

MAP_EVENTS = ROOT / 'tools/data/map-events.json'
WORLD_NPCS = ROOT / 'tools/data/world-npcs.json'
WORLD_OBJECTS = ROOT / 'tools/data/world-objects.json'
BOSS_PHASES = ROOT / 'tools/data/boss-phases.json'
OUT = ROOT / 'tools/data/client-waves.json'
DAILY_OUT = ROOT / 'tools/data/daily-waves.json'
DAILY_MAPS = (900001, 900002, 900003)


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding='utf-8'))


def wave_steps() -> dict[str, list[str]]:
    """WaveName -> flat ordered spawner list (WaveStepData orders, each order's spawners in sequence)."""
    by_wave: dict[str, list[tuple[int, list[str]]]] = defaultdict(list)
    for row in load_rows('WaveStepData'):
        by_wave[row['WaveName']].append((row['Order'], list(row['SpawnerList'])))
    flat: dict[str, list[str]] = {}
    for name, orders in by_wave.items():
        parts: list[str] = []
        for _, spawners in sorted(orders):
            parts.extend(spawners)
        flat[name] = parts
    return flat


def wave_meta() -> dict[str, dict]:
    meta = {}
    for row in load_rows('WaveData'):
        meta[row['Name']] = {
            'id': row['ID']['Value'],
            'group': row.get('Group'),
            'phase_index': row.get('PhaseIndex', 0),
        }
    return meta


def spawn_names_on_map(world: dict, map_id: int, objects: dict | None = None) -> set[str]:
    """Spawner names of a map: its NPC spawns and (objects) its environment-object spawners (a wave part may be a supply box)."""
    rows = world.get('maps', {}).get(str(map_id), [])
    names: set[str] = set()
    if rows:
        names = {r['name'] for r in rows} if isinstance(rows[0], dict) else {r[0] for r in rows}
    if objects:
        names |= {o['name'] for o in objects.get('maps', {}).get(str(map_id), {}).get('objects', []) if o.get('name')}
    return names


def scan_map_event_waves(events: dict, served: set[int]) -> dict[int, set[str]]:
    """map id -> wave names referenced by StartWave directives or ClearWave objectives."""
    out: dict[int, set[str]] = defaultdict(set)

    def scan_dirs(dirs, mid: int) -> None:
        for d in dirs or []:
            if not isinstance(d, dict):
                continue
            t = d.get('t')
            if t == 'StartWave' and d.get('v'):
                out[mid].add(d['v'][0])

    for ev in events.values():
        mid = ev.get('map')
        if mid not in served:
            continue
        for rule in ev.get('rules', []) or []:
            scan_dirs(rule.get('directives'), mid)
        for key in ('on_complete', 'on_failure', 'complete', 'fail'):
            scan_dirs(ev.get(key), mid)
        for stage in ev.get('stages', []):
            scan_dirs(stage.get('fail_directives'), mid)
            for step in stage.get('steps', []):
                for o in step.get('obj') or []:
                    if o.get('t') == 'ClearWave' and o.get('v'):
                        out[mid].add(o['v'][0])
                scan_dirs(step.get('pre'), mid)
                scan_dirs(step.get('post'), mid)
                for o in step.get('obj') or []:
                    scan_dirs(o.get('pre'), mid)
                    scan_dirs(o.get('post'), mid)
    return out


def ludra_pattern_parts(wave: str, names: set[str]) -> list[str]:
    part = re.compile('^' + re.escape(wave) + r'_(\d+)$', re.I)
    found = [n for n in names if part.match(n)]
    return sorted(found, key=lambda n: int(part.match(n).group(1)))


def synthetic_waves(bp: dict) -> set[tuple[int, str]]:
    out: set[tuple[int, str]] = set()
    for wave, entry in (bp.get('waves') or {}).items():
        out.add((entry['map'], wave.split('@')[0]))
    return out


def build() -> dict:
    steps = wave_steps()
    meta = wave_meta()
    me = load_json(MAP_EVENTS)
    world = load_json(WORLD_NPCS)
    objects = load_json(WORLD_OBJECTS)
    bp = load_json(BOSS_PHASES)
    served = set(me.get('served_maps') or [])
    refs = scan_map_event_waves(me.get('events', {}), served)
    handmade = synthetic_waves(bp)

    maps_out: dict[str, dict[str, list[str]]] = {}
    timer_fallback: list[dict] = []
    coverage = {
        'served_maps_with_waves': 0,
        'wave_references': 0,
        'ludra_pattern': 0,
        'synthetic': 0,
        'client_wavestep': 0,
        'no_client_data': 0,
    }

    for mid in sorted(refs):
        names = spawn_names_on_map(world, mid, objects)
        map_waves: dict[str, list[str]] = {}
        for wave in sorted(refs[mid], key=str.lower):
            coverage['wave_references'] += 1
            if (mid, wave) in handmade:
                coverage['synthetic'] += 1
                continue
            if ludra_pattern_parts(wave, names):
                coverage['ludra_pattern'] += 1
                continue
            parts = steps.get(wave)
            if not parts:
                coverage['no_client_data'] += 1
                timer_fallback.append({'map': mid, 'wave': wave, 'reason': 'no_wavestep'})
                continue
            map_waves[wave] = parts
            coverage['client_wavestep'] += 1
            on_map = sum(1 for p in parts if p in names)
            if names and on_map < len(parts):
                timer_fallback.append({
                    'map': mid,
                    'wave': wave,
                    'reason': 'partial_spawns',
                    'on_map': on_map,
                    'expected': len(parts),
                })
        if map_waves:
            maps_out[str(mid)] = map_waves
            coverage['served_maps_with_waves'] += 1

    daily_maps: dict[str, dict[str, list[str]]] = {}
    for mid in DAILY_MAPS:
        for wave in refs.get(mid, ()):
            if wave in steps:
                daily_maps.setdefault(str(mid), {})[wave] = steps[wave]

    return {
        'source': (
            'WaveData.dat + WaveStepData.dat (research/client-export/2026-11-22); '
            'wave names from tools/data/map-events.json StartWave and ClearWave on served_maps. '
            'Ludra {wave}_{n} and boss-phases.json synthetic waves are omitted here.'
        ),
        'wave_meta': {k: meta[k] for k in sorted(meta) if k in steps},
        'maps': maps_out,
        'timer_fallback': timer_fallback,
        'coverage': coverage,
        '_daily_maps': daily_maps,
    }


def daily_waves_file(data: dict) -> dict:
    return {
        'source': (
            'WaveStepData.dat (client export 2026-11-22), built by tools/wave_data.py for daily dungeon '
            'maps 900001-900003 (ClearWave objectives in map-events.json).'
        ),
        'maps': data['_daily_maps'],
    }


def validate(data: dict) -> list[str]:
    problems: list[str] = []
    steps = wave_steps()
    for mid, waves in data.get('maps', {}).items():
        for wave, parts in waves.items():
            if wave not in steps:
                problems.append(f'map {mid} wave {wave}: not in WaveStepData')
            elif parts != steps[wave]:
                problems.append(f'map {mid} wave {wave}: parts stale vs WaveStepData')
            if not parts:
                problems.append(f'map {mid} wave {wave}: empty parts')
    return problems


def text(data: dict) -> str:
    payload = {k: v for k, v in data.items() if not k.startswith('_')}
    return json.dumps(payload, indent=2, ensure_ascii=False) + '\n'


def print_coverage(data: dict) -> None:
    c = data['coverage']
    print(f"Wave references on served maps: {c['wave_references']}")
    print(f"  Ludra {{wave}}_{{n}} (runtime): {c['ludra_pattern']}")
    print(f"  boss-phases.json synthetic: {c['synthetic']}")
    print(f"  client WaveStepData (client-waves.json): {c['client_wavestep']}")
    print(f"  still timer-only (no WaveStepData row): {c['no_client_data']}")
    print(f"Maps in client-waves.json: {c['served_maps_with_waves']}")
    partial = [t for t in data['timer_fallback'] if t['reason'] == 'partial_spawns']
    if partial:
        print(f"Partial spawn coverage (tracked with on-map spawners only): {len(partial)}")
        for row in partial[:20]:
            print(f"  {row['map']} {row['wave']}: {row['on_map']}/{row['expected']}")
    none = [t for t in data['timer_fallback'] if t['reason'] == 'no_wavestep']
    if none:
        print('Timer fallback (no client row):')
        for row in none:
            print(f"  {row['map']} {row['wave']}")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description='Build client wave parts from WaveStepData export.')
    parser.add_argument('command', nargs='?', default='build', choices=('build', 'coverage'))
    parser.add_argument('--check', action='store_true', help='Exit 1 if committed JSON is stale')
    args = parser.parse_args(argv)

    data = build()
    problems = validate(data)
    if problems:
        print('\n'.join(problems))
        return 1

    if args.command == 'coverage' or '--check' not in sys.argv and args.command == 'coverage':
        print_coverage(data)
        return 0

    if args.check:
        stale = []
        if not OUT.is_file() or OUT.read_text(encoding='utf-8') != text(data):
            stale.append(OUT.relative_to(ROOT))
        daily = daily_waves_file(data)
        daily_text = json.dumps(daily, indent=2, ensure_ascii=False) + '\n'
        if not DAILY_OUT.is_file() or DAILY_OUT.read_text(encoding='utf-8') != daily_text:
            stale.append(DAILY_OUT.relative_to(ROOT))
        if stale:
            print('Stale: ' + ', '.join(str(p) for p in stale) + ' — run python tools/wave_data.py build')
            return 1
        return 0

    if args.command == 'coverage':
        print_coverage(data)
        return 0

    write_data_json(OUT, {k: v for k, v in data.items() if not k.startswith('_')})
    write_data_json(DAILY_OUT, daily_waves_file(data))
    print_coverage(data)
    print(f"client-waves -> {OUT.relative_to(ROOT)} ({sum(len(w) for w in data['maps'].values())} waves)")
    print(f"daily-waves -> {DAILY_OUT.relative_to(ROOT)}")
    return 0


if __name__ == '__main__':
    sys.exit(main())