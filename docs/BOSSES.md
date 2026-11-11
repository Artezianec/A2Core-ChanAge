# Boss phases and dungeon waves

Ported from [ckrealm/A2Core](https://github.com/ckrealm/A2Core) (branch `friends-server`, commits `c299686` and `c52fc1a`),
licensed AGPL-3.0, and adapted to our map-event code and client data. Like the original, this has **not been checked
against an official boss capture**: the boss scripts are our design on top of the client's own data.

"Client" below means the game's exported data (`map-events.json`, `world-npcs.json`, `skill-areas.json`, Skill.dat cast
times). "OURS" means an invented value, kept in `tools/data/boss-phases.json` and marked there.

## Waves (`ClearWave`)

`ClearWave` used to finish on its own after `MapEventRun.FallbackMs` (30 s). Now:

- **The Ludra raid (map 600082) uses the client's own waves.** `StartWave ReforgedAbyss_1PartyWave_03` brings the raid
  map's spawn entries `ReforgedAbyss_1PartyWave_03_1`, `_2`, … They wait dormant from the map load; before, every wave
  monster stood in the raid from the start. Parts come in order: the next one when the current one is dead, or
  `Aion:Bosses:WavePartSeconds` (25 s, OURS) after it came. Each monster goes for the nearest player. The wave is clear
  when every part came and died, and only then does the script's `ClearWave` count. `EndWave` (a failed stage) removes
  the wave, and a new `StartWave` starts it again from part 1.
- **Ferocious Horn Den's totem wave is hand-made (OURS).** Its spawn data names no wave monsters. Three of the den's
  Lycans come around the totem every 15 s while it lives, up to six times. The wave is clear when the totem and its Lycans
  are dead.
- **Any other wave keeps the old 30 s timer.** Field events, Beritra and the sealed dungeons name spawns we can't match yet.
- A wave already cleared when its `ClearWave` step begins counts at once.
- The `HpRemaining` condition (scripts that branch on a boss's HP) now reads the NPC's HP. It used to be always false.
- `ResetRebirthLimit` and `SetAbnormalVolumeState` are accepted as no-ops. The client data has no limit or volume
  effect for them.

Code: `GameSession.Bosses.cs` (waves and adds on the copy), `MapEvents.cs` (`IMapEventHost.WaveKnown`, `WaveDone`,
`StartWave`, `EndWave`, `HpPercent`; `MapEventRun.WaveCleared`).

## Boss phases (68 bosses)

`tools/boss_phases.py` builds `tools/data/boss-phases.json`, and the rules are in its docstring. The script covers 26
final bosses, 39 mid bosses and the 3 Ludra bosses. `python tools/boss_phases.py --check` fails when the file is stale.

| Piece | What it does | Source |
|---|---|---|
| **Slam** | The boss stops and winds up (0x3809 with the wind-up time) under a warning in the middle of the screen. When it lands, every player inside the area takes the hit (0x3802 plus one 0x3804 each), the tank included. Walking out dodges it. A hold or stun breaks the wind-up, and the boss tries again 4 s later. | The skill is the boss's own longest cast. **The area is the skill's client shape from `skill-areas.json` for 84 of the 95 slams** (`radius_from: skill-areas`): circles, cones and rectangles, placed on the boss or the target as the client data says, with the client's target cap. The other 11 use an invented circle (`radius_from: OURS`). **The wind-up is the skill's Skill.dat cast time for 63 slams** (`wind_up_from: client cast`). The other 32 (bosses with no slow skill, which wind up their basic attack for 3 s at 3x) are OURS. Intervals and damage multipliers are OURS. |
| **Adds** | At 70% HP (final bosses), 60% (Lagta, Kashapa) and 75% and 50% (Durbati), the boss calls 2–4 monsters around itself. They come at its target, vanish when the boss dies or the group wipes, and never respawn. | Thresholds and counts are OURS. The monster is the dungeon's own most common one (client spawns). |
| **Fury / enrage** | At 40% the boss hits harder and slams more often. At 20% (Normal and Hard) it hits harder still, with shorter cooldowns. | OURS. |
| **Frenzy** | Hard final bosses: 4 minutes into the fight they hit 3x and attack twice as fast. | Global's hard-mode rule (4 minutes). The multipliers are OURS. **Urugugu Canyon Hard and Vakron Sky Island Hard skip it**, because `Patch20261028` already applies the patch notice's frenzy to every boss hit there (`DungeonBossDamage`). The two never stack. |
| **Mid bosses** | Named bosses with six or more skills get a slam every 30 s and 20% more damage below half HP. | OURS. |

Easy modes are gentler: 2 adds, slams every 30 s, no enrage and no frenzy.

Every notice and warning carries a Russian twin (`notice_ru`, `warning_ru`). The NoticeNt packet has one text per client
culture, so `GameSession.Notice(text, russian, …)` puts the Russian at culture index 7 and the English everywhere else.
Each client shows the text for its own language.

Adds are new targets on the copy, with keys 700000–799999 (`BossSummons`). They are named `<monster>_Summon`, so the
scripts' spawner checks and kill objectives never count them.

## Settings: `Aion:Bosses`

| Setting | Default | Meaning |
|---|---|---|
| `Phases` | `true` | `false` turns every script off, and bosses fight as before. |
| `Frenzy` | `true` | The scripts' hard-mode frenzy. |
| `FrenzySeconds` | `0` | Overrides every script's frenzy time (0 keeps the script's 240 s). |
| `DamageScale` | `1.0` | Multiplies the phases' and slams' damage (0.1–10). |
| `Waves` | `true` | Tracks the waves' own monsters. `false` puts every `ClearWave` back on the 30 s timer. |
| `WavePartSeconds` | `25` | How long a wave part lives before the next part comes anyway (1–600). |

Environment form: `Aion__Bosses__DamageScale=0.8`. The startup log prints the values (`Bosses: …`).

## Not done

- There is no ground marker under a slam. The client shows the cast animation and our warning, but the official telegraph
  effect messages aren't recorded.
- The waves on other maps keep the timer (see above).

## Brigade Base (HQ1101070, layer 100002)

The layer's elite Drakan (`Multi_Cliff_Drakan_Module_2`, NPC 2701128) was up from the entry. The open-`ClearMapEvent`
boss respawn (`RespawnOpenInstanceLayerBosses`) ignored that the script starts it later. Killing it early ended the layer
before the last waves. Our port of KiskEmu `80a8e9b` (#482) brought over the kill guard but missed the dormant-spawner
gate, which is now added. `Hq1101070BrigadeBaseTests` (from KiskEmu) checks that the elite stays down at step 1 and that
the script plays through to `DungeonClear`.
