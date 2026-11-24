"""Middle Abyss (map 21) and deferred-quest NPC data from research/client-export.

When research/local MapData for map 21 is absent, spawn positions stay CANDIDATE (quest_supplement grid).
NpcData for 26001xx and event NPCs comes from the pinned client table export (not donor clones).

    python tools/abyss_middle_data.py templates --ids 2600101,2600102
    python tools/abyss_middle_data.py patch-world-npcs   # refresh supplement templates in world-npcs.json
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from data_json import write_data_json

ROOT = Path(__file__).resolve().parents[1]
WORLD_NPCS = ROOT / 'tools/data/world-npcs.json'

# Sync with tests/Aion.World.Tests/QuestDeferredParityTests.cs DeferredCodes.
DEFERRED_QUEST_CODES: tuple[str, ...] = (
    'DQ1202054', 'DQ1202061', 'DQ1202072', 'DQ1202073', 'DQ1202074',
    'DQ2202072', 'DQ2202073', 'DQ2202074', 'DQ2203140', 'DQ2203210',
    'HQ1102070', 'HQ1103140', 'HQ2102070', 'HQ2103460',
    'UQ1309037', 'UQ1309038', 'UQ1309039', 'UQ1309040', 'UQ1309041', 'UQ1309042', 'UQ1309043',
    'UQ1309044', 'UQ1309045', 'UQ1309046', 'UQ1309047', 'UQ1309048', 'UQ1309049',
    'UQ1309077', 'UQ1309078', 'UQ1309079', 'UQ1309080', 'UQ1309081', 'UQ1309082', 'UQ1309083',
    'UQ1309084', 'UQ1309085', 'UQ1309086', 'UQ1309087', 'UQ1309088', 'UQ1309089',
    'UQ2309037', 'UQ2309038', 'UQ2309039', 'UQ2309040', 'UQ2309041', 'UQ2309042', 'UQ2309043',
    'UQ2309044', 'UQ2309045', 'UQ2309046', 'UQ2309047', 'UQ2309048', 'UQ2309049',
    'UQ2309077', 'UQ2309078', 'UQ2309079', 'UQ2309080', 'UQ2309081', 'UQ2309082', 'UQ2309083',
    'UQ2309084', 'UQ2309085', 'UQ2309086', 'UQ2309087', 'UQ2309088', 'UQ2309089',
)

MIDDLE_ABYSS_NPC_LOW = 2600100
MIDDLE_ABYSS_NPC_HIGH = 2600199

MAP21_MAPDATA_HINT = (
    'MapData.dat for Reshanta Middle (MapId 21): '
    'WorldProbe with MapData under AION2/Content/Data/Map/… then '
    'python tools/world_npcs.py import-map --map 21 <research/local/.../table-NNN.json>'
)


def middle_abyss_npc_ids(quests: dict) -> list[int]:
    """Npc ids referenced by DutyScroll quests on map 21."""
    ids: set[int] = set()
    for code, q in quests.get('quests', {}).items():
        if q.get('map') != 21 and code not in DEFERRED_QUEST_CODES:
            continue
        if q.get('map') != 21 and not str(code).startswith('UQ'):
            continue
        for step in q.get('steps') or []:
            for o in step.get('objectives') or []:
                for nid in o.get('npcs') or []:
                    if isinstance(nid, int) and MIDDLE_ABYSS_NPC_LOW <= nid <= MIDDLE_ABYSS_NPC_HIGH:
                        ids.add(nid)
    return sorted(ids)


def npc_ids_for_deferred_quests(quests: dict) -> set[int]:
    needed: set[int] = set()
    bucket = quests.get('quests', {})
    for code in DEFERRED_QUEST_CODES:
        q = bucket.get(code)
        if not q:
            continue
        for step in q.get('steps') or []:
            for o in step.get('objectives') or []:
                if o.get('type') in ('KillNpc', 'CollectFakeItem', 'AskNpc', 'CloseNpc'):
                    for nid in o.get('npcs') or []:
                        if isinstance(nid, int):
                            needed.add(nid)
                    if isinstance(o.get('npc'), int):
                        needed.add(o['npc'])
    return needed


def client_export_templates(npc_ids: set[int] | list[int]) -> tuple[dict[str, dict], dict[str, dict], set[int]]:
    """Build world-npcs template dicts from client NpcData (+ skill rows). Missing ids are omitted."""
    from world_npcs import aggro_shape, extra_template, npc_skills

    wanted = set(npc_ids)
    from client_export import load_rows

    rows = {row['ID']['Value']: row for row in load_rows('NpcData') if row['ID']['Value'] in wanted}
    shapes = {row['AggroDetectType']: aggro_shape(row) for row in load_rows('NpcAggroData')}
    templates: dict[str, dict] = {}
    skills: set[int] = set()
    for nid, row in sorted(rows.items()):
        templates[str(nid)] = extra_template(row, shapes)
        skills.update(s['Value'] for s in row.get('ActiveSkillList', []) if s.get('Value'))
    skill_rows = npc_skills(skills, 'client:Skill', 'client:SkillEffect') if skills else {}
    return templates, skill_rows, wanted - set(rows)


def patch_world_npc_templates(npc_ids: set[int]) -> list[int]:
    """Overwrite tools/data/world-npcs.json templates (and npc_skills) from client export."""
    world = json.loads(WORLD_NPCS.read_text(encoding='utf-8'))
    templates, skill_rows, missing = client_export_templates(npc_ids)
    world['templates'].update(templates)
    world.setdefault('npc_skills', {}).update(skill_rows)
    src = world.setdefault('source', {})
    src['middle_abyss_npcdata'] = 'research/client-export/2026-11-22 NpcData (Middle Abyss 26001xx + deferred talk/kill npcs)'
    if missing:
        src['middle_abyss_npcdata_missing'] = sorted(missing)
    write_data_json(WORLD_NPCS, world)
    return sorted(npc_ids & {int(k) for k in templates})


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='command', required=True)
    t = sub.add_parser('templates', help='print template json for npc ids')
    t.add_argument('--ids', required=True, help='comma-separated NpcData ids')
    sub.add_parser('patch-world-npcs', help='refresh deferred / Middle Abyss templates in world-npcs.json')
    args = parser.parse_args()
    if args.command == 'templates':
        ids = {int(x.strip()) for x in args.ids.split(',') if x.strip()}
        templates, skills, missing = client_export_templates(ids)
        print(json.dumps({'templates': templates, 'npc_skills': skills, 'missing': sorted(missing)}, indent=2))
    elif args.command == 'patch-world-npcs':
        quests = json.loads((ROOT / 'tools/data/quests.json').read_text(encoding='utf-8'))
        ids = npc_ids_for_deferred_quests(quests) | set(middle_abyss_npc_ids(quests))
        patched = patch_world_npc_templates(ids)
        print(f'patched {len(patched)} templates in {WORLD_NPCS.relative_to(ROOT)}; map21 spawn positions: {MAP21_MAPDATA_HINT}')


if __name__ == '__main__':
    main()