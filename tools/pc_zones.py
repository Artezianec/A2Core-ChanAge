"""Player damage zones: skills whose Spawn row places a PC_Summon marker with no attacks (Bittercold Wind).

The cast spawns NpcData on the target; damage is applied by the marker's pulse skills in the client (SkillEffect group
152800021 Damage on skill 15280002/15280003). Until summon AI runs those skills, the server ticks area damage from this file
(tools/data/pc-zones.json).

    python tools/pc_zones.py build     # refresh from research/client-export (CLIENT_EXPORT_DIR)

See also tools/pc_summons.py and server/Aion.World/PcZones.cs.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "tools/data/pc-zones.json"
CLASS_SKILLS = ROOT / "tools/data/class-skills.json"
PC_SUMMONS = ROOT / "tools/data/pc-summons.json"
PC_TRAPS = ROOT / "tools/data/pc-traps.json"
DEFAULT_EXPORT = ROOT / "research/client-export/2026-11-22"

sys.path.insert(0, str(ROOT / "tools"))
from client_export import export_dir, load_table  # noqa: E402
from data_json import write_data_json  # noqa: E402


def _value(cell):
    if isinstance(cell, dict) and "Value" in cell:
        return cell["Value"]
    return cell


def _enum(cell) -> str:
    if isinstance(cell, str):
        return cell.split("::")[-1]
    return str(cell)


def _rows(table: str, directory: Path | None = None):
    return load_table(table, directory)["Properties"]["Data"]


def _effects_by_group(directory: Path | None = None) -> dict[int, list[dict]]:
    out: dict[int, list[dict]] = {}
    for row in _rows("SkillEffect", directory):
        gid = _value(row.get("SkillEffectGroupId", 0))
        if isinstance(gid, int) and gid:
            out.setdefault(gid, []).append(row)
    return out


def _skill_by_id(directory: Path | None = None) -> dict[int, dict]:
    return {_value(row["ID"]): row for row in _rows("Skill", directory)}


def _spawn_lifetime_ms(values: list) -> int | None:
    if len(values) <= 7:
        return None
    raw = str(values[7])
    parts = [p.strip() for p in raw.split(",")]
    if len(parts) >= 2 and parts[1].isdigit():
        return int(parts[1])
    return None


def _damage_row(effect: dict) -> dict | None:
    if _enum(effect.get("EffectType")) != "Damage":
        return None
    values = effect.get("EffectValueList") or []
    if len(values) < 4:
        return None
    num = lambda i: int(values[i]) if i < len(values) and str(values[i]).lstrip("-").isdigit() else None
    payload = {
        "flat": [num(0), num(1)],
        "ratio": [num(2), num(3)],
        "effect_id": _value(effect["ID"]),
    }
    if len(values) > 4:
        payload["value4"] = values[4]
    group = effect.get("SkillEffectLvGroupId")
    if group not in (None, "", "None"):
        payload["level_group"] = _value(group) if isinstance(group, dict) else group
    return payload


def _filter_shape(filter_id: int, directory: Path | None = None) -> tuple[int | None, int | None]:
    for row in _rows("SkillEffectFilter", directory):
        if _value(row.get("ID")) != filter_id:
            continue
        vals = row.get("EffectRangeValues") or []
        radius = int(vals[4]) if len(vals) > 4 else None
        return radius, row.get("TargetCountMax")
    return None, None


def _marker_pulse_tick_ms(skill_table: dict[int, dict], marker_skills: list[int]) -> int | None:
    """NeedCoolTime of the marker's first damage pulse skill (15280002/15280003 family), if present."""
    for sid in marker_skills:
        if sid not in skill_table:
            continue
        cool = skill_table[sid].get("NeedCoolTime")
        if isinstance(cool, (int, float)) and cool > 0:
            return int(cool)
    return None


