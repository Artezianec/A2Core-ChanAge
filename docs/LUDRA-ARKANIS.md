# Abyssal Forge: Ludra (raid) and Shattered Arkanis (Transcendence dungeon)

Both are **data** on the boss mechanics engine ([BOSS-MECHANICS-ENGINE.md](BOSS-MECHANICS-ENGINE.md)) and the skill effect
engine ([SKILL-EFFECT-ENGINE.md](SKILL-EFFECT-ENGINE.md)). Marks: **official** = read from the 2.0.9.0 client tables,
**OURS** = the existing `boss-phases.json` script kept as it was, **CANDIDATE** = guessed (also written in each file's `note`).

## What the repo already had

| | Abyssal Forge (Sanctuary raid) | Shattered Arkanis (Transcendence) |
|---|---|---|
| Maps | 600082 Normal (spawns, env objects, routes, waves, `Rahu` / `Ketu` / `BossRoom` MapEvents), content ticket | 600063 / 600065 / 600068 / 610062 = grades `G_01` / `G_03` / `G_06` / `G_09` (stages 1-4) with MapEvents and spawns |
| Entry | Item level 2800 (`DungeonInfo`), 10 players | Stage item levels from `Patch20261028` |
| NPCs | Rahu `2301006`, Ketu `2301007`, Ludra `2301014`, stones `2301015-17`, reactor `2301008`, forge guards `2301019` | Per stage: elite 1 `…707`, elite 2 `…708`, boss `…709` (`2330…`, `2350…`, `2353…`, `2356…`) and the adds `…701` / `…703` |
| Bosses | generic `boss-phases.json` scripts (compiled) | generic scripts (compiled) |

Gaps found and closed (generated catalogs, additions only, by `tools/ludra_arkanis_data.py`, which is idempotent; `--check` is
tested):

- **Boss-kit skills.** `world-npcs.json` carried only `NpcData.ActiveSkillList`; the phases of these bosses use skills from
  `NpcBehaviorCase`. They are now in `npc_skills` / `skill_hits` (official Skill.dat rows), so the skill effect engine's
  `ratio_damage` and `npc_abnormals` (regenerated, `tools/skill_effect_engine.py`) know them.
- **Ludra Hard** (`ReforgedAbyss_Hard`, map 600083, item level 3000, NpcData `2300280-2300298`). The client has the NpcData
  rows, the MapEvent and a content ticket, but the repo had **no Hard map data** (no spawns, env objects, sub-events, dungeon
  row). It is **derived** from Normal (the client's Hard map points are the Normal ones): same spawns / objects with the Hard
  NPC ids, the Normal sub-events on the Hard NPCs, 3 revives. Everything derived is CANDIDATE.

## Engine additions (small, generic, behind the existing flags)

| Addition | Why | Doc |
|---|---|---|
| `damage.ratio: true` (with `skill`) | A telegraph / cast whose client row is `MaxHPRatio` (a 70% orb, the lethal 500% line) used the level curve. Opt-in, uses `SkillEffectEngine.RatioDamageAgainst` (flag `AION_NPC_RATIO_DAMAGE`). | Engine doc, Damage |
| `rule.npc_alive: <npcId>` | The abyss stones buff the party only while they stand. | Engine doc, rules |
| `MechanicDebuffHook` wired to `GameSession.EngineApplyAbnormal` | The `debuff` action's `abnormal` now puts the real client abnormal on the player: a debuff from the boss (immunities, stacking), or a buff for the stones. | Engine doc, `debuff` |

## Rahu (Lagta) `2301006` / Hard `2300282`: `ludra_rahu.json`

- Official: kit rotation (1802760 arc, 1802730 cross, 1802740 circle, 1802800 circle, 1802810), the Thread of Gaps (1802860,
  lethal 1600-wide line, `damage.ratio`), the curse abnormal 18027101, the three-black-hole wipe, the abyss stones.
- OURS (kept): call at 60% (3 forge guards), enrage at 30%.
- CANDIDATE: the curse pass (20 s, every 32 s, "stand with an ally"), black-hole size / damage, Bonds of Grief thresholds and
  radius, stone-to-abnormal order.
- Missing: the quadrant floor split with the knock-off dash and the Devourer feeding (no NpcData for the Devourers in 2.0.9.0,
  no floor-piece data), the circle-overlap cast 1802720 (500% in a 5000 circle: its safe-circle rule is not in the client data).
- No berserk: the client has none for Rahu.

## Ketu (Kashapa) `2301007` / Hard `2300283`: `ludra_ketu.json`

- Official: kit rotation (1801840, 1801820, 1801860), Burst of Power (1801961, 6.5 s), falling orbs (1801940 / ratio 80% 1801943),
  Abyssal Gaze (1801970, 9 s), the stones.
- OURS: call at 60%, enrage at 30%.
- CANDIDATE: the cone-per-player as circles, orb catching window, gaze safe radius.
- Missing: the Mortal Soulstone interrupt (NPCs `2920677/78`, skills 1801964/65 ratio 60%), the homing scythes (1801888) and the
  look-away/face colour gaze after 50%: summoned NPCs without templates and facing checks the engine does not have. No berserk.

## Ludra (Durbati) `2301014` / Hard `2300290`: `ludra_ludra.json`

- Official: **600 s berserk** (`enrage_timer`), kit rotation (1803140, 1803130, 1803120, 1803090), the bingo grids (Skill11_02 /
  _03: 70% / 50% max-HP ratio, 4.2 s / 3.8 s) as a 3x3 tile `telegraph` with random patterns, orbs (1803203 70%), tornadoes
  (1803175 20%), lightning (1803215).
- OURS (kept): call at 75% (4 guards), fury at 50% (+15%, faster slam, 4 guards), enrage at 25%.
- CANDIDATE: tile patterns, the tile size (2000 around the arena centre), timers, the Hard berserk (the Nov 11 notice implies
  4 min for Hard; kept at the client's 600 s).
- Missing: the 16 s lethal Skill13 (1803010 / 1803011, 500% ratio): its dispel condition (abnormals 18064951/52) is not
  understood; the red/blue orb absorb; the OrcusGem reactor and extractors (`2301008`, stays on its generic script).

## Shattered Arkanis: `arkanis_siliator.json`, `arkanis_ruthilis.json`, `arkanis_talisra_s1..s4.json`

Siliator and Ruthilis are the stage elites 1 and 2 (their kits are `Naga_01` and `Naga_02`; the compiled slams 1606100 /
1606450 confirm it), Talisra (Thalythra) is the stage boss. Stages 1-2 are listed under `Normal`, 3-4 under `Hard`.

- Official: the three kits as rotations (Siliator arcs, line, chakram link 1606060 with MoveSpeed 16060602, DefenseDown
  16060902; Ruthilis ring 1606440, 90-degree long cone 1606450, lightning bomb 16063724/25; Talisra 1801630 / 40 / 50 / 60),
  **300 s berserk** for Talisra.
- OURS (kept): elite fury at 50%; Talisra call at 70% (the stage's adds), fury at 40%, enrage at 20%; the compiled slams.
- Missing: Talisra's light/dark orbs (NPCs `2920563/64`, abnormals `18017001-03` / `18017101-03`), the 18 s dome cast
  (1801690), the 12 pillars (`2920687-98`), Skill12 (1801780, grades 4+) and the colour marker 1801680: the last two have no
  damage row in the client (abnormals only), which the slam pipeline would turn into basic hits. Siliator's stagger-break race.
  The stage-2 Hard-key split is by grade only; no per-stage damage tuning (CANDIDATE: none).
- No separate Arkanis maps were missing: the client has all ten grades; only the encounters were.

## Tests

`LudraArkanisEncounterTests` (engine: `npc_alive`, the debuff hook, `damage.ratio`; data: every boss resolves to its authored
encounter on each difficulty, the stones follow the difficulty, berserk timers, skills exist; a 130 s fight of each boss),
`tests/test_ludra_arkanis_data.py`, `python tools/boss_mechanics_validate.py`.
