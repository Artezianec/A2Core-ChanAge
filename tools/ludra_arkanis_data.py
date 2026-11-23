"""Data the Abyssal Forge: Ludra raid and the Shattered Arkanis party dungeon need on top of the generated catalogs.

Idempotent and deterministic: it rewrites only the entries it owns (documented in docs/LUDRA-ARKANIS.md), so the generated files
(world-npcs.json, map-events.json, world-objects.json) keep their order and every other row byte for byte. Rerun it after a
regeneration of those catalogs; then rerun tools/skill_effect_engine.py (ratio_damage and npc_abnormals follow the npc_skills keys).

1. Boss-kit skills. world-npcs.json carries only the NpcData.ActiveSkillList skills, but the Ludra bosses cast their phases through
   NpcBehaviorCase / summon skills outside that list (Skill.dat ids 1802690-1802999 Rahu, 1801800-1801999 Ketu, 1803000-1803399
   Ludra, 1801580-1801799 Thalythra, 1605990-1606109 / 1606370-1606459 the Arkanis Naga). They are added to npc_skills and
   skill_hits (client Skill.dat / SkillEffect.dat), so the mechanics engine and the skill effect engine find their damage rows,
   HP-ratio rows and abnormals.
2. Ludra Hard (Dungeon ReforgedAbyss_Hard, map 600083, item level 3000). The client has the Hard NpcData rows (2300280-2300298),
   the MapEvent ReforgedAbyss_Hard_MapEvent, map points and a content ticket row, but the repo's catalogs have no Hard map data
   (no MapData.dat decode: no spawns, no env objects, no Rahu/Ketu/BossRoom/wave events, no Dungeon row). The Hard map is DERIVED
   from the Normal one (the client's own Hard map points are the Normal ones, same coordinates): same spawns with the Hard NPC ids,
   same env objects, the Normal sub-events with the Hard npcs and 3 revives. Everything derived is OURS / CANDIDATE.

    python tools/ludra_arkanis_data.py            # writes the catalogs
    python tools/ludra_arkanis_data.py --check    # exit 1 when they are stale
"""
from __future__ import annotations

import argparse
import copy
import json
import re
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))

from client_export import load_rows  # noqa: E402
from data_json import write_data_json  # noqa: E402
import world_npcs  # noqa: E402

DATA = ROOT / 'tools/data'
WORLD_NPCS = DATA / 'world-npcs.json'
MAP_EVENTS = DATA / 'map-events.json'
WORLD_OBJECTS = DATA / 'world-objects.json'

NORMAL_MAP, HARD_MAP = '600082', '600083'
HARD_DUNGEON = 'ReforgedAbyss_Hard'
HARD_ITEM_LEVEL = 3000
# Hard raids give each player 3 revives (the Oct 28 / Nov 11 notices state it for the Hard expeditions; the Ludra Hard row is INFERRED).
HARD_REVIVES = '3'
SUB_EVENTS = ('Rahu', 'Ketu', 'BossRoom', '1PartyWave', '2PartyWave')

# Skill.dat ranges (inclusive start, exclusive end) of the bosses' kits, see the module docstring.
KIT_RANGES = [(1802690, 1803000), (1801800, 1802000), (1803000, 1803400), (1801580, 1801800), (1605990, 1606110), (1606370, 1606460)]


def kit_skills(skill_rows: list[dict], effect_groups: set[int]) -> list[int]:
    return sorted(row['ID']['Value'] for row in skill_rows
                  if any(lo <= row['ID']['Value'] < hi for lo, hi in KIT_RANGES)
                  and any(t['SkillEffectGroupId']['Value'] in effect_groups for t in row.get('SkillEffectTimeDataList', [])))


def kit_hits(skills: list[dict], effects: list[dict], ids: set[int]) -> dict[str, list[dict]]:
    """world_npcs.skill_hits_catalog on the client rows (that function reads ProjectileId through a helper it does not define)."""
    damage: dict[int, dict] = {}
    groggy: dict[int, int] = {}
    for effect in effects:
        group, kind = effect['SkillEffectGroupId']['Value'], world_npcs._enum(effect['EffectType'])
        if kind == 'Damage':
            damage.setdefault(group, effect)
        elif kind == 'GroggyGuardDecreaseEnergy':
            values = effect.get('EffectValueList') or []
            groggy.setdefault(group, int(values[0]) if values else 0)
    out = {}
    for row in sorted((r for r in skills if r['ID']['Value'] in ids), key=lambda r: r['ID']['Value']):
        hits = []
        for timed in row.get('SkillEffectTimeDataList', []):
            group = timed['SkillEffectGroupId']['Value']
            if group in damage:
                hits.append({'effect': damage[group]['ID']['Value'], 'projectile_id': int(timed.get('ProjectileId', {}).get('Value', 0) or 0),
                             'groggy': groggy.get(group, 0)})
        if hits:
            out[str(row['ID']['Value'])] = hits
    return out


