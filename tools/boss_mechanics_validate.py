"""Validate boss encounter files (tools/data/boss-mechanics/*.json, docs/BOSS-MECHANICS-ENGINE.md).

Checks what the server's loader checks (types, required fields, anchors, shapes, $var references) and, on top, that every id
an encounter names exists in the client/tool data:

  skills     world-npcs.json npc_skills or skill-areas.json skills (cast, telegraph, zone, decal, damage.skill, skill_remap)
  NPCs       world-npcs.json templates (npcs, summon npc, npc_killed)
  env        world-objects.json maps[].objects[].env (env_obj)
  talks      npc-talks.json talks (caption)
  waves      boss-phases.json waves or map-events.json event names (summon wave)

Vocabulary (trigger, action, shape, anchor names) comes from tools/data/boss-mechanics.schema.json, the same lists the server
uses (EncounterVocabularyTests keeps them equal).

    python tools/boss_mechanics_validate.py                 # every file, exit 1 on errors
    python tools/boss_mechanics_validate.py path/to/x.json  # just these
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / "tools/data"
ENCOUNTERS = DATA / "boss-mechanics"
SCHEMA = DATA / "boss-mechanics.schema.json"

DIFFICULTIES = {
    "easy": "Easy", "exploration": "Easy",
    "normal": "Normal", "conquest": "Normal", "conquest_normal": "Normal",
    "hard": "Hard", "conquest_hard": "Hard", "advanced": "Hard",
}

TRIGGER_NEEDS = {
    "hp_below": [("pct",)],
    "hp_every": [("step",)],
    "timer": [("after_ms", "every_ms")],
    "enrage_timer": [("after_ms",)],
    "phase_enter": [("phase",)],
    "phase_exit": [("phase",)],
    "adds_dead": [("group",)],
    "add_died": [("group",)],
    "players_in_area": [("area",), ("min", "max")],
    "shield_broken": [("shield",)],
    "stacks_reached": [("debuff",), ("stacks",)],
    "npc_killed": [("npc",)],
    "counter_reached": [("counter",), ("value",)],
    "part_broken": [("part",)],
    "event": [("event",)],
}

ACTION_NEEDS = {
    "cast": [("skill",)],
    "telegraph": [("shape", "steps", "skill")],
    "zone": [("shape", "grid")],
    "soak": [("radius",)],
    "stack": [("radius",)],
    "spread": [("radius",)],
    "cover": [("id",)],
    "displace": [("mode",)],
    "summon": [("npc", "wave")],
    "despawn_group": [("group",)],
    "shield": [("id",), ("invuln", "hp", "hp_pct", "until")],
    "stagger_check": [("window_ms",)],
    "parts": [("parts", "from_client")],
    "debuff": [("debuff",)],
    "cleanse": [("debuff",)],
    "tank_swap": [("debuff",)],
    "orb": [("radius",), ("required",)],
    "move": [("to",)],
    "phase": [("phase",)],
    "notice": [("text",)],
    "caption": [("talk",)],
    "env": [("env_obj",)],
    "decal": [("skill",)],
    "set_rotation": [("rotation",)],
    "counter": [("counter",), ("add", "set")],
    "delay": [("ms",), ("do",)],
    "repeat": [("every_ms",), ("do",), ("count", "duration_ms", "ends")],
    "random": [("choices",)],
    "mark": [("name",)],
    "emit": [("event",)],
}

NESTED = ("during", "on_land", "on_break", "on_hit", "on_success", "on_fail", "on_expire", "on_max", "on_complete", "on_touch", "do", "rotation")
NUM_FIELDS = ("wind_up_ms", "every_ms", "radius", "inner", "angle", "length", "width", "distance", "delay_ms", "duration_ms", "linger_ms",
              "tick_ms", "first_tick_ms", "interval_ms", "overlap_scale", "hp", "hp_pct", "window_ms", "damage_mul", "haste_mul", "add", "set", "ms")
TRIGGER_NUMS = ("pct", "step", "after_ms", "every_ms", "value", "hold_ms")
DISPLACE_MODES = {"knockback", "pull", "to_radius", "to_point"}
MODIFY_MODES = {"phase", "mul", "set"}


def present(value) -> bool:
    """Set to something: 0 counts (summon npc 0 is the boss itself), false and [] do not."""
    return value is not None and value is not False and value != []


def vocabulary(schema: dict | None = None) -> dict[str, list[str]]:
    schema = schema or json.loads(SCHEMA.read_text(encoding="utf-8"))
    defs = schema["$defs"]
    return {
        "triggers": defs["trigger_type"]["enum"],
        "actions": defs["action_type"]["enum"],
        "shapes": defs["shape_name"]["enum"],
        "anchors": defs["anchor_keyword"]["enum"],
    }


@dataclass
class ClientData:
    skills: set[int] = field(default_factory=set)
    npcs: set[int] = field(default_factory=set)
    env: set[int] = field(default_factory=set)
    talks: set[str] = field(default_factory=set)
    waves: set[str] = field(default_factory=set)
    scripted_npcs: set[int] = field(default_factory=set)

    @classmethod
    def load(cls) -> "ClientData":
        world = json.loads((DATA / "world-npcs.json").read_text(encoding="utf-8"))
        areas = json.loads((DATA / "skill-areas.json").read_text(encoding="utf-8"))
        objects = json.loads((DATA / "world-objects.json").read_text(encoding="utf-8"))
        talks = json.loads((DATA / "npc-talks.json").read_text(encoding="utf-8"))
        phases = json.loads((DATA / "boss-phases.json").read_text(encoding="utf-8"))
        events = json.loads((DATA / "map-events.json").read_text(encoding="utf-8"))
        env = {o["env"] for m in objects["maps"].values() for o in m.get("objects", []) if "env" in o}
        waves = set(phases.get("waves") or {})
        for e in events.get("events") or []:
            if isinstance(e, dict) and e.get("name"):
                waves.add(e["name"])
        return cls(
            skills={int(k) for k in world["npc_skills"]} | {int(k) for k in areas["skills"] if str(k).isdigit()},
            npcs={int(k) for k in world["templates"]},
            env=env,
            talks=set(talks["talks"]),
            waves=waves,
            scripted_npcs={int(k) for k in phases["bosses"]},
        )


@dataclass
class Report:
    file: str
    errors: list[str] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)


class Validator:
    def __init__(self, data: ClientData | None, vocab: dict[str, list[str]] | None = None):
        self.data = data
        self.vocab = vocab or vocabulary()
        self.trigger_types = set(self.vocab["triggers"])
        self.action_types = set(self.vocab["actions"])
        self.shape_names = set(self.vocab["shapes"])
        self.anchor_names = set(self.vocab["anchors"])

    # ---- helpers ---------------------------------------------------------------------------------------------------
    def num(self, value, where: str, name: str, r: Report, vars_: set[str]):
        if value is None:
            return
        if isinstance(value, bool) or not isinstance(value, (int, float, str)):
            r.errors.append(f"{where}: {name} must be a number or \"$var\"")
        elif isinstance(value, str):
            if not re.fullmatch(r"\$[A-Za-z_][A-Za-z0-9_]*", value):
                r.errors.append(f"{where}: {name} '{value}' is not a number or \"$var\"")
            elif value[1:] not in vars_:
                r.errors.append(f"{where}: {name} references unknown var '{value}'")

    def anchor(self, value, where: str, r: Report):
        if value is None:
            return
        if isinstance(value, dict):
            if not {"x", "y"} <= set(value) or set(value) - {"x", "y", "z"}:
                r.errors.append(f"{where}: a point anchor is {{\"x\", \"y\"[, \"z\"]}}")
            return
        if not isinstance(value, str):
            r.errors.append(f"{where}: anchor must be a keyword or a point")
            return
        base, _, arg = value.partition(":")
        if base not in self.anchor_names or base == "point":
            r.errors.append(f"{where}: unknown anchor '{value}'")
        elif base in ("mark", "group") and not arg:
            r.errors.append(f"{where}: anchor '{value}' needs a name ('{base}:<name>')")

    def shape(self, value, where: str, r: Report):
        if value is not None and value not in self.shape_names:
            r.errors.append(f"{where}: unknown shape '{value}'")

    def skill(self, skill, where: str, r: Report):
        if self.data and isinstance(skill, int) and skill > 0 and skill not in self.data.skills:
            r.errors.append(f"{where}: skill {skill} is not in world-npcs.json npc_skills or skill-areas.json")

    def difficulty(self, name, where: str, r: Report) -> str | None:
        d = DIFFICULTIES.get(str(name).strip().lower())
        if d is None:
            r.errors.append(f"{where}: unknown difficulty '{name}' (Easy/Exploration, Normal/Conquest, Hard)")
        return d

    # ---- checks ----------------------------------------------------------------------------------------------------
    def trigger(self, t, where: str, r: Report, vars_: set[str]):
        if not isinstance(t, dict) or "type" not in t:
            r.errors.append(f"{where}: 'when' needs a type")
            return
        kind = t["type"]
        if kind not in self.trigger_types:
            r.errors.append(f"{where}: unknown trigger '{kind}'")
            return
        for options in TRIGGER_NEEDS.get(kind, []):
            if not any(t.get(o) is not None for o in options):
                r.errors.append(f"{where}: trigger {kind} needs '{' or '.join(options)}'")
        if t.get("from") not in (None, "fight", "phase"):
            r.errors.append(f"{where}: timer 'from' must be fight or phase")
        self.anchor(t.get("at"), where, r)
        if isinstance(t.get("area"), dict):
            self.shape(t["area"].get("shape"), where, r)
        for n in TRIGGER_NUMS:
            self.num(t.get(n), where, n, r, vars_)
        if kind == "npc_killed" and self.data and isinstance(t.get("npc"), int) and t["npc"] not in self.data.npcs:
            r.errors.append(f"{where}: npc {t['npc']} is not in world-npcs.json templates")

    def actions(self, actions, where: str, r: Report, vars_: set[str]):
        for i, a in enumerate(actions or []):
            at = f"{where}[{i}:{a.get('id') or a.get('type')}]" if isinstance(a, dict) else f"{where}[{i}]"
            if not isinstance(a, dict) or "type" not in a:
                r.errors.append(f"{at}: an action needs a type")
                continue
            kind = a["type"]
            if kind not in self.action_types:
                r.errors.append(f"{at}: unknown action '{kind}'")
                continue
            for options in ACTION_NEEDS.get(kind, []):
                if not any(present(a.get(o)) for o in options):
                    r.errors.append(f"{at}: {kind} needs '{' or '.join(options)}'")
            if kind == "displace" and a.get("mode") not in DISPLACE_MODES:
                r.errors.append(f"{at}: displace mode must be one of {sorted(DISPLACE_MODES)}")
            if kind == "modify" and a.get("mode") not in (None, *MODIFY_MODES):
                r.errors.append(f"{at}: modify mode must be phase, mul or set")
            if kind == "env" and a.get("op") not in (None, "spawn", "despawn"):
                r.errors.append(f"{at}: env op must be spawn or despawn")
            if a.get("ends") not in (None, "cast_land"):
                r.errors.append(f"{at}: ends must be 'cast_land'")
            if a.get("offset_space") not in (None, "world", "facing"):
                r.errors.append(f"{at}: offset_space must be world or facing")
            if a.get("count_mode") not in (None, "consume", "per_player", "per_tick"):
                r.errors.append(f"{at}: count_mode must be consume, per_player or per_tick")
            if kind == "shield" and a.get("until") not in (None, "stagger") and not str(a.get("until")).startswith("adds_dead:"):
                r.errors.append(f"{at}: shield until must be 'stagger' or 'adds_dead:<group>'")
            if a.get("ends") == "cast_land" and "/during" not in at and "/on_" not in at and kind in ("zone", "repeat", "decal"):
                r.warnings.append(f"{at}: ends 'cast_land' only means something inside a cast's during/on_land")
            self.shape(a.get("shape"), at, r)
            for step in a.get("steps") or []:
                self.shape(step.get("shape"), at, r)
                for n in ("radius", "inner", "angle", "length", "width"):
                    self.num(step.get(n), at, f"steps.{n}", r, vars_)
            for name in ("at", "toward", "to", "target"):
                self.anchor(a.get(name), at, r)
            for n in NUM_FIELDS:
                self.num(a.get(n), at, n, r, vars_)
            for offset in a.get("offsets") or []:
                if not isinstance(offset, list) or len(offset) != 2:
                    r.errors.append(f"{at}: each offset is [x, y]")
                    continue
                for n in offset:
                    self.num(n, at, "offsets", r, vars_)
            if isinstance(a.get("damage"), dict):
                d = a["damage"]
                for key in ("factor", "fixed"):
                    if key in d and (not isinstance(d[key], list) or len(d[key]) != 2):
                        r.errors.append(f"{at}: damage {key} must be [min, max]")
                    for n in d.get(key) or []:
                        self.num(n, at, f"damage.{key}", r, vars_)
                self.num(d.get("scale"), at, "damage.scale", r, vars_)
                self.skill(d.get("skill"), f"{at}/damage", r)
            if isinstance(a.get("grid"), dict):
                for n in ("arena_half_extent", "cell_size", "wall_half_thickness", "step_interval_ms"):
                    self.num(a["grid"].get(n), at, f"grid.{n}", r, vars_)
            self.skill(a.get("skill"), at, r)
            if self.data:
                if kind == "summon" and isinstance(a.get("npc"), int) and a["npc"] != 0 and a["npc"] not in self.data.npcs:
                    r.errors.append(f"{at}: npc {a['npc']} is not in world-npcs.json templates")
                if isinstance(a.get("env_obj"), int) and a["env_obj"] not in self.data.env:
                    r.errors.append(f"{at}: env_obj {a['env_obj']} is not in world-objects.json")
                if kind == "caption" and a.get("talk") and a["talk"] not in self.data.talks:
                    r.errors.append(f"{at}: talk '{a['talk']}' is not in npc-talks.json")
                if kind == "summon" and a.get("wave") and a["wave"] not in self.data.waves:
                    r.warnings.append(f"{at}: wave '{a['wave']}' is not a boss-phases.json wave or map-events.json event name")
            if kind in ("cover", "shield", "orb") and not a.get("id"):
                r.warnings.append(f"{at}: give {kind} an id so rules can refer to it")
            for nested in NESTED:
                self.actions(a.get(nested), f"{at}/{nested}", r, vars_)
            for choice in a.get("choices") or []:
                self.num(choice.get("weight"), at, "choices.weight", r, vars_)
                self.actions(choice.get("do"), f"{at}/choices", r, vars_)
            for part in a.get("parts") or []:
                if not part.get("id"):
                    r.errors.append(f"{at}: every part needs an id")
                self.num(part.get("hp"), at, "parts.hp", r, vars_)
                self.actions(part.get("on_break"), f"{at}/parts/{part.get('id')}", r, vars_)

    def rules(self, rules, where: str, r: Report, vars_: set[str], ids: set[str], unique: bool):
        for rule in rules or []:
            rid = rule.get("id")
            if not rid:
                r.errors.append(f"{where}: every rule needs an id")
                continue
            if rid in ids and unique:
                r.errors.append(f"{where}: duplicate rule id '{rid}'")
            ids.add(rid)
            at = f"{where}/{rid}"
            self.trigger(rule.get("when"), at, r, vars_)
            diff = rule.get("difficulty")
            for d in [diff] if isinstance(diff, str) else diff or []:
                self.difficulty(d, at, r)
            self.num(rule.get("chance"), at, "chance", r, vars_)
            self.actions(rule.get("do"), at, r, vars_)

    def encounter(self, enc: dict, file: str) -> Report:
        r = Report(file)
        if not isinstance(enc, dict):
            r.errors.append("an encounter file is one JSON object")
            return r
        allowed = {"$schema", "id", "display", "display_ru", "dungeon", "source", "note", "npcs", "arena", "vars", "start_phase",
                   "opening_ms", "rotation", "rules", "difficulty"}
        for key in set(enc) - allowed:
            r.errors.append(f"unknown top-level field '{key}'")
        if not enc.get("id"):
            r.errors.append("missing id")
        npcs = enc.get("npcs") or {}
        if not npcs:
            r.errors.append("npcs lists no NpcData ids")
        for name, ids in npcs.items():
            self.difficulty(name, "npcs", r)
            for npc in ids:
                if self.data and npc not in self.data.npcs:
                    r.errors.append(f"npcs/{name}: {npc} is not in world-npcs.json templates")
                elif self.data and npc not in self.data.scripted_npcs:
                    r.warnings.append(f"npcs/{name}: {npc} has no boss-phases.json script (dungeon name comes from the encounter)")
        modes = enc.get("difficulty") or {}
        vars_ = set(enc.get("vars") or {})
        for mode in modes.values():
            vars_ |= set(mode.get("vars") or {})
        self.num(enc.get("opening_ms"), "opening_ms", "opening_ms", r, vars_)
        if isinstance(enc.get("arena"), dict):
            self.anchor(enc["arena"].get("centre"), "arena", r)
        self.actions(enc.get("rotation"), "rotation", r, vars_)
        for a in enc.get("rotation") or []:
            if a.get("type") != "cast":
                r.errors.append(f"rotation: only cast actions may rotate (got '{a.get('type')}')")
        ids: set[str] = set()
        self.rules(enc.get("rules"), "rules", r, vars_, ids, unique=True)
        rule_ids = {rule.get("id") for rule in enc.get("rules") or []}
        for name, mode in modes.items():
            self.difficulty(name, "difficulty", r)
            where = f"difficulty/{name}"
            self.rules(mode.get("rules_add"), f"{where}/rules_add", r, vars_, set(ids), unique=False)
            self.actions(mode.get("rotation"), f"{where}/rotation", r, vars_)
            removed = mode.get("rules_remove") or []
            for rid in [removed] if isinstance(removed, str) else removed:
                if rid not in rule_ids:
                    r.errors.append(f"{where}: rules_remove names unknown rule '{rid}'")
            for src, dst in (mode.get("skill_remap") or {}).items():
                if not str(src).isdigit():
                    r.errors.append(f"{where}: skill_remap key '{src}' is not a skill id")
                else:
                    self.skill(int(src), f"{where}/skill_remap", r)
                self.skill(dst, f"{where}/skill_remap", r)
        return r


def validate_all(paths: list[Path] | None = None, data: ClientData | None = None, client_data: bool = True) -> list[Report]:
    if client_data and data is None:
        data = ClientData.load()
    validator = Validator(data if client_data else None)
    reports = []
    seen_npcs: dict[int, str] = {}
    seen_ids: dict[str, str] = {}
    for path in paths or sorted(ENCOUNTERS.glob("*.json")):
        name = path.name
        try:
            enc = json.loads(path.read_text(encoding="utf-8"))
        except json.JSONDecodeError as e:
            reports.append(Report(name, [f"JSON: {e}"]))
            continue
        report = validator.encounter(enc, name)
        if isinstance(enc, dict):
            if enc.get("id") in seen_ids:
                report.errors.append(f"duplicate encounter id '{enc['id']}' (also {seen_ids[enc['id']]})")
            seen_ids.setdefault(enc.get("id"), name)
            for ids in (enc.get("npcs") or {}).values():
                for npc in ids:
                    if npc in seen_npcs and seen_npcs[npc] != name:
                        report.errors.append(f"npc {npc} already belongs to {seen_npcs[npc]}")
                    seen_npcs.setdefault(npc, name)
        reports.append(report)
    return reports


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("files", nargs="*", type=Path)
    parser.add_argument("--no-client-data", action="store_true", help="structural checks only")
    args = parser.parse_args(argv)
    reports = validate_all(args.files or None, client_data=not args.no_client_data)
    failed = 0
    for report in reports:
        status = "FAIL" if report.errors else "ok"
        print(f"{status:4} {report.file} ({len(report.errors)} errors, {len(report.warnings)} warnings)")
        for e in report.errors:
            print(f"     error: {e}")
        for w in report.warnings:
            print(f"     warning: {w}")
        failed += bool(report.errors)
    print(f"{len(reports)} encounters, {failed} with errors")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
