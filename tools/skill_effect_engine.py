"""Generate tools/data/skill-effect-engine.json from the client skill tables (the skill effect engine's data).

The server's skill effect engine (docs/SKILL-EFFECT-ENGINE.md) resolves every skill / abnormal effect row by its client effect
type. This tool turns the client export into the rows the engine needs and the existing generated files do not carry:

- ``skill_effect_types`` / ``abnormal_effect_types``: every ESkillEffectType / EAbnormalEffectType in the tables with row counts
  (the registry's universe; server/Aion.World/SkillEffectRegistry.cs maps each to a handler).
- ``triggers``: conditional procs by abnormal id (TriggerSkillByAttackAll, ...ByGetDamage, ...ByKill, ...ByGetAbnormal,
  ...ByAddAbnormal, ...BySelfBlock / Evade / WeaponBlock / ShieldBlock): proc skill, chance, internal cooldown, max count and
  filters. A passive's abnormal is a hidden buff on the server, so its triggers fire like an active buff's.
- ``proc_skills``: the SkillEffect rows of every proc skill a trigger casts (heals, abnormals, damage, cooldown changes) with
  the per-level SkillEffectLv values of each row.
- ``drains``: life steal on Damage rows (EffectValueList[13..14] + [25..26], DrainByDamageAndCasterMaxHPOnce).
- ``ratio_damage``: Damage rows that deal a share of the victim's HP (EffectValueList[10] MaxHPRatio / CurrentHPRatio,
  [11] ratio in 1/10000, [12] cap, -1 none), for NPC skills whose flat damage range is 0.
- ``npc_abnormals``: Abnormal / Abnormal_True rows of NPC skills the ported table (ported-skill-abnormals.json) lacks.
- ``passives``: learned passive skill -> its abnormal ids (boxes.json passives, else the Abnormal_Passive rows).
- ``abnormal_effects``: SkillAbnormalEffect rows for abnormals the engine references that no other generated file carries.
- ``effect_levels``: SkillEffectLv / SkillAbnormalEffectLv values by level group, only for the groups referenced above.

Generated: never edit the JSON by hand; rerun this tool (also run by tools/client_skill_sync.py).

    python tools/skill_effect_engine.py            # writes tools/data/skill-effect-engine.json
    python tools/skill_effect_engine.py --check    # exit 1 when the committed file is stale
"""
from __future__ import annotations

import argparse
import json
import sys
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))

from client_export import DEFAULT_EXPORT, load_rows  # noqa: E402
from data_json import write_data_json  # noqa: E402

OUT = ROOT / 'tools/data/skill-effect-engine.json'
CLASS_SKILLS = ROOT / 'tools/data/class-skills.json'
BOXES = ROOT / 'tools/data/boxes.json'
SKILL_AREAS = ROOT / 'tools/data/skill-areas.json'
WORLD_NPCS = ROOT / 'tools/data/world-npcs.json'
PORTED = ROOT / 'tools/data/ported-skill-abnormals.json'
DUNGEON_BUFFS = ROOT / 'tools/data/dungeon-buff-abnormals.json'

# EAbnormalEffectType -> the engine's trigger kind. Values of every Trigger* row: [0] proc skill, [1] chance (1/10000),
# [2] internal cooldown ms, [3] max procs (-1 unlimited); the rest per type (see TRIGGER_FILTERS).
TRIGGER_KINDS = {
    'TriggerSkillByAttackAll': 'OnHit',
    'TriggerSkillByGetDamage': 'OnDamaged',
    'TriggerSkillByKill': 'OnKill',
    'TriggerSkillByGetAbnormal': 'OnGetAbnormal',
    'TriggerSkillByAddAbnormal': 'OnAddAbnormal',
    'TriggerSkillBySelfBlock': 'OnBlock',
    'TriggerSkillBySelfEvade': 'OnEvade',
    'TriggerSkillBySelfWeaponBlock': 'OnWeaponBlock',
    'TriggerSkillBySelfShieldBlock': 'OnShieldBlock',
}
HIT_RESULTS = ('Normal', 'Critical', 'Miss', 'SkillImmune', 'Block', 'Evade', 'Parry')
# Damage EffectValueList columns (28-column rows, 9238 of 15504 in the 2026-11-22 export).
DMG_CALC, DMG_RATIO, DMG_RATIO_CAP = 10, 11, 12
DRAIN_RATIO, DRAIN_CAP, DRAIN_RESULTS, DRAIN_KIND = 13, 14, 25, 26
HEAL_VALUE_TYPES = {
    'HpHeal', 'HpHealRatio', 'MpHeal', 'MpHealRatio', 'SpHeal', 'SpHealRatio', 'APHeal', 'APHealRatio', 'DpHeal', 'DpHealRatio',
    'FpHeal', 'FpHealRatio',
}