def build(directory: Path | None = None, only: set[str] | None = None) -> dict:
    directory = export_dir(directory)
    effects = _effects_by_group(directory)
    skills = _skill_by_id(directory)
    pc_summons = json.loads(PC_SUMMONS.read_text(encoding="utf-8"))
    class_skills = json.loads(CLASS_SKILLS.read_text(encoding="utf-8"))["skills"]
    class_ids = {int(k) for k in class_skills}
    trap_ids = {str(k) for k in json.loads(PC_TRAPS.read_text(encoding="utf-8")).get("traps", {})}
    if only is None:
        only = (
            set(json.loads(OUT.read_text(encoding="utf-8")).get("zones", {}))
            if OUT.is_file()
            else set()
        )
    only -= trap_ids
    if not only:
        only = {
            skill_id_s
            for skill_id_s, entry in pc_summons.get("skills", {}).items()
            if int(skill_id_s) in class_ids
            and not pc_summons.get("templates", {}).get(str(entry["npc"]), {}).get("skills")
            and skill_id_s not in trap_ids
        }
    zones: dict[str, dict] = {}

    for skill_id_s, entry in pc_summons.get("skills", {}).items():
        if skill_id_s not in only:
            continue
        skill_id = int(skill_id_s)
        if skill_id not in class_ids:
            continue
        npc_id = entry["npc"]
        template = pc_summons.get("templates", {}).get(str(npc_id), {})
        if template.get("skills"):
            continue  # real companion summon, not a marker zone
        skill_row = skills.get(skill_id)
        if not skill_row:
            continue
        spawns = []
        for timed in skill_row.get("SkillEffectTimeDataList") or []:
            gid = _value(timed.get("SkillEffectGroupId"))
            for eff in effects.get(gid, []):
                if _enum(eff.get("EffectType")) == "Spawn":
                    spawns.append(eff)
        if not spawns:
            continue
        spawn = spawns[0]
        vals = spawn.get("EffectValueList") or []
        lifetime = _spawn_lifetime_ms(vals)
        damage = None
        pulse_candidates: list[int] = []
        # Bittercold Wind: marker pulse skills 1528000x1/2/3 (Damage group 152800021 on 15280002).
        base = skill_id // 10_000 * 10_000
        for offset in range(1, 100):
            cand = base + offset
            if cand not in skills:
                break
            g = _value((skills[cand].get("SkillEffectTimeDataList") or [{}])[0].get("SkillEffectGroupId", 0))
            for eff in effects.get(g, []):
                if _enum(eff.get("EffectType")) == "Damage":
                    pulse_candidates.append(cand)
                    if damage is None:
                        damage = _damage_row(eff)
                    break
            if damage and offset > 3:
                break
        if damage is None:
            # Same pattern as the first timed row's group (Spawn only): look for a sibling Damage group *21.
            for gid, rows in effects.items():
                if gid // 10 == skill_id and gid % 100 == 21:
                    for eff in rows:
                        damage = _damage_row(eff)
                        if damage:
                            break
                if damage:
                    break
        if damage is None:
            continue
        tick_ms = _marker_pulse_tick_ms(skills, pulse_candidates) or 1000
        if tick_ms < 250:
            tick_ms = 1000  # 210 ms is the marker skill cd; zone ticks at 1 s until pulse AI exists (OURS).
        timed = skill_row.get("SkillEffectTimeDataList") or []
        filter_id = _value(timed[0].get("SkillEffectFilterId")) if timed else 0
        for t in timed:
            fid = _value(t.get("SkillEffectFilterId"))
            if fid and str(fid).endswith("3"):
                filter_id = fid
                break
        radius, max_targets = _filter_shape(filter_id, directory) if filter_id else (None, None)
        # AoE filter is on row 3 (152800003) for Bittercold; fall back to the pulse damage filter.
        if max_targets is None:
            for t in timed:
                fid = _value(t.get("SkillEffectFilterId"))
                if fid:
                    radius, max_targets = _filter_shape(fid, directory)
                    if max_targets:
                        break
        zone = {
            "name": class_skills.get(skill_id_s, {}).get("name") or template.get("name", skill_id_s),
            "npc": int(vals[9]) if len(vals) > 9 else npc_id,
            "lifetime_ms": lifetime or 6000,
            "tick_ms": tick_ms,
            "radius": radius or 400,
            "max": max_targets or 4,
            "damage": damage,
            "notes": {
                "source": f"client export {directory.name}",
                "spawn_lifetime_ms": f"Spawn EffectValueList[7] = {vals[7] if len(vals) > 7 else '?'}",
                "damage": f"SkillEffect group {damage['effect_id'] // 10} Damage row {damage['effect_id']}",
                "tick_ms": "marker pulse NeedCoolTime when >= 250 ms else 1000 ms (OURS until summon AI)",
            },
        }
        zones[skill_id_s] = zone

    return {
        "source": "Built by tools/pc_zones.py from " + (
            directory.resolve().relative_to(ROOT).as_posix()
            if directory.resolve().is_relative_to(ROOT)
            else directory.name
        ),
        "zones": dict(sorted(zones.items(), key=lambda kv: int(kv[0]))),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("command", nargs="?", choices=["build"], default="build")
    parser.add_argument("--dir", dest="directory", help="Client export directory")
    args = parser.parse_args()
    write_data_json(OUT, build(Path(args.directory) if args.directory else None))
    print(f"wrote {OUT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())