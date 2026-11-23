"""Apply research/client-export/2026-11-22 gzip tables to committed tools/data JSON.

Preserves deliberate overrides only where the export is empty and the row is marked override
(skill damage_source, skill_cond_source, …). Client rows win when present.

    python tools/client_skill_sync.py
    python tools/client_skill_sync.py --dry-run
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))

from client_export import load_rows
from data_json import write_data_json

import class_skills as cs
import skill_areas

CLASS_SKILLS = ROOT / 'tools/data/class-skills.json'
SKILL_AREAS = ROOT / 'tools/data/skill-areas.json'
BOXES = ROOT / 'tools/data/boxes.json'
WORLD_NPCS = ROOT / 'tools/data/world-npcs.json'
PASSIVE_TRIGGERS = ROOT / 'tools/data/passive-triggers.json'
PC_SKILL_MIN, PC_SKILL_MAX = cs.PC_SKILL_MIN, cs.PC_SKILL_MAX


def _short(value):
    return str(value).split('::')[-1]


def _value(v):
    return v['Value'] if isinstance(v, dict) else v


def _effect_groups_from_export():
    groups = defaultdict(list)
    for row in load_rows('SkillEffect'):
        groups[_value(row['SkillEffectGroupId'])].append(row)
    return groups


def _catalog_entry(skill_id, skill_row, groups):
    return {
        'id': skill_id,
        'name': skill_row.get('SkillString_Key'),
        'raw': skill_row,
        'facts': skill_row,
        'relationships': {
            'effects': [
                {'rows': groups.get(_value(timed.get('SkillEffectGroupId', 0)), [])}
                for timed in skill_row.get('SkillEffectTimeDataList') or []
            ]
        },
    }


def _skill_lv_index():
    found = {}
    for row in load_rows('SkillLv'):
        gid = row.get('SkillLvGroupId')
        if gid in (None, '', 'None'):
            continue
        found.setdefault(gid, {})[row['SkillLv']] = row
    return found


def _effect_lv_index():
    found = defaultdict(dict)
    for row in load_rows('SkillEffectLv'):
        gid = row.get('SkillEffectLvGroupId')
        if gid in (None, '', 'None'):
            continue
        found[gid][row['SkillEffectLv']] = row.get('EffectValueList') or []
    return found


def _charge_index():
    return {_value(r['ID']): r for r in load_rows('SkillCharge')}


def _hold_charge_field(skill_row, charges):
    cid = _value(skill_row.get('ChargeId', 0)) or 0
    if not cid or cid not in charges:
        return {}
    data = charges[cid].get('SkillChargeDataList') or []
    levels = []
    for idx, stage in enumerate(data):
        min_hold = int(stage.get('ChargeTime') or stage.get('AddChargeTime') or 0)
        levels.append({'min_hold_ms': min_hold, 'damage_row': idx})
    if not levels:
        return {}
    return {'hold_charge': {'max_hold_ms': int(charges[cid].get('AddChargeLimitTime') or 0) or None, 'levels': levels}}


def patch_class_skills(dry_run: bool):
    skills_by_id = {r['ID']['Value']: r for r in load_rows('Skill')}
    groups = _effect_groups_from_export()
    skill_lv = _skill_lv_index()
    effect_lv = _effect_lv_index()
    charges = _charge_index()

    doc = json.loads(CLASS_SKILLS.read_text(encoding='utf-8'))
    cast = doc['skills']
    changed = 0
    damage_cleared = 0

    for key, row in cast.items():
        sid = int(key)
        src = skills_by_id.get(sid)
        if not src:
            continue
        entry = _catalog_entry(sid, src, groups)
        new_row = cs.cast_row(entry)
        if _hold_charge_field(src, charges):
            new_row.update(_hold_charge_field(src, charges))
        # Cooldown / cast / cost always owned by client when export exists
        for field in ('cooldown_ms', 'casting_ms', 'cost', 'chain_prev', 'chain_rate', 'chain_window_ms',
                      'chain_deactivate', 'context_register', 'group_cooldown_id'):
            if row.get(field) != new_row.get(field):
                row[field] = new_row[field]
                changed += 1
        if new_row.get('range') is not None and row.get('range') != new_row.get('range'):
            row['range'] = new_row['range']
            changed += 1
        export_damage = new_row.get('damage') or []
        if export_damage:
            if row.get('damage_source') == 'override':
                damage_cleared += 1
                row.pop('damage_source', None)
            if row.get('damage') != export_damage:
                row['damage'] = export_damage
                changed += 1
        elif not export_damage and row.get('damage_source') != 'override':
            pass  # keep existing non-override rows
        lv_group = src.get('SkillLvGroupId')
        if lv_group not in (None, '', 'None') and lv_group in skill_lv:
            levels = skill_lv[lv_group]
            if sorted(levels) == list(range(2, max(levels) + 1)):
                times = [int(levels[lv].get('NeedCoolTime') or 0) for lv in sorted(levels)]
                base = int(new_row.get('cooldown_ms') or 0)
                if any(t != base for t in times):
                    row['cooldown_levels'] = times
                    changed += 1
        groups_used = {d.get('level_group') for d in export_damage if d.get('level_group')}
        number = lambda values, i: int(values[i]) if i < len(values) and str(values[i]).lstrip('-').isdigit() else 0
        effect_levels = {}
        for group in groups_used:
            if not group or group not in effect_lv:
                continue
            levels = effect_lv[group]
            if sorted(levels) != list(range(2, max(levels) + 1)):
                continue
            effect_levels[group] = [([number(levels[lv], i) for i in (0, 1, 21, 22)]) for lv in sorted(levels)]
        if effect_levels:
            merged = {**doc.get('effect_levels', {}), **effect_levels}
            if doc.get('effect_levels') != merged:
                doc['effect_levels'] = merged
                changed += 1

    doc.setdefault('evidence', {})['client_export'] = (
        'research/client-export/2026-11-22: Skill/SkillEffect/SkillLv/SkillCharge via tools/client_skill_sync.py')
    print(f'class-skills: {changed} field updates, {damage_cleared} improvised damage rows replaced')
    if not dry_run:
        write_data_json(CLASS_SKILLS, doc)
    return changed


def _abnormal_effect_rows(wanted: set[int]):
    out = {}
    for row in load_rows('SkillAbnormalEffect'):
        aid = _value(row['AbnormalId'])
        if aid not in wanted:
            continue
        kind = _short(row.get('AbnormalEffectType'))
        if kind in ('None', 'Max'):
            continue
        out.setdefault(aid, []).append([
            _value(row['ID']) if row.get('ID') is not None else aid,
            kind,
            *[str(v) for v in row.get('Values') or []][:8],
        ])
    return {str(a): {'effects': rows, 'source': 'client'} for a, rows in sorted(out.items())}


def patch_boxes_passive_abnormals(dry_run: bool):
    doc = json.loads(BOXES.read_text(encoding='utf-8'))
    wanted = set()
    for ids in (doc.get('passives') or {}).values():
        wanted.update(int(x) for x in ids)
    abn_table = {_value(r['ID']): r for r in load_rows('SkillAbnormal')}
    effects = _abnormal_effect_rows(wanted)
    abnormals = doc.setdefault('abnormals', {})
    added = 0
    for aid_str, payload in effects.items():
        aid = int(aid_str)
        if aid_str in abnormals and abnormals[aid_str].get('effects') and abnormals[aid_str].get('source') == 'client':
            continue
        meta = abn_table.get(aid, {})
        abnormals[aid_str] = {
            'group': int(meta.get('GroupId') or aid),
            'effects': [[row[0], row[1], *row[2:]] for row in payload['effects']],
            'source': 'client',
        }
        added += 1
    print(f'boxes.json: {added} passive abnormal specs from client export')
    if not dry_run:
        write_data_json(BOXES, doc)
    return added


def rebuild_skill_areas(dry_run: bool):
    overrides = json.loads((ROOT / 'tools/data/skill-effect-overrides.json').read_text(encoding='utf-8'))
    data = skill_areas.build(overrides=overrides)
    wanted = {int(k) for k in (data.get('abnormal_effects') or {})}
    passives = json.loads(BOXES.read_text(encoding='utf-8')).get('passives') or {}
    for ids in passives.values():
        wanted.update(int(x) for x in ids)
    merged = _abnormal_effect_rows(wanted)
    abnormal_effects = data.setdefault('abnormal_effects', {})
    for key, row in merged.items():
        if abnormal_effects.get(key, {}).get('source') == 'override':
            continue
        abnormal_effects[key] = row
    skill_areas.apply_abnormal_overrides(abnormal_effects, overrides)
    skill_areas.mark_held_by_effects(data['skills'], abnormal_effects)
    data['abnormal_types'] = skill_areas.abnormal_type_names(abnormal_effects)
    print(f'skill-areas: {len(data["skills"])} skills, {len(abnormal_effects)} abnormal_effects ({sum(1 for v in abnormal_effects.values() if v.get("source")=="client")} client)')
    if not dry_run:
        write_data_json(SKILL_AREAS, data)
    return data


def patch_world_npc_skills(dry_run: bool):
    import world_npcs

    doc = json.loads(WORLD_NPCS.read_text(encoding='utf-8'))
    skill_ids = {int(k) for k in doc.get('npc_skills', {})}
    patched = world_npcs.npc_skills(skill_ids, skill_table='client:Skill', effect_table='client:SkillEffect')
    old = doc.get('npc_skills') or {}
    fixes = 0
    for sid, row in patched.items():
        prev = old.get(sid, {})
        if prev.get('damage_min') == 0 and row.get('damage_min', 0) > 0:
            fixes += 1
        old[sid] = row
    doc['npc_skills'] = old
    doc.setdefault('source', {})['npc_skills_export'] = 'research/client-export/2026-11-22 Skill + SkillEffect'
    print(f'world-npcs: {len(patched)} npc_skills rows refreshed ({fixes} gained non-zero damage)')
    if not dry_run:
        write_data_json(WORLD_NPCS, doc)
    return fixes


def _trigger_kind(effect_type: str, values: list) -> tuple[str, int]:
    t = effect_type or ''
    attacks = str(values[6]) if len(values) > 6 else ''
    if 'GetDamage' in t:
        return 'OnDamaged', 0
    if 'Critical' in attacks and 'Normal' not in attacks:
        return 'OnCrit', 0
    if 'Critical' in attacks:
        return 'OnCrit', 0  # dual: also register OnHit below when syncing
    return 'OnHit', 0


def build_passive_triggers(dry_run: bool):
    doc = json.loads(BOXES.read_text(encoding='utf-8'))
    passives = doc.get('passives') or {}
    skill_by_abnormal = {}
    for sid, abs in passives.items():
        if not (11_000_000 <= int(sid) < 20_000_000):
            continue
        for ab in abs:
            skill_by_abnormal[int(ab)] = int(sid)

    skills = {r['ID']['Value']: r for r in load_rows('Skill')}
    by_group = defaultdict(list)
    for e in load_rows('SkillEffect'):
        by_group[_value(e['SkillEffectGroupId'])].append(e)

    def proc_effects(proc_skill_id: int):
        proc = skills.get(proc_skill_id)
        if not proc:
            return []
        for timed in proc.get('SkillEffectTimeDataList') or []:
            gid = _value(timed.get('SkillEffectGroupId', 0))
            for eff in by_group.get(gid, []):
                if _short(eff.get('EffectType')) == 'Abnormal_True':
                    vals = eff.get('EffectValueList') or []
                    return [[_short(eff.get('EffectType')), str(vals[0] if vals else '0'),
                             str(vals[1] if len(vals) > 1 else vals[0] if vals else '0'),
                             str(vals[2] if len(vals) > 2 else '0')]]
        return []

    triggers = defaultdict(list)
    for row in load_rows('SkillAbnormalEffect'):
        aid = _value(row['AbnormalId'])
        sid = skill_by_abnormal.get(aid)
        if not sid:
            continue
        et = _short(row.get('AbnormalEffectType'))
        if 'Trigger' not in et and et not in ('CastSkill',):
            continue
        values = [str(v) for v in row.get('Values') or []]
        proc_id = int(values[0]) if values and str(values[0]).lstrip('-').isdigit() else 0
        effects = proc_effects(proc_id)
        if not effects:
            continue
        kind, hp = _trigger_kind(et, values)
        entry = {'on': kind, 'effects': effects, 'evidence': f'SkillAbnormalEffect {aid} {et} (client export 2026-11-22)'}
        if hp:
            entry['hp_permille'] = hp
        if 'Critical' in str(values[6] if len(values) > 6 else '') and kind == 'OnCrit':
            triggers[str(sid)].append(entry)
            triggers[str(sid)].append({**entry, 'on': 'OnHit'})
        else:
            triggers[str(sid)].append(entry)

    out = {
        'note': 'Conditional passive procs from client SkillAbnormalEffect trigger rows (tools/client_skill_sync.py).',
        'triggers': {k: v for k, v in sorted(triggers.items(), key=lambda kv: int(kv[0]))},
    }
    print(f'passive-triggers: {len(out["triggers"])} skills with client trigger rows')
    if not dry_run:
        write_data_json(PASSIVE_TRIGGERS, out)
    return len(out['triggers'])


def run_helpers(dry_run: bool):
    if dry_run:
        print('skipping skill_cond / skill_use_conditions (dry-run)')
        return
    # skill_effect_engine reads class-skills / boxes / skill-areas / world-npcs, so it runs after they are patched.
    for mod in ('skill_cond', 'skill_use_conditions', 'abnormal_stack_rules', 'skill_effect_engine'):
        subprocess.run([sys.executable, str(ROOT / 'tools' / f'{mod}.py')], check=False, cwd=ROOT)


def write_misdecode_report():
    """Fields that look empty or suspect on the 2.0.9.0 client with usmap 2.0.6.0."""
    suspects = []
    skills = {r['ID']['Value']: r for r in load_rows('Skill')}
    by_group = defaultdict(list)
    for e in load_rows('SkillEffect'):
        by_group[_value(e['SkillEffectGroupId'])].append(e)
    for sid in (1800655, 1803000):
        s = skills.get(sid)
        if not s:
            continue
        dmg = []
        for g in s.get('SkillEffectTimeDataList') or []:
            gid = _value(g.get('SkillEffectGroupId', 0))
            for e in by_group.get(gid, []):
                if _short(e.get('EffectType')) == 'Damage':
                    dmg.append(e.get('EffectValueList')[:6])
        label = dmg if dmg else 'none'
        suspects.append(f'- NPC skill {sid}: damage rows {label} (SkillEffect may be mis-decoded)')
    out = ROOT / 'tools/reports/client-export-misdecode.md'
    out.write_text(
        '# Client export 2026-11-22 — suspect fields\n\n'
        'Mapping: usmap **2.0.6.0** on client **2.0.9.0** (see research/client-export/2026-11-22/README.md).\n\n'
        + '\n'.join(suspects) + '\n',
        encoding='utf-8',
    )
    print(out)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--dry-run', action='store_true')
    args = parser.parse_args()
    patch_class_skills(args.dry_run)
    patch_boxes_passive_abnormals(args.dry_run)
    rebuild_skill_areas(args.dry_run)
    patch_world_npc_skills(args.dry_run)
    build_passive_triggers(args.dry_run)
    run_helpers(args.dry_run)
    if not args.dry_run:
        write_misdecode_report()
        subprocess.run([sys.executable, str(ROOT / 'tools/abnormal_coverage.py')], cwd=ROOT)
        subprocess.run([sys.executable, str(ROOT / 'tools/skill_audit.py')], cwd=ROOT)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())