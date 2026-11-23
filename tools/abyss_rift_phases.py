"""AbyssRiftPhase + AbyssRiftPhaseSpawnerGroup from client export -> abyss-rift-phases.json.

    python tools/abyss_rift_phases.py build
    python tools/abyss_rift_phases.py --check
"""
from __future__ import annotations

import argparse
import json
import sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))

from client_export import load_rows
from data_json import write_data_json

OUT = ROOT / 'tools/data/abyss-rift-phases.json'


def parse_end(row: dict) -> list[dict]:
    out = []
    for end in row.get('PhaseEndDatas') or []:
        raw_type = end.get('EndType', '')
        end_type = raw_type.split('::')[-1] if '::' in raw_type else raw_type
        val = end.get('EndValue', '')
        msg = end.get('TargetMessage') or {}
        key = msg.get('Key') if isinstance(msg, dict) else None
        out.append({'type': end_type, 'value': val, 'message_key': key})
    return out


def build() -> dict:
    groups: dict[str, list[str]] = defaultdict(list)
    for row in load_rows('AbyssRiftPhaseSpawnerGroup'):
        g = row.get('Group')
        if g and g != 'None':
            groups[g].append(row['SpawnerName'])

    rifts: dict[str, list[dict]] = defaultdict(list)
    for row in load_rows('AbyssRiftPhase'):
        rid = row['AbyssRiftId']['Value']
        sg = row.get('SpawnerGroup')
        rifts[str(rid)].append({
            'phase': row['Phase'],
            'spawner_group': sg if sg not in (None, 'None') else None,
            'spawners': groups.get(sg, []) if sg not in (None, 'None') else [],
            'ends': parse_end(row),
            'abnormal_ids': row.get('PhaseAbnormalIds') or [],
        })
    for phases in rifts.values():
        phases.sort(key=lambda p: p['phase'])

    return {
        'source': 'AbyssRiftPhase.dat + AbyssRiftPhaseSpawnerGroup.dat (research/client-export/2026-11-22)',
        'rifts': dict(rifts),
    }


def text(data: dict) -> str:
    return json.dumps(data, indent=2, ensure_ascii=False) + '\n'


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('command', nargs='?', default='build', choices=('build',))
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args(argv)
    data = build()
    if args.check:
        if not OUT.is_file() or OUT.read_text(encoding='utf-8') != text(data):
            print(f'{OUT.relative_to(ROOT)} is stale: run python tools/abyss_rift_phases.py build')
            return 1
        return 0
    write_data_json(OUT, data)
    total = sum(len(v) for v in data['rifts'].values())
    print(f'{len(data["rifts"])} rift(s), {total} phase row(s) -> {OUT.relative_to(ROOT)}')
    return 0


if __name__ == '__main__':
    sys.exit(main())