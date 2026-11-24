"""SkillCond.dat use conditions (SkillCondTableItem) and self-buff grants for conditional skills.

Client tooltip chains (e.g. Ranger Suppressing Arrow: [Precision icon] -> skill, \"while Precision is active\")
come from SkillCond.dat rows linked to skills via CondSkillList, with:
  CondType            ESkillCondType (HasAbnormalGroupId, HasAbnormalEffectType)
  CondAbnormalGroupId / CondAbnormalEffectType
  CondSelectTarget    ECondSelectTarget (Self, Target)

When a prerequisite skill is cast it applies the Precision (etc.) abnormal; the client greys the conditional skill until
that buff is active (0x382a). The server mirrors the same rules and applies grants on accept.

Inputs (research/local; optional):
  skillcond-*/table-000.json or skill-cond-*/table-000.json   SkillCond.dat
  skill-tables-*/table-000.json                               Skill.dat (ToggleOnAbnormalId)
  skill-tables-*/table-001.json                               SkillEffect.dat (self Abnormal_True)

Without those exports, tools/data/skill-cond-overrides.json supplies improvised rows (skill_cond_source \"override\").

    python tools/skill_cond.py          # patch tools/data/class-skills.json
    python tools/skill_cond.py --check
"""
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from data_json import write_data_json  # noqa: E402

DATA = ROOT / 'tools/data/class-skills.json'
OVERRIDES = ROOT / 'tools/data/skill-cond-overrides.json'
LOCAL = Path(os.environ.get('AION_RESEARCH_LOCAL', ROOT / 'research/local'))


def _short(value):
    return str(value).split('::')[-1]


def _value(v):
    return v['Value'] if isinstance(v, dict) else v


def _rows(source):
    if isinstance(source, str) and source.startswith('client:'):
        from client_export import load_rows
        return load_rows(source.split(':', 1)[1])
    return json.loads(Path(source).read_text(encoding='utf-8'))['Properties']['Data']


def _optional_table(name_prefix):
    found = sorted(LOCAL.glob(f'{name_prefix}*/table-000.json'))
    return found[-1] if found else None


def _export_skill_cond_rows():
    try:
        from client_export import load_rows
        return load_rows('SkillCond')
    except FileNotFoundError:
        return None


def _skill_tables():
    skills = sorted(LOCAL.glob('skill-tables-*/table-000.json'))
    effects = sorted(LOCAL.glob('skill-tables-*/table-001.json'))
    skill_path = skills[-1] if skills else None
    effect_path = effects[-1] if effects else None
    if skill_path is None or effect_path is None:
        try:
            from client_export import load_rows
            load_rows('Skill')
            return ('client:Skill', 'client:SkillEffect')
        except FileNotFoundError:
            pass
    return (skill_path, effect_path)


def export_from_skill_cond():
    """skill_id -> cond fields from SkillCond.dat CondSkillList."""
    path = _optional_table('skillcond') or _optional_table('skill-cond')
    export_rows = _export_skill_cond_rows() if path is None else None
    if path is None and not export_rows:
        return {}, 'missing'
    by_skill = {}
    for row in (_rows(path) if path else export_rows):
        cond_type = _short(row.get('CondType', 'None'))
        if cond_type in ('None', 'Max'):
            continue
        group = int(_value(row.get('CondAbnormalGroupId', 0)) or 0)
        effect = _short(row.get('CondAbnormalEffectType', 'None'))
        target = _short(row.get('CondSelectTarget', 'Self'))
        payload = {'type': cond_type, 'select_target': target, 'cond_id': int(_value(row.get('ID', 0)) or 0)}
        if cond_type == 'HasAbnormalGroupId' and group:
            payload['abnormal_group'] = group
        elif cond_type == 'HasAbnormalEffectType' and effect not in ('None', 'Max'):
            payload['abnormal_effect'] = effect
        for skill in row.get('CondSkillList') or []:
            sid = int(_value(skill) or 0)
            if sid:
                by_skill[sid] = payload
    if path:
        try:
            rel = str(path.relative_to(ROOT))
        except ValueError:
            rel = str(path)
    else:
        rel = 'research/client-export/SkillCond.json.gz'
    return by_skill, rel


