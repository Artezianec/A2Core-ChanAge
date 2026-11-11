# Field bosses

Ported from [ckrealm/A2Core](https://github.com/ckrealm/A2Core) (branch `friends-server`, commit `77ac9ed`,
`FunFeatures.cs`/`GameSession.Fun.cs`/`FunNoticeService.cs`), licensed AGPL-3.0. We changed the default timers to
retail-like hours, added Russian notices, and use the client's participant reward tables.

## What it does

- **Who counts.** Hostile open-world `HeroMonster`s (never in a dungeon, a scripted layer, or a quest's support spawn).
  A **boss** is a spawn whose name contains `Boss` or `Gartua`, or one of grade 6+: `M_*_FieldBoss_*` (Kashapa, Lagta,
  Ragta, Beritra, Dragon), `M_D1_IoI_OdiumGartua`, `World_L_A_Single_IslandOfEternity_Boss`, `M_AR*_AbBoss_*`. The other
  HeroMonsters are **named elites**.
- **Respawn.** A killed boss comes back after `BossRespawnMinMinutes`–`BossRespawnMaxMinutes` (default 6 h). Per-name
  overrides take precedence: Gartua and Lagta 11 h (the retail guides' figures), and Ragta and the Island of Eternity
  boss 11 h (INFERRED as their Light-side twins). Named elites take 30–60 min (OURS). Everything else keeps the usual
  30 s. The corpse still goes 4 s after the kill (`Target.KilledAtMs`).
- **Notices.** When a boss dies, every player sees "Alice and 2 others defeated the field boss Kashapa (level 48) in the
  Light lands!", and chat gets the English text. When it is due back, everyone sees "The field boss Kashapa (level 48)
  has appeared in the Light lands." Russian clients get the Russian text (culture index 7 of the notice). Boss names are
  read from the spawn names, and the Russian names are our transliterations. The sessions queue the notices in
  `FieldBosses`; the host's `FieldBossNoticeService` sends them every 5 s, using the wall clock for the delayed ones.
- **Loot for every fighter.** Everyone else on the boss's hate table who is still on the map rolls their own share on
  their own loop. That share comes from the boss's client participant table (`*_participant_all` in `loot.json`; 90 field
  bosses and elites have one), or from its full table when it has none. The drops lie at the corpse for them. The killer
  and their party nearby get the usual kill loot.

## Settings: `Aion:FieldBosses`

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | `false`: every monster respawns after 30 s, with no notices and no shares. |
| `BossRespawnMinMinutes` / `BossRespawnMaxMinutes` | `360` / `360` | Boss respawn window (1–2880). Each kill picks a time inside it. |
| `RespawnMinutesByName` | `Gartua 660, Lagta 660, Ragta 660, IslandOfEternity 660` | Fixed respawns by a part of the spawn name. |
| `NamedRespawnMinMinutes` / `NamedRespawnMaxMinutes` | `30` / `60` | Named elites (OURS). |
| `Announcements` | `true` | Kill and return notices. |
| `SharedLoot` | `true` | A loot roll for every other fighter. |

Environment form: `Aion__FieldBosses__RespawnMinutesByName__Kashapa=360`. The startup log prints the values.

## Not done

- Respawn times live in memory. A server restart brings every field boss back at once.
- A boss killed by an NPC respawns on the long timer but isn't announced.