def _v(value):
    return value['Value'] if isinstance(value, dict) else value


def _short(value) -> str:
    return str(value).split('::')[-1]


def _int(value, default=0) -> int:
    try:
        return int(str(value).strip())
    except (TypeError, ValueError):
        return default


def _pair(value) -> tuple[int, int]:
    parts = [p.strip() for p in str(value).split(',')]
    lo = _int(parts[0]) if parts else 0
    hi = _int(parts[1], lo) if len(parts) > 1 else lo
    return lo, hi


def _results(value) -> list[str]:
    if value in (None, '', 'None', 'none'):
        return []
    return [p.strip() for p in str(value).split(',') if p.strip() and p.strip() != 'None']


def _none(value) -> str:
    text = str(value).strip() if value is not None else ''
    return '' if text.lower() in ('', 'none') else text


def _filter_side(filter_row) -> str:
    """'self' for a proc whose SkillEffectFilter lands on the caster (Self / friendly-only), else 'target'."""
    if not filter_row:
        return 'self'
    if _short(filter_row.get('EffectRangeType')) == 'Self':
        return 'self'
    enemy = bool(filter_row.get('bRelationshipEnemy'))
    friendly = bool(filter_row.get('bRelationshipFriendly'))
    if friendly and not enemy:
        return 'self'
    return 'target' if enemy else 'self'


def trigger_spec(effect_id: int, effect_type: str, values: list[str], level_group: str) -> dict | None:
    kind = TRIGGER_KINDS.get(effect_type)
    if kind is None or not values:
        return None
    proc = _int(values[0])
    if proc <= 0:
        return None
    spec = {
        'effect_id': effect_id,
        'type': effect_type,
        'on': kind,
        'proc_skill': proc,
        'chance': _int(values[1], 10000) if len(values) > 1 else 10000,
        'cooldown_ms': max(0, _int(values[2])) if len(values) > 2 else 0,
        'max_count': _int(values[3], -1) if len(values) > 3 else -1,
    }
    if effect_type in ('TriggerSkillByAttackAll', 'TriggerSkillByGetDamage'):
        # [5] element of the triggering hit (Fire, ...), [6] hit results, [8] extra check (TriggerSkillCheck_BossMonster, or an
        # abnormal effect type the victim must carry: Snare).
        element = _none(values[5]) if len(values) > 5 else ''
        results = _results(values[6]) if len(values) > 6 else []
        check = _none(values[8]) if len(values) > 8 else ''
        if element:
            spec['element'] = element
        if results:
            spec['results'] = results
        if check == 'TriggerSkillCheck_BossMonster':
            spec['boss_only'] = True
        elif check:
            spec['target_effect'] = check
    elif effect_type == 'TriggerSkillByKill':
        # [4] victim kind (PC / None), [5] extra (HOLD: the victim was held).
        victim = _none(values[4]) if len(values) > 4 else ''
        if victim:
            spec['victim'] = victim
    elif effect_type in ('TriggerSkillByGetAbnormal', 'TriggerSkillByAddAbnormal'):
        # [4] abnormal category (Buff / Debuff), [5] abnormal id or group the incoming one must match (0 / own id: any).
        category = _none(values[4]) if len(values) > 4 else ''
        if category:
            spec['category'] = category.capitalize() if category.lower() != 'debuff' else 'DeBuff'
        match = _int(values[5]) if len(values) > 5 else 0
        if match:
            spec['match'] = match
    if level_group:
        spec['level_group'] = level_group
    return spec