def _effects_by_group(effect_table):
    by_group = {}
    if effect_table is None:
        return by_group
    for row in _rows(effect_table):
        gid = int(_value(row.get('SkillEffectGroupId', 0)) or 0)
        if gid:
            by_group.setdefault(gid, []).append(row)
    return by_group


def _needed_self_groups(cond_by_skill):
    """Abnormal groups that unlock Self-gated conditional skills (SkillCond.dat)."""
    groups = set()
    for row in cond_by_skill.values():
        if row.get('type') != 'HasAbnormalGroupId':
            continue
        if row.get('select_target', 'Self') != 'Self':
            continue
        group = int(row.get('abnormal_group') or 0)
        if group:
            groups.add(group)
    return groups


def _self_abnormal_grants(skill_table, effect_table, cond_by_skill):
    """skill_id -> grant from toggles and caster Abnormal_True rows that feed SkillCond needs."""
    grants = _self_abnormal_grants_toggle(skill_table)
    grants.update(_self_abnormal_grants_caster(skill_table, effect_table, cond_by_skill))
    return grants


def _self_abnormal_grants_caster(skill_table, effect_table, cond_by_skill):
    """Skills whose first caster Abnormal_True enables another skill's Self HasAbnormalGroupId gate."""
    needed = _needed_self_groups(cond_by_skill)
    if not needed or skill_table is None or effect_table is None:
        return {}
    by_group = _effects_by_group(effect_table)
    skills = {int(_value(r['ID'])): r for r in _rows(skill_table)}
    grants = {}
    for sid, sk in skills.items():
        if sid < 11_000_000 or sid >= 20_000_000:
            continue
        for slot in sk.get('SkillEffectTimeDataList') or []:
            gid = int(_value(slot.get('SkillEffectGroupId', 0)) or 0)
            if not gid:
                continue
            for row in by_group.get(gid, []):
                et = _short(row.get('EffectType', 'None'))
                if et not in ('Abnormal_True', 'Abnormal'):
                    continue
                tt = _short(row.get('TargetType', 'None'))
                if tt not in ('Self', 'MainSelf', 'Caster', 'None', ''):
                    continue
                values = row.get('EffectValueList') or []
                abnormal = _int(values, 2)
                if not abnormal or abnormal not in needed:
                    continue
                min_ms = _int(values, 0)
                max_ms = _int(values, 1)
                duration = max_ms if max_ms > 0 else (min_ms if min_ms > 0 else -1)
                grants[sid] = {
                    'abnormal': abnormal,
                    'duration_ms': duration,
                    'select_target': 'Self',
                    'source': 'SkillEffect.dat',
                }
                break
            if sid in grants:
                break
    return grants


def _self_abnormal_grants_toggle(skill_table):
    """skill_id -> grant from Skill.dat ToggleOnAbnormalId (self toggle buffs)."""
    if skill_table is None:
        return {}
    grants = {}
    for row in _rows(skill_table):
        sid = row['ID']['Value']
        toggle = int(_value(row.get('ToggleOnAbnormalId', 0)) or 0)
        if toggle:
            grants[sid] = {'abnormal': toggle, 'duration_ms': -1, 'source': 'ToggleOnAbnormalId'}
    return grants


def _int(values, i):
    return int(values[i]) if i < len(values) and str(values[i]).lstrip('-').isdigit() else 0


def load_overrides():
    if not OVERRIDES.is_file():
        return {'needs': {}, 'grants': {}, 'evidence': {}}
    doc = json.loads(OVERRIDES.read_text(encoding='utf-8'))
    return {
        'needs': {int(k): v for k, v in (doc.get('needs') or {}).items()},
        'grants': {int(k): v for k, v in (doc.get('grants') or {}).items()},
        'evidence': doc.get('evidence') or {},
    }


