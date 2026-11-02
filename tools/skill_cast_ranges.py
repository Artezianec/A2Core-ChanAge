"""Infer Skill.dat NeedSkillUseRange (cm) for PC skills when the Skill table is not exported.

Official NeedSkillUseRange lives on Skill.dat; this script writes tools/data/skill-cast-ranges.json
so the server can validate cast distance. When research/local Skill table is present, prefer
`python tools/class_skills.py` with table export (cast_row adds range from facts).

Heuristics (OURS until table export): projectile skills 2500, bow/gun/orb/book MainTarget 2500,
MainSelf 800, ground placement 2000, MainTarget of a ranged class (Ranger, Sorcerer,
Elementalist, Cleric) 2500, other MainTarget melee 650.
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CLASS = ROOT / 'tools/data/class-skills.json'
AREAS = ROOT / 'tools/data/skill-areas.json'
OUT = ROOT / 'tools/data/skill-cast-ranges.json'

RANGED_WEAPONS = frozenset({'Bow', 'Gun', 'Orb', 'Book', 'Magicbook', 'Harp'})
# Class id prefix (skill_id // 1_000_000): 14 Ranger, 15 Sorcerer, 16 Elementalist, 17 Cleric. Their MainTarget
# skills (basic attacks and 'All'-weapon spells such as Ice Chain / Cold Wave) are cast from range.
RANGED_CLASSES = frozenset({14, 15, 16, 17})
DEFAULT_MELEE = 650
DEFAULT_RANGED = 2500
DEFAULT_SELF = 800
DEFAULT_GROUND = 2000


def family_area(areas, skill_id):
    sid = str(skill_id)
    if sid in areas:
        return areas[sid]
    fam = str(int(skill_id) // 10_000 * 10_000)
    return areas.get(fam)


def infer_range(skill_id, skill, areas):
    area = family_area(areas, skill_id)
    if area and area.get('projectile', 0) > 0:
        return DEFAULT_RANGED
    if area and area.get('ground'):
        return DEFAULT_GROUND
    target = skill.get('target') or ''
    weapons = skill.get('weapons') or []
    if target == 'MainSelf':
        return DEFAULT_SELF
    if target == 'MainTarget':
        if any(w in RANGED_WEAPONS for w in weapons) or skill_id // 1_000_000 in RANGED_CLASSES:
            return DEFAULT_RANGED
        return DEFAULT_MELEE
    return DEFAULT_RANGED


def main():
    skills = json.loads(CLASS.read_text(encoding='utf-8'))['skills']
    areas = json.loads(AREAS.read_text(encoding='utf-8')) if AREAS.exists() else {}
    out = {}
    for sid, row in skills.items():
        r = infer_range(int(sid), row, areas)
        if r > 0:
            out[sid] = r
    write = {'note': 'NeedSkillUseRange cm (Skill.dat); heuristics until table export', 'skills': out}
    OUT.write_text(json.dumps(write, indent=2, sort_keys=True) + '\n', encoding='utf-8')
    print(f'Wrote {len(out)} ranges to {OUT}', file=sys.stderr)


if __name__ == '__main__':
    main()