def add_kit_skills(world: dict) -> None:
    skills = load_rows('Skill')
    effects = load_rows('SkillEffect')
    groups = {e['SkillEffectGroupId']['Value'] for e in effects}
    ids = kit_skills(skills, groups)
    with tempfile.TemporaryDirectory() as tmp:
        skill_file, effect_file = Path(tmp) / 'skill.json', Path(tmp) / 'effect.json'
        skill_file.write_text(json.dumps({'Properties': {'Data': [r for r in skills if r['ID']['Value'] in set(ids)]}}), encoding='utf-8')
        wanted = {t['SkillEffectGroupId']['Value'] for r in skills if r['ID']['Value'] in set(ids) for t in r.get('SkillEffectTimeDataList', [])}
        effect_file.write_text(json.dumps({'Properties': {'Data': [e for e in effects if e['SkillEffectGroupId']['Value'] in wanted]}}), encoding='utf-8')
        new_skills = world_npcs.npc_skills(ids, skill_file, effect_file)
        new_hits = kit_hits(skills, effects, set(ids))
    for key, row in new_skills.items():
        world['npc_skills'].setdefault(key, row)
    for key, hits in new_hits.items():
        world['skill_hits'].setdefault(key, hits)


def hard_ids() -> dict[int, int]:
    """Normal NpcData id -> Hard NpcData id of the same monster (M_PD_ReforgedAbyss_X -> M_PD_ReforgedAbyss_Hard_X), plus the Hard rows."""
    rows = {r['ID']['Value']: r for r in load_rows('NpcData')}
    by_name = {r['Name']: i for i, r in rows.items()}
    mapping = {}
    for i, row in rows.items():
        name = row['Name']
        if name.startswith('M_PD_ReforgedAbyss_Hard_') and (normal := by_name.get(name.replace('_Hard_', '_', 1))) is not None:
            mapping[normal] = i
    return mapping


def hard_templates(world: dict, mapping: dict[int, int]) -> None:
    rows = {r['ID']['Value']: r for r in load_rows('NpcData')}
    for normal, hard in mapping.items():
        if str(normal) not in world['templates']:
            continue
        template = copy.deepcopy(world['templates'][str(normal)])
        row = rows[hard]
        template['level'] = row['Level']
        template['skills'] = [s['Value'] for s in row.get('ActiveSkillList', []) if s['Value']]
        template['sub_type'] = world_npcs._enum(row.get('NpcSubType', 'ENpcSubType::NormalMonster'))
        template['grade'] = int(row.get('Grade') or template.get('grade', 3))
        world['templates'][str(hard)] = template  # existing keys keep their place, new ones are appended


def hard_spawns(world: dict, mapping: dict[int, int]) -> None:
    spawns = []
    for spawn in world['maps'][NORMAL_MAP]:
        row = list(spawn)
        row[1] = mapping.get(row[1], row[1])
        row[0] = row[0].replace('ReforgedAbyss_01_Wave_', 'ReforgedAbyss_Hard_01_Wave_')
        spawns.append(row)
    world['maps'][HARD_MAP] = spawns
    world['routes'][HARD_MAP] = copy.deepcopy(world['routes'].get(NORMAL_MAP, []))


def map_ints(node, mapping: dict[int, int]):
    """Deep copy of a map-event node with every NPC id of a "npcs" list replaced by the Hard id."""
    if isinstance(node, dict):
        return {k: ([mapping.get(i, i) for i in v] if k == 'npcs' and isinstance(v, list) else map_ints(v, mapping)) for k, v in node.items()}
    if isinstance(node, list):
        return [map_ints(x, mapping) for x in node]
    return node


def set_revives(node, revives: str):
    if isinstance(node, dict):
        if node.get('t') == 'ResetRebirthLimit':
            return dict(node, v=[revives])
        return {k: set_revives(v, revives) for k, v in node.items()}
    if isinstance(node, list):
        return [set_revives(x, revives) for x in node]
    return node


