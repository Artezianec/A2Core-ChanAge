# Abyss Rift Zone, Beritra and the Beritra Air Raid

What exists, what is official, what is a guess. Config: `Aion:AbyssRiftZone` and `Aion:FieldEvents:Invasions` (both default on,
see [SERVER_CONFIGURATION.md](SERVER_CONFIGURATION.md)). Sources: client tables generated locally from the client, reflected in `tools/data/abyss-rift-phases.json`,
`client-waves.json`, `systems2.json`, and `map-events.json`, plus public patch notes.

## Abyss Rift Zone (scheduled event)

Code: `server/Aion.World/AbyssRiftZone.cs` (schedule, a pure phase machine `AbyssRiftZoneRun`, the world run `AbyssRiftZoneEvents`),
ticked by the sessions like the fortress siege (`GameSession.Abyss.cs` `AbyssRiftZoneWorldTick`). Tests: `AbyssRiftZoneTests`.

| Part | Status |
|---|---|
| Tuesday and Thursday 22:00, groups A 22:00, B 22:05, C 22:10 | **OFFICIAL** (KR notes 2026-06-17). The server clock is UTC+9, a **GUESS** (`ServerUtcOffsetHours`). |
| Level 45+ and item level 3,000+ | **OFFICIAL**. Used for who gets the notices and phase packets. The entry request itself is not wired (see below). |
| 5-minute combat wait | Phase 1 of the client table (`Time 5`, `UI_ABYSSRIFT_TARGET_WAIT`) = **OFFICIAL** length (player-reported 5 min). No PvP restriction is enforced (no arena). |
| 8 phases and their monsters | **Client data**: phase 2 to 6 end by kill count (12, 4, 6, 10, 8), phase 7 by `Time 60`, phase 8 when the four final bosses 2670066 / 067 / 114 / 118 are dead. A phase with several ends ends on any. |
| Spawner to NPC | **Client data**: group `Phase_N` is phase N+1; the spawner `AR1_RiftBoss_<grade>_<nnn>` is NPC `2670000 + nnn` (the phase 8 kill ids confirm it). |
| Phase 7's `Time 60` unit | **GUESS**: minutes, like the 5 of the wait (`TimeUnitSeconds` 60). |
| Map and placement | **GUESS**. `AR1` in the spawner names matches map 20 (Reshanta Lower, `E_AR1_*` artifacts), but the spawners have no coordinates in our data and the event's own map is not known. Bosses stand on a ring (radius 4,000) around the map's start point; the three groups are 20,000 apart (`ArenaRadius`, `ArenaCentre`, `GroupSpacing`). Killed ones never respawn; leftovers go when the run ends. |
| Boss mechanics | The 8 grade 6 bosses are invulnerable for 25 s at 66% and 33% HP, once each (**OFFICIAL**: 8 bosses, 25 s, at most twice; which 8 and the thresholds are **GUESSES**), as data in `abyss_rift_wh6.json` on the boss engine. |
| Progress packets | 0x385B request answered from the live run; 0x385E phase notice to eligible players at every phase change. The three counters of 0x385C stay 0 (CANDIDATE layout). |
| Not done | The entry (the minimap icon, the 400 per faction queue and the client's request are not known), faction scoring and the win / draw / loss, the final-blow reward by contribution, the Rift Bundle, the Battlefield Authority buff, the 100 m PvP points. None is in the client tables we have; the patch notes give no values. |

A run starts only within `LateStartMinutes` (5) of its open. A late server start does not begin the run.

## Beritra defence (28 spawns)

All 28 parts of the defence are placed (25 spawner parts plus the three supply boxes, which are env objects named like spawners
in `world-objects.json`; `tools/wave_data.py`, `GameSession.Bosses.cs` `PrepareWaves`). `BeritraDefenceWaveTests` pins the 28.

## Beritra Air Raid (Dimensional Invasion): hosted as data

The invasion system is the field event machinery (`GameSession.FieldEvents.cs`): `systems2.json` has the invasion events, the
schedules `Group_Light_Invasion` / `Group_Dark_Invasion` run every hour at :30 (ready 180 s, progress 600 s: matches the public
guides) and `map-events.json` has the map scripts. The Direct-entrance event of each variant is listed in `FieldEventInfo_NT` (0x6000/0x6001) and
enterable during its entry window; the map's own script then runs on the existing map event engine:

| Variant | Light / Dark event | Map | Script |
|---|---|---|---|
| Beritra Air Raid | 120101 / 220101 | 500032 / 510027 | `IN_Beritra_01_L/D_MapEvent` (`IN_BeritraAttack_*_S2`, 4 waves) |
| Broken Sword | 120201 / 220201 | 500031 / 510026 | `IN_BrokenSword_01_*_MapEvent` |
| Spirit Attack | 120301 / 220301 | 500033 / 510028 | `IN_SpiritAttack_01_*_MapEvent` |

**GUESS:** the three variants share one schedule, so one runs per slot: the variant is the server-clock hour modulo 3 (so Beritra
comes at 00:30, 03:30, 06:30, ...). Only 220101 at 12:30 was observed (consistent); a recording at 01:30 without an Elyos
invasion is not (it would show Broken Sword). The Interact-entrance events (x02-x04: field maps) are not listed. Reward mail
and scoring: not in the data. Tests: `InvasionFieldEventTests`.