def patch(doc):
    """Merge SkillCond export + table grants + overrides into class-skills.json skills."""
    needs, need_src = export_from_skill_cond()
    skill_table, effect_table = _skill_tables()
    overrides = load_overrides()
    for sid, row in overrides['needs'].items():
        if sid not in needs:
            needs[sid] = {k: v for k, v in row.items() if k != 'evidence'}
    grants = _self_abnormal_grants(skill_table, effect_table, needs)
    for sid, row in overrides['grants'].items():
        grants[sid] = {k: v for k, v in row.items() if k != 'evidence'}
    # Mastery variants (13060047, …) carry the grant row; the kit learns the family id (13060000).
    for sid, grant in list(grants.items()):
        root = sid // 10_000 * 10_000
        grants.setdefault(root, grant)

    patched_needs, patched_grants = [], []
    for sid_str, skill in doc.get('skills', {}).items():
        if skill.get('type') != 'Active':
            continue
        sid = int(sid_str)
        family = sid // 10_000 * 10_000
        if sid in needs:
            if skill.get('skill_cond') and skill.get('skill_cond_source') != 'override':
                pass
            else:
                skill['skill_cond'] = needs[sid]
                skill['skill_cond_source'] = 'override' if sid in overrides['needs'] else 'SkillCond.dat'
                patched_needs.append(sid_str)
        else:
            skill.pop('skill_cond', None)
            skill.pop('skill_cond_source', None)

        grant = grants.get(sid) or grants.get(family)
        if grant:
            if skill.get('skill_cond_grant') and skill.get('skill_cond_grant_source') != 'override':
                pass
            else:
                g = {k: grant[k] for k in ('abnormal', 'duration_ms', 'select_target') if k in grant}
                skill['skill_cond_grant'] = g
                src = grant.get('source', 'tables')
                skill['skill_cond_grant_source'] = 'override' if sid in overrides['grants'] or family in overrides['grants'] else src
                patched_grants.append(sid_str)
        else:
            skill.pop('skill_cond_grant', None)
            skill.pop('skill_cond_grant_source', None)

    ev = doc.setdefault('evidence', {})
    parts = []
    if need_src != 'missing':
        parts.append(f'needs from {need_src}')
    if skill_table:
        label = skill_table if isinstance(skill_table, str) else skill_table.parent.name
        parts.append(f'grants from Skill.dat/SkillEffect ({label})')
    if overrides['evidence']:
        parts.append('; '.join(f'{k}: {v}' for k, v in overrides['evidence'].items()))
    ev['skill_cond'] = (
        'SkillCond.dat SkillCondTableItem (CondType, CondAbnormalGroupId, CondAbnormalEffectType, CondSkillList, CondSelectTarget); '
        'grants from ToggleOnAbnormalId; self Abnormal_True on caster filters is applied from skill-areas caster_debuffs at hit time; skill_cond_*_source \"override\" marks OURS. '
        + ('; '.join(parts) if parts else 'tables not present: overrides only'))
    return patched_needs, patched_grants


def main(argv=None):
    argv = sys.argv[1:] if argv is None else argv
    doc = json.loads(DATA.read_text(encoding='utf-8'))
    needs, grants = patch(doc)
    if '--check' in argv:
        current = DATA.read_text(encoding='utf-8')
        tmp = ROOT / 'tools/data/.class-skills.skill-cond.check.json'
        write_data_json(tmp, doc)
        same = tmp.read_text(encoding='utf-8') == current
        tmp.unlink()
        print('up to date' if same else 'class-skills.json needs tools/skill_cond.py')
        return 0 if same else 1
    write_data_json(DATA, doc)
    print(f'{len(needs)} skills with skill_cond, {len(grants)} with skill_cond_grant')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