def resolve_npcs(node, names: dict[str, int]):
    """Hard_MapEvent names its objectives by NpcData name and has no "npcs" (the Normal events carry them): add them."""
    if isinstance(node, dict):
        out = {k: resolve_npcs(v, names) for k, v in node.items()}
        if node.get('t') in ('KillNpc', 'HpRemaining') and 'npcs' not in node and node.get('v'):
            ids = [names[n.strip()] for n in str(node['v'][0]).split(',') if n.strip() in names]
            if ids:
                out['npcs'] = ids
        return out
    if isinstance(node, list):
        return [resolve_npcs(x, names) for x in node]
    return node


def hard_events(events: dict, maps: dict, mapping: dict[int, int]) -> None:
    names = {name: i for name, i in ((r['Name'], r['ID']['Value']) for r in load_rows('NpcData') if r['Name'].startswith('M_PD_ReforgedAbyss_Hard_'))}
    events['ReforgedAbyss_Hard_MapEvent'] = resolve_npcs(events['ReforgedAbyss_Hard_MapEvent'], names)
    for sub in SUB_EVENTS:
        normal = events[f'ReforgedAbyss_Normal_{sub}_MapEvent']
        events[f'ReforgedAbyss_Hard_{sub}_MapEvent'] = set_revives(dict(map_ints(normal, mapping), map=int(HARD_MAP)), HARD_REVIVES)
    listed = maps[HARD_MAP]
    for sub in SUB_EVENTS:
        if f'ReforgedAbyss_Hard_{sub}_MapEvent' not in listed:
            listed.append(f'ReforgedAbyss_Hard_{sub}_MapEvent')


def hard_dungeon(me: dict, mapping: dict[int, int]) -> None:
    normal = copy.deepcopy(me['dungeons'][NORMAL_MAP])
    hard_boss = mapping[normal['cube_boss']]
    hard = dict(normal, name=HARD_DUNGEON, map=int(HARD_MAP), item_level=HARD_ITEM_LEVEL, cube_boss=hard_boss)
    hard['cube'] = {str(hard_boss): normal['cube'][str(normal['cube_boss'])]}
    me['dungeons'][HARD_MAP] = hard
    if int(HARD_MAP) not in me['served_maps']:
        me['served_maps'] = sorted(me['served_maps'] + [int(HARD_MAP)])


HARD_OBJECT_NAMES = {
    'E_PD_ReforgedAbyss_1PartyPotal_01': 'E_PD_ReforgedAbyss_Hard_1PartyPotal_01',
    'E_PD_ReforgedAbyss_2PartyPotal_01': 'E_PD_ReforgedAbyss_Hard_2PartyPotal_01',
    'E_PD_ReforgedAbyss_Nor_TreasureBoxLower01_001': 'E_PD_ReforgedAbyss_Hard_TreasureBoxLower01_001',
}


def hard_objects(objects: dict) -> None:
    source = objects['maps'][NORMAL_MAP]
    copied = copy.deepcopy(source)
    for obj in copied['objects']:
        if (hard := HARD_OBJECT_NAMES.get(obj['name'])) is not None:
            # The Hard event spawns the portal by its Hard name and uses it by the Normal one: the spawner name is the object's name,
            # the env name the UseEnvObj objective's.
            obj['env_name'] = obj['name']
            obj['name'] = hard
    objects['maps'][HARD_MAP] = copied


def build() -> dict[Path, object]:
    world = json.loads(WORLD_NPCS.read_text(encoding='utf-8'))
    me = json.loads(MAP_EVENTS.read_text(encoding='utf-8'))
    objects = json.loads(WORLD_OBJECTS.read_text(encoding='utf-8'))
    mapping = hard_ids()
    add_kit_skills(world)
    hard_templates(world, mapping)
    hard_spawns(world, mapping)
    hard_events(me['events'], me['maps'], mapping)
    hard_dungeon(me, mapping)
    hard_objects(objects)
    return {WORLD_NPCS: world, MAP_EVENTS: me, WORLD_OBJECTS: objects}


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    parser.add_argument('--check', action='store_true', help='exit 1 when the committed catalogs differ from a fresh build')
    args = parser.parse_args(argv)
    built = build()
    stale = []
    for path, data in built.items():
        text = json.dumps(data, indent=2, ensure_ascii=False) + '\n'
        if path.read_text(encoding='utf-8') != text:
            stale.append(path.name)
            if not args.check:
                write_data_json(path, data)
    if args.check and stale:
        print('stale: ' + ', '.join(stale) + ' (run python tools/ludra_arkanis_data.py)')
        return 1
    print('up to date' if not stale else 'wrote ' + ', '.join(stale))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
