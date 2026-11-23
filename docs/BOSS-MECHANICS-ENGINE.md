# Boss mechanics engine

Every boss fight is data: one JSON encounter file per boss family under `tools/data/boss-mechanics/`, made of **rules**
(a trigger and a list of primitive **actions**). The server runs them with one generic engine; no boss needs its own C#.

- Files: `tools/data/boss-mechanics/*.json`, schema `tools/data/boss-mechanics.schema.json`.
- Validator: `python tools/boss_mechanics_validate.py` (structure and every client id; exit 1 on errors).
- Engine: `server/Aion.World/BossEngine/` (model, catalog, run state) and `server/Aion.World/NpcAi.Encounters.cs` (execution).
- Switch: `Aion:Bosses:Engine` (default `true`). `false` runs the legacy hand-coded fights (see [Legacy fallback](#legacy-fallback)).
- Research notes used when authoring encounters are kept outside this tree (see [Mechanic patterns](#mechanic-patterns-to-primitives)).

Contents: [How it runs](#how-it-runs) · [File layout](#file-layout) · [Triggers](#triggers) · [Anchors](#anchors) ·
[Shapes](#shapes) · [Damage](#damage) · [Actions](#actions) · [Difficulty](#difficulty-overrides) ·
[Logging](#logging-and-debugging) · [Authoring workflow](#authoring-workflow) · [Examples](#examples) ·
[Migration status](#migration-status) · [Known gaps](#known-gaps-and-candidate-values)

## How it runs

1. A boss is engaged. If it is a summoned copy (`BossSummons.IsSummon`) it fights as an add and runs no encounter.
2. `BossEncounters.Resolve(npcId)` picks the encounter:
   - an **authored** file listing this NpcData id, merged for its difficulty, or
   - the boss's `boss-phases.json` script **compiled** into the same rules (rotation; one `hp_below` rule per phase with its
     multipliers, slam, notice and adds; the frenzy as an `enrage_timer`). Compiled fights behave exactly as before.
   - A script that names a hand-coded `mechanic` and has no authored file stays on the legacy path (none today).
3. Each AI tick (`NpcAi.BossTick`, and also while the boss is held or stunned) the engine evaluates triggers, drains events,
   runs due delayed work, ticks zones, shields, orbs, stagger windows, parts and debuffs.
4. **Casts** go through the existing boss slam pipeline (`NpcAi.Bosses`): skill casting start, ground marker
   (`docs/GROUND-TELEGRAPHS.md`), stagger break (`Held`), the landing with the skill's own client shape and Skill.dat damage
   (NPC damage formula), recovery. The engine adds `during` / `on_land` / `on_break` hooks and `cover`.
5. Everything else (telegraphs, zones, soaks, ...) is evaluated by the engine with the same NPC damage formula and reaches
   players through the tick's hit callback, like every other NPC hit. No skill-effect code is involved.
6. Summons, map waves and env objects are requested by the AI and created by the session (`GameSession.Bosses`,
   `GameSession.BossMechanics`), as before.

Distances are flat (2D, to the player's centre) unless the skill's client shape is used (`skill_area`, casts).

## File layout

```json
{
  "$schema": "../boss-mechanics.schema.json",
  "id": "fire_temple_kromede",
  "display": "Kromede's Desire", "display_ru": "Желание Кромеды",
  "dungeon": "Fire_Temple",
  "source": "where the numbers come from (client / official overlay / CANDIDATE)",
  "npcs": { "Easy": [2310371], "Normal": [2300371], "Hard": [2320371] },
  "arena": { "centre": "home" },
  "vars": { "wall_linger": 14000 },
  "start_phase": "p1",
  "opening_ms": 12500,
  "rotation": [ { "type": "cast", "skill": 1801080, "every_ms": 25000, "wind_up_ms": 2000, "warning": "..." } ],
  "rules": [ { "id": "fire_walls_75", "when": { "type": "hp_below", "pct": 75 }, "do": [ ... ] } ],
  "difficulty": { "Hard": { "vars": { "wall_linger": 12000 }, "skill_remap": { "1801185": 1801191 } } }
}
```

| Field | Meaning |
|---|---|
| `id` | Unique encounter id (logs). |
| `npcs` | NpcData ids per difficulty. `Easy` = Exploration, `Normal` = Conquest, `Hard` = Conquest Hard (those names work too). |
| `dungeon` | Family name; the `boss-phases.json` script's dungeon (with mode suffix) wins when the boss has one. |
| `arena.centre` | `home` (spawn, default), `boss` (where the pull happens) or a point. The `arena_centre` anchor. |
| `vars` | Named numbers; any numeric field can say `"$name"`. Difficulty `vars` override them. |
| `start_phase` | Phase set when the fight starts (fires `phase_enter`). |
| `opening_ms` | First rotation cast this long after the pull (default: half the first cast's `every_ms`). |
| `rotation` | Filler casts in order, each repeating on its `every_ms` (only `cast`). `set_rotation` replaces it. |
| `rules` | `{ id, when, do, phase?, difficulty?, once?, max_fires?, chance?, npc_alive?, note? }`. |
| `difficulty` | Per-difficulty overrides ([below](#difficulty-overrides)). |

A rule fires when its trigger matches and its filters pass: `phase` (only in these phases), `difficulty` (only on these),
`once` (default true for `hp_below`, `enrage_timer`, `npc_killed`, `fight_start`; false otherwise), `max_fires`, `chance` (0–1),
`npc_alive` (only while an NPC with this NpcData id is alive on the map: the Abyssal Forge stones that buff the party).
Unknown fields are errors (typos do not silently do nothing).

## Triggers

| `type` | Fields | Fires |
|---|---|---|
| `fight_start` | | Once, on the first engine tick of the fight. |
| `hp_below` | `pct` | When boss HP % ≤ `pct` (once by default). Several crossed in one tick fire highest first. |
| `hp_every` | `step`, `pct` (start, default 100) | Every `step` % lost below `pct` (once per step). |
| `timer` | `after_ms` and/or `every_ms`, `count`, `from` (`fight` / `phase`) | After `after_ms` (default `every_ms`), then every `every_ms`, `count` times. `from: phase` restarts on each phase change. |
| `enrage_timer` | `after_ms` | Like the legacy frenzy: `Aion:Bosses:FrenzySeconds` overrides it, `Aion:Bosses:Frenzy=false` and official frenzies disable it. |
| `phase_enter` / `phase_exit` | `phase` | When the `phase` action enters / leaves that phase. |
| `adds_dead` | `group` | When every add of a `summon` group is dead. |
| `add_died` | `group` | Each time one add of the group dies (anchor `event` = where it died). |
| `players_in_area` | `at`, `area` (shape), `min` / `max`, `hold_ms` | When the count of players in the area is within min..max for `hold_ms` (re-arms after it stops being true). |
| `shield_broken` | `shield` | When that shield breaks (not when it expires). |
| `stacks_reached` | `debuff`, `stacks` | When a player's stacks of that debuff reach `stacks` (anchor `event` = that player). |
| `npc_killed` | `npc` | When an NPC with that NpcData id on the map is dead (mid-boss gates). |
| `counter_reached` | `counter`, `value` | When a `counter` crosses `value` upward. |
| `stagger_broken` | | When the boss becomes staggered / held (gauge broken). |
| `part_broken` | `part` | When that breakable part breaks. |
| `event` | `event` | When an action emits it: every action with an `id` emits `<id>.success` / `<id>.fail`; `emit` sends any name. |

## Anchors

Where an action happens (`at`, `toward`, `to`, `target`):

| Anchor | Point(s) |
|---|---|
| `boss` | The boss (facing its victim). Default for most actions. |
| `victim` | The boss's current target (a player). |
| `each_player`, `random_player`, `nearest_player`, `farthest_player` | Players in the fight (by distance from the boss). |
| `arena_centre`, `home` | The arena centre / the boss's spawn point. |
| `cast_centre` | Inside a cast's `during` / `on_land` / `on_break`: where the cast is placed (on the victim for on-target or ground skills, else the boss). |
| `event` | The point (and player) of the event that fired the rule (`add_died`, `stacks_reached`, `on_hit`, ...). |
| `event_player` | The player the running follow-up belongs to (`on_hit`, `on_touch`, a `debuff` `on_max`), at their **live** position, also inside a `delay` (acid pools, lava marks: where the player stands when the delay ends). |
| `mark:<name>` | The player a `mark` action named. |
| `group:<name>` | Each living add of a summon group (facing as the add faces). |
| `{ "x": .., "y": .., "z": .. }` | A world point (z 0 = the boss's height). |

Placement modifiers on any placing action: `toward` + `distance` (move the anchor that far toward another anchor; without
`toward`, `distance` moves forward along the facing), `offsets: [[x, y], ...]` (one copy per offset; `offset_space`
`world`, default, or `facing` = [forward, right]). An **inverted** shape (`invert`, `safe_zone`) with several offsets is a set of safe
spots: it hits only players outside **every** copy (rocks, shields, white-light circles). `soak` / `stack` with several offsets succeed when
enough players stand in **any** of the circles (pillar baits).

## Shapes

`shape` on `telegraph`, `zone`, trigger `area` and each of `steps`:

| Shape | Fields | Inside |
|---|---|---|
| `circle` | `radius` | Within `radius`. |
| `donut` | `inner`, `radius` | Between `inner` and `radius` (rings). |
| `cone` | `radius`, `angle` (degrees, full width, default 90) | Within radius and the facing arc. |
| `rect` | `length`, `width` | A strip starting at the anchor going forward (charges, beams). |
| `line` | `length`, `width` | A strip centred on the anchor along its facing (clone lines, walls). |
| `grid` | `grid` (below) | Moving wall grid (zones only). |
| `safe_zone` | `radius` | Everyone **outside** the circle (same as `circle` + `invert`). |
| `skill_area` | `skill` | The skill's client shape from `skill-areas.json` (default when only `skill` is given). |

`invert: true` flips any shape (hit outside). `steps: [shape, shape, ...]` changes the shape on each repeat
(`repeat` iteration or `telegraph` `count`), e.g. expanding rings.

`grid`: `arena_half_extent` 5000, `cell_size` 1400, `wall_half_thickness` 550, `rows` 7, `cols` 7, `step_interval_ms` 2500,
`gap_patterns` (open columns per step, cycled), `pattern_offset` (start further into the cycle). Defaults are Kromede's.

## Damage

`damage` on any hurting action:

| Field | Meaning |
|---|---|
| `skill` | The skill's Skill.dat damage row (world-npcs.json `npc_skills`) through the NPC damage formula (level, grade, phase/enrage multipliers, `Aion:Bosses:DamageScale`). Remapped by `skill_remap`. |
| `factor: [min, max]` | Same formula with an authored row (1/10000 of the NPC fixing damage) when the client has none. |
| `fixed: [min, max]` | Flat damage (DoTs, puddles). Not scaled, no defense. |
| `scale` | Multiplier on the result. Difficulty `damage_scale` multiplies too. |
| `hp_ratio` | A share of the victim's maximum HP in 1/10000 (the client Damage row calc type MaxHPRatio: 10000 = all of it, 50000 = lethal), through `SkillEffectEngine.NpcRatioDamage` (`AION_NPC_RATIO_DAMAGE`); with the flag off the other fields apply, so give a `factor` fallback. `scale` and `damage_scale` multiply it. With `split` (`stack`, `soak`) the share is divided between the players in it: `hp_ratio: 15000` split reproduces the client's 150 / 75 / 50 / 30% rows for 1 / 2 / 3 / 5 players. |
| `ratio: true` | With `skill`: the skill's client HP-ratio row (`MaxHPRatio` / `CurrentHPRatio`, `skill-effect-engine.json` `ratio_damage`) of the victim's HP, when the skill has one (a 70% orb, a lethal 500% line). Opt-in: casts and telegraphs otherwise keep the Skill.dat damage row. Complements `hp_ratio`: `hp_ratio` is an authored share, `ratio` reads the skill's own client row (also for `cast`, where the slam pipeline applies it). |
| `lethal: true` | Kills (wipes). |
| `ignore_defense: true` | Skip the victim's defense reduction. |

Casts use their skill's own damage row (`damage.scale` multiplies it). A skill with no row deals 1 (see gaps).

## Actions

Every action is `{ "type": ..., "id"?: ..., "note"?: ..., fields }`. An `id` names it in logs and makes it emit
`<id>.success` / `<id>.fail`. Nested lists (`during`, `on_land`, `on_break`, `on_hit`, `on_success`, `on_fail`, `on_expire`,
`on_max`, `on_complete`, `on_touch`, `do`) take more actions.

### Casting and ground hits

| Action | Fields | Does |
|---|---|---|
| `cast` | `skill`, `wind_up_ms`, `every_ms`, `radius`, `damage.scale`, `warning`/`warning_ru`, `once`, `stagger_check`, `land`, `cover`, `during`, `on_land`, `on_break` | The boss casts a real skill through the slam pipeline. From a rule it is once-only by default and goes next (a filler being wound up is broken for it; a once-only one keeps going and this waits behind it). `every_ms` without `once` replaces the filler. `land: false` deals no damage itself (mechanic-only casts: run `on_land`). `stagger_check: true` cancels the cast if the boss is staggered / held during the wind-up (`on_break`, and `<id>.success`). `cover: true` spares players behind an active `cover`. The client wind-up (Skill.dat cast time) wins over `wind_up_ms`. |
| `telegraph` | shape fields or `steps`, `skill`, `at`, `delay_ms`, `damage`, `count`, `interval_ms`, `decal`, `on_hit`, `on_success`, `on_fail` | A ground marker (the skill's decal at the exact point, `decal: false` for none) that resolves after `delay_ms`: hits everyone inside. `count`/`interval_ms` place it again, re-aimed. Success = nobody hit. |
| `zone` | shape fields or `grid`, `at`, `offsets`, `skill`, `damage`, `tick_ms` (1000), `first_tick_ms`, `duration_ms` or `ends: cast_land` + `linger_ms`, `blocks_movement`, `env_obj`, `decal`, `on_hit` | A lasting hazard: ticks damage on players inside. `blocks_movement` refuses player moves into it (walls). Without a duration it lasts the fight. |
| `decal` | `skill`, `at`, `duration_ms` or `ends: cast_land` + `linger_ms` | Only the ground marker. |

### Party checks

| Action | Fields | Does |
|---|---|---|
| `soak` | `at` (default boss), `radius`, `min_players`, `damage`, `split`, `delay_ms`, `skill`, `decal`, `on_hit`, `on_success`, `on_fail` | Players within `radius` take the hit (`split: true` shares one roll); fewer than `min_players` (default 1) fails. Towers. |
| `stack` | as soak; `at` defaults to `victim`, `split` to true | Stack-to-share on a player (usually `mark:<name>`). |
| `spread` | `at` (default `each_player`), `radius`, `damage`, `overlap_scale` (1), `delay_ms` | A circle on each target; a player under n circles takes 1 + `overlap_scale`×(n−1) hits' worth. Any overlap fails. |
| `cover` | `id`, `at`, `toward`, `distance`, `length` (depth, 900), `width` (1400), `duration_ms`, `env_obj`, `skill` (decal) | Line-of-sight cover (pillars): players behind it, seen from the boss, within depth and half width, are spared by `cover: true` casts. Re-placing an `id` moves it. |
| `stagger_check` | `window_ms`, `warning`, `on_success`, `on_fail` | The party must stagger the boss (break its gauge / hold it) within the window. For a cast that must be broken, prefer `cast` with `stagger_check: true`. |
| `orb` | `id`, `at`, `offsets`, `radius`, `required`, `count_mode` (`consume` default, `per_player`, `per_tick`), `window_ms`, `env_obj`, `skill`, `on_touch`, `on_complete`, `on_fail` | Interaction objects: touches count toward `required` within the window. |

### Movement

| Action | Fields | Does |
|---|---|---|
| `displace` | `mode` (`knockback`, `pull`, `to_radius`, `to_point`), `at`, `target` (default `each_player`), `distance`, `radius` | Moves players (server-side teleport): away from / toward the anchor by `distance` (pull with no distance = onto it), onto the circle `radius` around it, or onto it. |
| `move` | `to` | The boss jumps to a point and faces its victim. |

### Adds, shields, parts

| Action | Fields | Does |
|---|---|---|
| `summon` | `npc` (0 = the boss itself), `count`, `radius`, `at`, `group`, `target`; or `wave` | Spawns adds around a point (default the boss), sent at the target (default the victim), kept in `group` (for `adds_dead`, `add_died`, `group:` anchors, shields). `wave` starts a map wave by name (boss-phases.json `waves` or a MapEvent). |
| `despawn_group` | `group` | Removes a group's living adds. |
| `shield` | `id`, `invuln`, `hp` or `hp_pct`, `until` (`adds_dead:<group>`, `stagger`), `duration_ms`, `on_break`, `on_expire` | `invuln`: players' hits do nothing. `hp`: a pool that absorbs damage (the boss's HP is held). Breaks when emptied or `until` is met (fires `shield_broken`); expiring runs `on_expire`. |
| `parts` | `parts: [{ id, hp, client_part_id, on_break }]` or `from_client: true` (MonsterPartsData) | Breakable parts: the boss's HP loss is credited to the first unbroken part; breaking runs `on_break` and fires `part_broken`. |

### Debuffs and threat

| Action | Fields | Does |
|---|---|---|
| `debuff` | `debuff`, `target`, `stacks` (1), `max`, `duration_ms`, `abnormal`, `on_max` | Engine-tracked stacks on players (fires `stacks_reached`). `abnormal` applies a client abnormal to the player through `NpcAi.MechanicDebuffHook`, which `GameSession` wires to `EngineApplyAbnormal` (stacking rules, immunities, client packet) for abnormal ids in the ported skill tables. `max: 1` makes a once-per-player latch for `on_max`. |
| `cleanse` | `debuff`, `target` (default everyone) | Clears stacks. |
| `tank_swap` | `debuff`, `max` (2), `duration_ms` | The victim gains a stack; at `max` the boss turns to the top-threat present player without the debuff. |
| `mark` | `name`, `target` (default victim), `text` | Remembers a player for `mark:<name>` (with an optional notice). |

### Fight state

| Action | Fields | Does |
|---|---|---|
| `phase` | `phase` | Changes phase (`phase_exit` / `phase_enter`, phase timers restart). |
| `modify` | `damage_mul`, `haste_mul`, `mode` (`phase` default: never weaker, enrage kept; `mul`; `set`) | The boss's damage and attack-speed multipliers. |
| `enrage` | `damage_mul`, `haste_mul`, `text`, `hard`, `every_ms`, `damage` | Soft enrage (multipliers for good). `hard: true` also hits everyone (lethal by default) every `every_ms`. |
| `wipe` | `text`, `target`, `damage` | Kills (lethal) the targets (default everyone). Failed checks. |
| `set_rotation` | `rotation` | Replaces the filler casts. |
| `notice` | `text`, `text_ru`, `size` (22), `color` (FFB040) | On-screen notice. |
| `caption` | `talk` | A client NPC talk line (npc-talks.json key). |
| `env` | `env_obj`, `at`, `op` (`spawn` / `despawn`), `slot`, `duration_ms` | Spawns / removes an env object (world-objects.json id). Engine env objects despawn when the fight ends. |

### Flow

| Action | Fields | Does |
|---|---|---|
| `delay` | `ms`, `do` | Runs `do` later. |
| `repeat` | `every_ms`, `do`, `delay_ms` (first, default `every_ms`), `count` / `duration_ms` / `ends: cast_land` + `linger_ms` | Runs `do` repeatedly (iteration number drives `steps`). |
| `random` | `choices: [{ weight, do }]` | Runs one choice. |
| `counter` | `counter`, `add` / `set` | Counters (fires `counter_reached`). |
| `emit` | `event` | Raises an `event`. |

## Difficulty overrides

```json
"difficulty": {
  "Easy": { "note": "Exploration: no enrage", "rules_remove": ["frenzy"] },
  "Hard": {
    "vars": { "frenzy_ms": 240000 },
    "damage_scale": 1.2,
    "time_scale": 0.85,
    "skill_remap": { "1801185": 1801191 },
    "rules_add": [ { "id": "hard_only", "when": { "type": "hp_below", "pct": 60 }, "do": [ ... ] } ],
    "rotation": [ ... ]
  }
}
```

`vars` override the encounter's; `damage_scale` multiplies all engine damage; `time_scale` multiplies every delay, duration,
interval and timer; `skill_remap` swaps skill ids everywhere (casts, telegraphs, damage rows); `rules_add` / `rules_remove`
change the rule list; `rotation` replaces the filler. Rules can also carry `"difficulty": ["Normal", "Hard"]`.

## Logging and debugging

- Every rule fire and action result goes to the fight's log (`NpcAi.EncounterOf(boss).Log`: time, rule, action, targets
  reached, result such as `success (3/2)`, `landed, 2 hit`, `fail (1/3)`) and to PlaytestDebug as `boss_mechanic`
  (`boss`, `encounter`, `difficulty`, `rule`, `action`, `id`, `targets`, `result`) when `Aion:Debug` actions are on.
- A file that fails to load is skipped (the boss falls back to its compiled script, or to the legacy path when that script
  names a hand-coded mechanic) and reported at startup
  (`Boss encounter <file> rejected: ...`) and to PlaytestDebug `boss_mechanic_load`.
- An action that throws at run time logs `error: ...` and the fight goes on.

## Authoring workflow

1. Find the boss's NpcData ids (Easy 231xxxx, Normal 230xxxx, Hard 232xxxx) and its client skills (`world-npcs.json`
   `npc_skills`, `skill-areas.json`); read the research doc for the mechanic.
2. Start from the boss's `boss-phases.json` rotation and phases (thresholds, warnings, notices); add the primitives.
3. Put tunable numbers in `vars`, so Hard can override them; mark guesses as CANDIDATE in `source` / `note`.
4. `python tools/boss_mechanics_validate.py` until it says `0 with errors`.
5. Rebuild: the files are embedded into `Aion.World` (`boss-mechanics/<file>`). Add a test with the `BossEngineTests.Rig`
   harness (a synthetic encounter) or run the real one like `BossEngineMigrationTests`.
6. `python tools/boss_coverage.py` shows each boss's engine path (`authored`, `compiled`, `legacy`).

## Examples

### 1. Groggy race wipe (Berk Extinction, Bakarma Wave of Death)

```json
{ "id": "extinction_50", "when": { "type": "hp_below", "pct": 50 }, "do": [
  { "type": "notice", "text": "Break the stagger gauge before the cast ends!" },
  { "type": "cast", "id": "extinction", "skill": 1800140, "wind_up_ms": 8000, "stagger_check": true,
    "warning": "Extinction for Eternity — break the stagger gauge!",
    "on_land": [ { "type": "wipe", "text": "Too slow." } ] } ] }
```

The cast lands (and wipes) unless the party staggers the boss during the wind-up; then `on_break` runs and
`extinction.success` fires.

### 2. Alternating stack and spread on a marked player

```json
{ "id": "share_or_scatter", "when": { "type": "timer", "after_ms": 20000, "every_ms": 30000 }, "phase": "p2", "do": [
  { "type": "mark", "name": "chosen", "target": "random_player", "text": "The boss marks a player!" },
  { "type": "random", "choices": [
    { "do": [ { "type": "decal", "skill": 1601030, "at": "mark:chosen", "duration_ms": 6000 },
              { "type": "stack", "at": "mark:chosen", "radius": 500, "delay_ms": 6000, "min_players": 3,
                "damage": { "factor": [60000, 60000] }, "on_fail": [ { "type": "debuff", "debuff": "weakened", "target": "each_player", "duration_ms": 20000 } ] } ] },
    { "do": [ { "type": "spread", "radius": 450, "delay_ms": 6000, "damage": { "factor": [25000, 30000] }, "overlap_scale": 1.5 } ] } ] } ] }
```

### 3. Adds behind a shield, then a soft and hard enrage

```json
"rules": [
  { "id": "guards", "when": { "type": "hp_below", "pct": 60 }, "do": [
    { "type": "summon", "npc": 2300910, "count": 4, "radius": 1500, "group": "guards", "at": "arena_centre" },
    { "type": "shield", "id": "ward", "invuln": true, "until": "adds_dead:guards", "duration_ms": 60000,
      "on_expire": [ { "type": "wipe", "text": "The ward holds; the guards overwhelm you." } ] },
    { "type": "env", "env_obj": 1400623, "at": "boss", "slot": 1 } ] },
  { "id": "ward_down", "when": { "type": "shield_broken", "shield": "ward" }, "do": [
    { "type": "env", "env_obj": 1400623, "op": "despawn", "slot": 1 },
    { "type": "phase", "phase": "burn" } ] },
  { "id": "soft", "when": { "type": "enrage_timer", "after_ms": "$soft_ms" }, "do": [ { "type": "enrage", "damage_mul": 1.5, "text": "Enraged!" } ] },
  { "id": "hard", "when": { "type": "timer", "after_ms": "$hard_ms" }, "do": [ { "type": "enrage", "hard": true, "every_ms": 3000 } ] }
]
```

The five migrated finals (`tools/data/boss-mechanics/berk.json`, `auldor.json`, `kromede.json`, `bakarma.json`,
`nuakum.json`) are full worked examples: pillar cover, marked stack, wall grid with `blocks_movement`, clone lines on a
`group:` anchor, edge snap + ring pulses with `steps`, whirlpool zones with `offsets`, an orb.

## Mechanic patterns to primitives

From "Patterns that repeat across the series" in the research doc:

| Pattern | Primitives |
|---|---|
| Groggy race wipe | `cast` with `stagger_check` (+ `on_land: wipe`), or `stagger_check` with `on_fail` |
| Line-of-sight hide | `cover` + `cast` with `cover: true` |
| Jump / glide checks | `repeat` + `telegraph` with `steps` of `donut` (inverted for safe bands), `displace` |
| Assignment checks (colours, numbers, markers) | `mark`, `debuff` with `abnormal`, `orb` per colour, `counter`, `random` |
| Stack versus spread | `stack` on `mark:<name>`, `spread`, `random` |
| Damage-stop windows | `shield` (`invuln`, `hp`), `phase`, `players_in_area` |
| Hard: tighter timers, enrage | `difficulty` `time_scale`, `vars`, `enrage_timer` + `enrage` (`hard`) |

## Migration status

| Fight | Status |
|---|---|
| 111 generic OURS scripts (`boss-phases.json` fury / call / enrage) | Engine, **compiled** from boss-phases.json; same phases, slams and frenzy (regression: `BossEngineMigrationTests`). |
| Berk (Krao Cave) 2300171 / 2310171 / 2320171 | **Authored** `berk.json`: Wave of Fury pillar `cover` (75%, 25%), Extinction stagger check (50%), frenzy. |
| Auldor (Urugugu Canyon) 2300218 / 2310218 / 2320218 | **Authored** `auldor.json`: Tempest Strike `mark` + `stack` (75%, 30%), outer-arena `zone` below 30%, Easy skill remap. |
| Kromede (Fire Temple) 2300371 / 2310371 / 2320371 | **Authored** `kromede.json`: fire-wall `grid` zone (75%, 25% with pattern offset), clones `summon` + clone-line `telegraph` timer, Hard remap and wind-up. |
| Bakarma (Draupnir) 2300471 / 2310471 / 2320471 | **Authored** `bakarma.json`: Wave of Death stagger cast with edge `displace` and ring `repeat` (70%, 30%), whirlpool `zone` below 30%. |
| Nuakum (Horn Cave) 2300910 / 2310910 / 2320910 | **Authored** `nuakum.json`: hunt clone `summon` (75%, 55%), Blue Light `orb` (30%). |
| Dying Dramata's Nest: Nayatman 2300507 / 2310507 / 2300153, Nazium 2300508 / 2310508 / 2300154, Durbati 2300509 / 2310509 / 2300155 | **Authored** `ddn_*.json`: wisps, spears, homing blades as chasing telegraphs, Durbati Poison Rain on every player, Venom Swamp rock check on the dungeon's own pillars, fear roar, enrage. |
| Cradle of Nihility: Beltiras 2301302 / 2311302 / 2321302, Balkan 2301303 / 2311303 / 2321303, Bakarma 2301301 / 2311301 / 2321301 | **Authored** `cradle_*.json`: Nebula Collapse orbs (80%, 20%), Supernova shields (40%), coloured pillar charge (80%, 20%), Bakarma pizza cast pair, line share, area restriction, enrage. |
| Hall of Illusion: Glassbane 2301401 / 2311401 / 2321401, Nasarak 2301402 / 2311402 / 2321402, Nathara 2301403 / 2311403 / 2321403 | **Authored** `hall_*.json`: ghost eyes / white-light check, Annihilation Ring, Nathara laser, shard prisms, Split Illusion (boss copies + 30 s ward), enrage. |
| Azure Breath Island: Baumnute 2300189 / 2300219 / 2300244, Xyrepe 2300190 / 2300230 / 2300245, Alturadon 2300188 / 2300200 / 2300243 | **Authored** `azure_*.json`: Veil at the official 8 m, lightning rain, mushroom pools, Alturadon Blue Breath (10 stacks wipe), Blue Beam from 50%, enrage. |
| Citadel of the Fallen Daeva: Turgen 2301601 / 2301701 / 2301721, Griosa 2301602 / 2301702 / 2301722, Basilus 2301603 / 2301703 / 2301723 | **Authored** `citadel_*.json`: sword fields, Commence Hunt shared hit, Basilus Field of Judgment golden circles (two explosions wipe), enrage. |
| Corrupted Deus Research Base | **Blocked**: the pinned client export has no NpcData, spawns or dungeon entry for it (Gromkas, Zylarus, Atiel), so there is no id to attach an encounter to. Authoring it needs a newer export (the CC-gauge Earth Collapse wipe maps to `cast` + `stagger_check`, the falling floor to `displace`). |
| Odium / Eternal Gartua 2400800 / 2101074 (field boss) | **Authored** `field_gartua.json`: 4-skill rotation, the 15 s Odyle Absorption stagger check (`on_break`: the cast is stopped; `on_land`: +20% damage for 30 s). Intervals GUESS. |
| Turncoat Ducal 2600521 (Middle Reshanta) | **Authored** `field_ducal.json`: Smite Ground and Greatsword Plunge from the client casts. Intervals GUESS. |
| Abyss rift grade 6 bosses 2670066 / 067 / 113-118 | **Authored** `abyss_rift_wh6.json`: invulnerable for 25 s at 66% and 33% HP (OFFICIAL: 8 bosses, 25 s, at most twice; the mapping to these 8 NPCs and the thresholds are GUESSES). |
| Rahu / Ketu / Ludra (Abyssal Forge raid) | **Authored** `ludra_*.json`, see [LUDRA-ARKANIS.md](LUDRA-ARKANIS.md). |
| Siliator / Ruthilis / Talisra (Shattered Arkanis) | **Authored** `arkanis_*.json`, see [LUDRA-ARKANIS.md](LUDRA-ARKANIS.md). |
| Krao Cave mid-bosses 2300104 (Gadioton) / 2300108 (Harcon), +10000 Easy / +20000 Hard | **Authored** `krao_gadioton.json`, `krao_harcon.json`: client rotation, generic fury kept, Harcon's sniper adds at 60%. |
| Urugugu Canyon mid-bosses 2300206 (Raur) / 2300207 (Urahum) | **Authored** `urugugu_raur.json` (reflect pool `shield` at 70% / 30%, Easy remap), `urugugu_urahum.json` (acid mark `debuff` max 1 + `event_player` puddle `zone`, triple slam `steps`). |
| Fire Temple mid-bosses 2300310 (Rotan) / 2300311 (Ignus) / 2300312 (Murute) | **Authored** `fire_rotan.json`, `fire_ignus.json` (lava marks 75%, Flame Rush `spread` 65%), `fire_murute.json` (Lava Lift follow-up circles, ring `steps` below 50%). Kromede Hard: clones from 75% (`rules_remove` / `rules_add`). |
| Draupnir mid-bosses 2300401 (Lakshmi) / 2300403 (Sarasvati) | **Authored** `draupnir_lakshmi.json` (reflect pool every 40 s, healing adds 50%), `draupnir_sarasvati.json` (poison puddle `zone`, root `debuff`). |
| Vakron Sky Island 2300810 (Tie) / 2300811 (Tamon) / 2300812 (Vakron) | **Authored** `vakron_tie.json` (map poison + spits 75% / 25%), `vakron_tamon.json` (colour `shield`, petrify circles), `vakron_final.json` (generic calls / fury / enrage kept; Thorn Prison, Surging Thorn 60% / 40%, stone prison 55% / 35%, frenzy). |
| Ferocious Horn Den 2300908 (Kuhapu) / 2300909 (Duanka) | **Authored** `horn_kuhapu.json` (Thunder Cry: invuln `shield` + `orb`s, Easy 1 orb), `horn_duanka.json` (Necromancy spirits + `adds_dead` shield, locked share `soak`, Soul Calling, personal `spread`). Berk: Whirlwind and rock eruptions added. |

Deliberate differences from the hand-coded fights (all small; tests pin the rest):

- Kromede's clone lines run only in Kromede's fight and from **each living clone** (the legacy code fired them for any boss
  fight with adds and only from the first clone, alive or not). Their damage uses the boss's stats, not the clone's.
- Thresholds crossed in one tick **queue** their casts (legacy: the later phase's cast replaced the earlier one before it
  wound up).
- Engine env objects (Berk's pillar) **despawn** when replaced or when the fight ends.
- Ring, whirlpool and soak distances are flat (2D) instead of 3D.

### Legacy fallback

`Aion:Bosses:Engine=false` restores the previous behaviour for every boss: `NpcAi.Bosses` phase loop and frenzy,
`NpcAi.ExpeditionMechanics` (pillar, tempest soak, rings, whirlpools, outer DoT, blue light, clone lines),
`NpcAi.BossMechanics` + `KromedeFireWalls` (wall grid), configured by `tools/data/expedition-boss-mechanics.json` and
`tools/data/kromede-fire-walls.json`. That code is unchanged and still tested (`ExpeditionMechanicsTests`,
`KromedeFireWallTests` pin `Engine=false`); delete it once the engine has been played.

### Still hand-coded (outside encounter files)

- The slam pipeline itself (wind-up, ground markers, landing, stagger break via `Held`), boss basic attacks and specials,
  and the client behavior cases (`NpcAi.BehaviorCases`, GimmickGauge) — the engine drives them, it does not replace them.
- Dungeon waves, mid-boss gates and recovery orbs (MapEvents / `ClientWaves`), cube rewards, boss revival.
- Skill effects and abnormals (SkillEffects / AbnormalEngine): the engine only deals NPC damage and tracks its own stacks.

## Known gaps and CANDIDATE values

Expedition mid-bosses and Vakron / Horn Den (`krao_*`, `urugugu_*`, `fire_*`, `draupnir_lakshmi/sarasvati`, `vakron_*`, `horn_*`):
skills, cast times, cooldowns, damage rows, abnormal ids, hidden sub-skill numbers (used as `damage.factor`), orb / spirit counts
and the Easy / Hard remaps are from the client tables. **Guesses** (each marked in the file's `note`): thresholds other than the
generic fury 50% (Raur 70/30, Ignus 75/65, Murute 50, Tie 75/25, Vakron 60/40 and 55/35, Lakshmi's reflect every 40 s and the
healer at 50%, Harcon's snipers at 60%), pool sizes of the reflect / colour / rock shields, the poison, share, personal-circle
and orb-explosion damage, the extra Surging Thorn ring bands and the Kuhapu purple ring, puddle and lava sizes and lifetimes.
Not modelled: Tamon's physical / magic colour split (one pool shield for both), "free by hitting" prisons (timed holds), the
Vakron 6-direction lines and giant stems, Kuhapu's chasing orbs and map lightning, Duanka's white-orb altar and torch orbs'
real positions, Raur's reflect as a real reflect (a pool that punishes when emptied), map-poison safe circle positions
(`arena_centre`), Kromede's Hard Blazing Judgment (positions unknown), effect NPCs (puddles / orbs are `zone` / `telegraph`),
the jump / glide checks (flat 2D, so ring and field blows always hit anyone in them). Hard enrage: Normal 300 s, Hard 240 s
(the official note; the client table says 300 for Fire Temple and Horn Den Hard, 240 for the others).
Add NPCs (Harcon's snipers, Lakshmi's attendants, Duanka's spirits) are existing trash templates: `world-npcs.json` has no
templates for the research's own add NPCs.

- Engine `mark`s and debuffs **without** an `abnormal` id still have no client icon (players see the notice); debuffs with an `abnormal` id use the skill-effect engine's real abnormal.
- A `cast` with no Skill.dat damage row (Odyle Absorption 1601205, damage 0) ignores the client cast time: give `wind_up_ms` explicitly.
- `parts` credit the boss's HP loss to the first unbroken part; there is no per-part hit wire yet.
- Skills with no Skill.dat damage row deal 1 (Auldor 1601030 / 1601035, Bakarma 1800655): use `damage.factor` when a real
  number is known. The migrated finals keep 1 to match the legacy fight.
- `cover` is a wedge behind the object, not real line of sight.
- Nuakum's stack/spread circles and throw skill, Kromede's Blazing Judgment (Hard, 25%): not in the data yet.
- The expedition dungeons' encounter files (`ddn_*`, `cradle_*`, `hall_*`, `azure_*`, `citadel_*`) use the client's real skill ids, cooldowns, cast times and
  area shapes for the rotation. Mechanics that the client implements with hidden helper NPCs (almost none are in `world-npcs.json` `templates`) are
  authored as `telegraph` / `zone` / `orb` / `counter` rules with explicit shapes; every number that is not a client row is labelled **GUESS** in the
  rule's `note`. Not implemented: Durbati eggs and one-player-per-rock (Hard), Nayatman's chain and barrier pair, Nazium's pillar-breaking, Balkan's
  real charge end point, Bakarma's rotating 4-wedge sweep (the 1804240 / 1804241 pair alternates instead), Beltiras's homing circle and tornado,
  Nasarak's illusion clones, Turgen's arrow lines, Basilus's Eclipse / Ordeal states and Hard colour assignment, the Alturadon stack buff icon.
- Enrage on the expedition finals uses the client's numbers: 300 s on Normal and Hard (`BossBerserkInvokeTime`), none on Exploration, damage x3 and haste 0.5
  (abnormal 10000008). The official patch notes give 4 minutes on Hard for six older expeditions only; Nathara / Alturadon datamine 5:00. Override
  `frenzy_ms` in a difficulty block if the 4 minute rule turns out to apply.
- Numbers in `expedition-boss-mechanics.json`, `kromede-fire-walls.json` and the five encounter files are CANDIDATE
  (guides and videos, `docs/BOSS-MECHANICS.md`).
