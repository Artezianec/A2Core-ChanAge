# QA gameplay coverage (follow-up to quest bots)

Goal: extend QA bots so **full gameplay** failures surface in `game.qa_stuck_buckets` / samples / heartbeat, not only quest-step stalls.

## What we can do now (mostly client-visible + existing session hooks)

| Area | Feasible in-process | Report via |
|------|---------------------|------------|
| **Quests** | Keep `QaQuestBrain` + `QuestStepDriver` | Existing reason codes |
| **Map events** | `QaMapEventRunner`, spawner kick, escort defend | `step_timeout`, `no_progress`, `death_loop` |
| **Combat basics** | `QaBotCombat` basic-attack **chain root + combo steps** (from `class-skills.json` / `GameData`), cast + hit land ticks | `mob_missing`, action-loop detail |
| **Dungeon enter/load** | `QaBotFeed`, `MapLoadTimeoutMs` | `step_timeout` (map load) |

## Flight (needs dedicated probe + some server truth)

**Probe:** `QaFlightProbe` run on a schedule (per class, level ≥ wing unlock) or on quests that require glide (HQ1101020+).

- **Takeoff / land:** send ride packets the client uses; assert `GameSession` ride state, `WorldPosition.Z` stability, ack packets (0x36xx ride family).
- **Glide / flight time:** after takeoff, sample `FlightTime` / ride energy each tick; stall if drain stuck at max while “flying” or never drains when moving.
- **Transports / paths:** record last transport id; timeout if path node index does not advance.

**Server hooks (nice-to-have):** expose ride state + last ride error on `GameSession` for QA (`LastRideRejectReason`), log teleports that drop Z through collision.

**Stuck codes (new):** `flight_no_ack`, `flight_fallthrough`, `flight_stuck_air`, `flight_path_stall`.

## Skills (class rotation harness)

**Probe:** `QaSkillProbe` per class — cyclic through `SkillBook` entries the character actually has (level + quests), including basic attack chain.

Per skill attempt:

- Send cast packets; record server accept/reject (unknown skill, requirement, cooldown).
- Compare client-visible cooldown end vs server `SkillCooldown` when available.
- After hit land tick, check target HP/abnormal or env state changed (same pattern as quest kill).

**Chain / grey skills:** explicitly try each chain step; report `skill_chain_gap` when step N works but N+1 greyed (Spiritmaster 2nd chain case).

**Stuck codes:** `skill_rejected`, `skill_no_effect`, `skill_cooldown_mismatch`, `skill_chain_gap`.

## Bosses & instances

**Probe:** `QaBossProbe` triggered on `ClearMapEvent` / `EnterDungeon` with boss map events (e.g. `Dungeon_D_0018_MapEvent`, L0016 boss).

- Enter instance → assert `MapEventRun` started, step advances within `MapEventStallTimeoutMs`.
- Boss: `KillSpawner` / `HpRemaining` / cutscene objectives — reuse runner; add phase tokens to progress fingerprint.
- Wipe: count deaths per instance run (`death_loop` already on quest step; add `boss_wipe` with run id).
- Loot: after boss kill objective, ground loot with killer tag (existing QA loot rule).
- Exit / reset: `LeaveInstance` + map event `Complete`.

**Server hooks:** `QaStartMapEventSpawner` (added in #563), optional `QaForceMapEventDirective` for tests only.

**Stuck codes:** `boss_not_spawned`, `boss_phase_stall`, `boss_unkillable`, `instance_no_loot`, `instance_exit_stall`.

## Architecture sketch

```
QaBots.StepAsync
  ├─ QaQuestBrain.Think()          # quests (primary)
  ├─ QaGameplayOrchestrator.Tick() # when quest idle / scheduled
  │    ├─ QaFlightProbe
  │    ├─ QaSkillProbe (per class slot)
  │    └─ QaBossProbe (when in dungeon layer)
  └─ Report → IQaBotStore (extend buckets: area=flight|skill|boss, quest_code optional)
```

**Config:** `QaBotOptions.GameplayProbes`: enable flags, intervals, level gates.

**Tests:** headless scenarios per probe; prod scenario adds “no silent stall” regression (map-event timeouts fire).

## Out of scope for first PR

- Full client UI parity (grey skill icons) — infer from server reject + skill row flags.
- Every dungeon — seed from `map-events.json` + hero chain table.
- PvP / arena.

## Landed in first gameplay PR

- **`QaBotCombat` basic chains:** resolve FixSlot chain root from learned skills / `IsBasicAutoAttack` (not hard-coded `*020000` mid-steps); `PickBasicChainStep` walks open `ActiveChainSkills` windows for Templar, Spiritmaster, Cleric, Chanter, etc. (`QaBotBasicChainTests`).

## Boss probe (client-export branch)

- **`QaBossProbe`** (`QaBossProbe.cs`, `Aion:QaBots:BossProbe`): in instance map-events, reports `boss_not_spawned`, `boss_phase_stall`, `boss_unkillable` via `QaQuestBrain`.
- **NpcBehaviorCase** wired in `NpcAi.BehaviorCases.cs` (`tools/npc_behavior_cases.py` → `npc-behavior-cases.json`): combat-start/casting/skill-use/die captions from client talks.
- **`tools/boss_coverage.py`**: client vs OURS per scripted boss; **`tools/boss_phases.py`** extended to extra expedition families + Deus/Arkanis mids (126 scripts).

## Dependencies

- #563 merged: map-event stall timeouts, death loop, escort defend, cutscene skip, spawner kick.
- DB: optional `area` column on `qa_stuck_buckets` or prefix reason codes (`flight_*`, `skill_*`, `boss_*`).