def build() -> dict:
    skills = {_v(r['ID']): r for r in load_rows('Skill')}
    effects = load_rows('SkillEffect')
    by_group = defaultdict(list)
    for e in effects:
        by_group[_v(e['SkillEffectGroupId'])].append(e)
    filters = {_v(r['ID']): r for r in load_rows('SkillEffectFilter')}
    abnormal_effects = load_rows('SkillAbnormalEffect')
    abnormal_meta = {_v(r['ID']): r for r in load_rows('SkillAbnormal')}

    skill_types = Counter(_short(e['EffectType']) for e in effects)
    abnormal_types = Counter(_short(e['AbnormalEffectType']) for e in abnormal_effects)

    class_skills = json.loads(CLASS_SKILLS.read_text(encoding='utf-8'))['skills']
    boxes = json.loads(BOXES.read_text(encoding='utf-8'))
    areas = json.loads(SKILL_AREAS.read_text(encoding='utf-8'))
    world = json.loads(WORLD_NPCS.read_text(encoding='utf-8'))
    ported = json.loads(PORTED.read_text(encoding='utf-8'))
    dungeon = json.loads(DUNGEON_BUFFS.read_text(encoding='utf-8')) if DUNGEON_BUFFS.exists() else {}
    known_abnormals = set(boxes.get('abnormals', {})) | set(areas.get('abnormal_effects', {})) \
        | set(ported.get('abnormals', {})) | {str(k) for k in (dungeon.get('abnormals') or {})}

    def timed_rows(skill_id):
        skill = skills.get(skill_id)
        if not skill:
            return []
        out = []
        for timed in skill.get('SkillEffectTimeDataList') or []:
            gid = _v(timed.get('SkillEffectGroupId', 0))
            if not gid:
                continue
            side = _filter_side(filters.get(_v(timed.get('SkillEffectFilterId', 0))))
            out.extend((side, e) for e in by_group.get(gid, []))
        return out

    # ---- passives: skill -> abnormal ids ----
    passives = {}
    for sid_str, row in class_skills.items():
        if row.get('type') != 'Passive':
            continue
        sid = int(sid_str)
        ids = [int(a) for a in (boxes.get('passives') or {}).get(sid_str, [])]
        if not ids:
            ids = [_int(e['EffectValueList'][0]) for _, e in timed_rows(sid)
                   if _short(e['EffectType']) == 'Abnormal_Passive' and e.get('EffectValueList')]
            ids = [a for a in ids if a > 0]
        if ids:
            passives[sid_str] = sorted(set(ids))
    abnormal_skill = {a: int(s) for s, ids in passives.items() for a in ids}

    # ---- triggers by abnormal ----
    triggers: dict[str, list] = defaultdict(list)
    for row in abnormal_effects:
        effect_type = _short(row['AbnormalEffectType'])
        if effect_type not in TRIGGER_KINDS:
            continue
        values = [str(v) for v in row.get('Values') or []]
        spec = trigger_spec(_v(row['ID']), effect_type, values, _none(row.get('AbnormalEffectLvGroupId')))
        if spec is None:
            continue
        aid = _v(row['AbnormalId'])
        if aid in abnormal_skill:
            spec['skill'] = abnormal_skill[aid]
        triggers[str(aid)].append(spec)
    # Several rows of one abnormal with the same trigger type and a different level group (Sorcerer_Passive009_LvUp_AB1 / AB4)
    # are upgrade tiers the export does not say how to unlock: the first row is the base, the rest are kept as variants.
    for aid, specs in triggers.items():
        seen = {}
        for spec in specs:
            key = spec['type']
            if key in seen and seen[key]['proc_skill'] != spec['proc_skill']:
                spec['variant'] = True
            elif key in seen:
                spec['duplicate'] = True
            else:
                seen[key] = spec
        triggers[aid] = [s for s in specs if not s.get('duplicate')]

    # ---- proc skills: trigger procs and CastSkill [interval, skill, ...] rows of the abnormals the server knows ----
    proc_ids = {s['proc_skill'] for specs in triggers.values() for s in specs}
    cast_abnormals = known_abnormals | {str(a) for a in triggers} | {str(a) for ids in passives.values() for a in ids}
    for row in abnormal_effects:
        if _short(row['AbnormalEffectType']) == 'CastSkill' and str(_v(row['AbnormalId'])) in cast_abnormals:
            vals = row.get('Values') or []
            if len(vals) > 1 and _int(vals[1]) > 0:
                proc_ids.add(_int(vals[1]))
    proc_skills = {}
    level_groups = set()
    for pid in sorted(proc_ids):
        rows = []
        for side, e in timed_rows(pid):
            t = _short(e['EffectType'])
            if t == 'NoEffect':
                continue
            entry = {'id': _v(e['ID']), 'type': t, 'side': side, 'values': [str(v) for v in e.get('EffectValueList') or []][:28]}
            group = _none(e.get('SkillEffectLvGroupId'))
            if group:
                entry['level_group'] = group
                level_groups.add(group)
            rows.append(entry)
        skill = skills.get(pid) or {}
        proc_skills[str(pid)] = {'name': skill.get('SkillString_Key') or '', 'rows': rows}

    # ---- drains (life steal) and HP-ratio damage ----
    drains, ratio_damage = {}, {}
    npc_skill_ids = {int(k) for k in world.get('npc_skills', {})}
    for sid, skill in skills.items():
        for side, e in timed_rows(sid):
            if _short(e['EffectType']) != 'Damage':
                continue
            vals = [str(v) for v in e.get('EffectValueList') or []]
            if len(vals) > DRAIN_KIND and _none(vals[DRAIN_KIND]):
                lo, hi = _pair(vals[DRAIN_RATIO])
                drains.setdefault(str(sid), {
                    'kind': vals[DRAIN_KIND], 'ratio_min': lo, 'ratio_max': hi, 'cap': _int(vals[DRAIN_CAP]),
                    'results': _results(vals[DRAIN_RESULTS]), 'effect_id': _v(e['ID']),
                })
            if sid in npc_skill_ids and len(vals) > DMG_RATIO_CAP and vals[DMG_CALC] in ('MaxHPRatio', 'CurrentHPRatio'):
                ratio_damage.setdefault(str(sid), {
                    'kind': vals[DMG_CALC], 'ratio': _int(vals[DMG_RATIO]), 'cap': _int(vals[DMG_RATIO_CAP], -1), 'effect_id': _v(e['ID']),
                })

    # ---- NPC abnormal rows the ported table lacks ----
    ported_npc = set((ported.get('npc_skills') or {}).keys())
    npc_abnormals = {}
    for sid in sorted(npc_skill_ids):
        if str(sid) in ported_npc:
            continue
        rows = []
        for side, e in timed_rows(sid):
            if _short(e['EffectType']) not in ('Abnormal', 'Abnormal_True', 'Abnormal_Cc') or side == 'self':
                continue
            vals = e.get('EffectValueList') or []
            if len(vals) < 3 or _int(vals[2]) <= 0:
                continue
            if any(r[0] == _int(vals[2]) for r in rows):
                continue   # later rows of the same abnormal are conditional variants (spec / stack conditions); the first is the base
            chance = _int(vals[4], 10000) if len(vals) > 4 else 10000
            rows.append([_int(vals[2]), _int(vals[0]), _int(vals[1]), chance if chance > 0 else 10000, max(1, _int(vals[3], 1)) if len(vals) > 3 else 1])
        if rows:
            npc_abnormals[str(sid)] = rows

    # ---- abnormal effect rows nobody else carries ----
    wanted = {int(a) for a in triggers} | {a for ids in passives.values() for a in ids} \
        | {r[0] for rows in npc_abnormals.values() for r in rows}
    for proc in proc_skills.values():
        for r in proc['rows']:
            if r['type'].startswith('Abnormal') and len(r['values']) > 2 and _int(r['values'][2]) > 0:
                wanted.add(_int(r['values'][2]))
    extra_effects: dict[str, dict] = {}
    first_tier: dict[tuple, str] = {}
    for row in abnormal_effects:
        aid = _v(row['AbnormalId'])
        if aid not in wanted or str(aid) in known_abnormals:
            continue
        kind = _short(row['AbnormalEffectType'])
        if kind in ('None', 'Max'):
            continue
        # One abnormal lists every upgrade tier's rows (..._LvUp_AB1, _AB4, ...): only the first tier per effect type applies.
        tier = _none(row.get('AbnormalEffectLvGroupId'))
        if first_tier.setdefault((aid, kind), tier) != tier:
            continue
        meta = abnormal_meta.get(aid, {})
        entry = extra_effects.setdefault(str(aid), {'group': int(meta.get('GroupId') or aid), 'effects': []})
        entry['effects'].append([_v(row['ID']), kind, *[str(v) for v in row.get('Values') or []][:12]])

    # ---- level tables for referenced groups ----
    skill_levels = defaultdict(dict)
    for row in load_rows('SkillEffectLv'):
        group = row.get('SkillEffectLvGroupId')
        if group in level_groups:
            skill_levels[group][str(row['SkillEffectLv'])] = [str(v) for v in row.get('EffectValueList') or []][:28]
    trig_groups = {s.get('level_group') for specs in triggers.values() for s in specs if s.get('level_group')}
    abnormal_levels = defaultdict(dict)
    for row in load_rows('SkillAbnormalEffectLv'):
        group = row.get('AbnormalEffectLevelGroupId')
        if group in trig_groups:
            abnormal_levels[group][str(row['AbnormalEffectLevel'])] = [str(v) for v in row.get('Values') or []][:12]

    # ---- PC skill elements (TriggerSkillByAttackAll element filters) ----
    elements = {}
    if any(s.get('element') for specs in triggers.values() for s in specs):
        for sid_str in class_skills:
            attr = _none(_short((skills.get(int(sid_str)) or {}).get('AttributeType', '')))
            if attr:
                elements[sid_str] = attr

    def compact(levels: dict) -> dict:
        """Only the levels whose values change from the level before (the server takes the highest stored level <= wanted)."""
        out, last = {}, None
        for lv, vals in sorted(levels.items(), key=lambda kv: int(kv[0])):
            if vals != last:
                out[lv] = vals
                last = vals
        return out

    return {
        'generated_by': 'tools/skill_effect_engine.py',
        'source': str(DEFAULT_EXPORT.relative_to(ROOT)),
        'note': 'GENERATED - do not edit by hand. Rerun python tools/skill_effect_engine.py. See docs/SKILL-EFFECT-ENGINE.md.',
        'skill_effect_types': dict(sorted(skill_types.items())),
        'abnormal_effect_types': dict(sorted(abnormal_types.items())),
        'passives': dict(sorted(passives.items(), key=lambda kv: int(kv[0]))),
        'triggers': dict(sorted(triggers.items(), key=lambda kv: int(kv[0]))),
        'proc_skills': proc_skills,
        'drains': dict(sorted(drains.items(), key=lambda kv: int(kv[0]))),
        'ratio_damage': dict(sorted(ratio_damage.items(), key=lambda kv: int(kv[0]))),
        'npc_abnormals': npc_abnormals,
        'skill_elements': dict(sorted(elements.items(), key=lambda kv: int(kv[0]))),
        'abnormal_effects': dict(sorted(extra_effects.items(), key=lambda kv: int(kv[0]))),
        'effect_levels': {g: compact(v) for g, v in sorted(skill_levels.items())},
        'abnormal_effect_levels': {g: compact(v) for g, v in sorted(abnormal_levels.items())},
    }


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    parser.add_argument('--check', action='store_true', help='exit 1 when the committed file differs from a fresh build')
    args = parser.parse_args(argv)
    data = build()
    if args.check:
        current = json.loads(OUT.read_text(encoding='utf-8')) if OUT.exists() else None
        if current != data:
            print(f'{OUT.relative_to(ROOT)} is stale: run python tools/skill_effect_engine.py', file=sys.stderr)
            return 1
        print('skill-effect-engine.json up to date')
        return 0
    write_data_json(OUT, data)
    print(f'{OUT.relative_to(ROOT)}: {len(data["triggers"])} trigger abnormals, {len(data["proc_skills"])} proc skills, '
          f'{len(data["drains"])} drains, {len(data["ratio_damage"])} ratio-damage NPC skills, {len(data["npc_abnormals"])} NPC abnormal skills, '
          f'{len(data["abnormal_effects"])} extra abnormals, {len(data["passives"])} passives')
    return 0


if __name__ == '__main__':
    sys.exit(main())
