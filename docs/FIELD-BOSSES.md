# Field bosses

Ported from [ckrealm/A2Core](https://github.com/ckrealm/A2Core) (branch `friends-server`, commit `77ac9ed`,
`FunFeatures.cs`/`GameSession.Fun.cs`/`FunNoticeService.cs`), licensed AGPL-3.0. We changed the default timers to
retail-like hours, use the client's participant reward tables, and (2026-11) aligned loot and notices with official
Aion 2 videos (YouTube [fLkKHYHv4IQ](https://www.youtube.com/watch?v=fLkKHYHv4IQ),
[s2w8M4Df2_c](https://www.youtube.com/watch?v=s2w8M4Df2_c); also [ukztVdNYTWI](https://www.youtube.com/watch?v=ukztVdNYTWI)
for the personal chest).

## Official facts (observed 2026-11)

From the official client in those videos, not inferred:

- **Map → Exploration → Field Boss** lists the zone's bosses with distance, state **Spawned** (green) or a
  **Time Left** countdown (`1h 38min 13s`, `35min 10s`, `35s`, …), a **Travel** button, and a **Loot Highlights Info**
  list. The client therefore receives per-boss respawn state from the server.
- No server-wide chat broadcast on spawn or kill was visible.
- On a kill, each participant sees upper-centre text **"Defeated Watcher Kaira."** then **"Reward collected."**
- Rewards are a **personal BOUND chest per participant** (for example "Kaira's Merit Chest": guaranteed Abyss Points 800
  plus a 1% gear pool; "Gartua's Loot Chest": guaranteed "Artwork Scrap: Field Boss I (Asmodian) (Bound) x8" plus a 25%
  chance of one of nine gear pieces). Gear is bind-on-pickup. No contribution or ranking UI. 100–200+ players fight
  together. "Kashapa's Loot Chest (Bound)" is granted the same way (ukztVdNYTWI).

## Gap table

| Official fact | What we did | Action |
|---|---|---|
| Field Boss map tab with Spawned / Time Left / Travel / Loot Highlights | The tab's request is known: **0x3A0C** `WorldMapContentsInfo_RQ` `{i32 map id}` and **0x3A0D** `WorldMapContentsInfo_RS` (`_result`, `_map_id`, `_field_named_info_list`, `_boss_info_list`, `_rift_info_list`, `_env_obj_list`), from the usmap. `WorldMapFieldNamedInfo` has `_data_id`, `_pos`, `_is_spawned`, `_respawn_time` (i64). `_data_id` is the **ID** of client table `WorldMapFieldNamedTableItem` (MapId, FieldNamedName, FieldNamedSpawnerName, SpawnType, PeriodSpawnId). That table is **not** in our client-data export, and `_respawn_time` units are unobserved (CANDIDATE: absolute Unix ms). Inventing `data_id`s would show the wrong names, so the named/timer list is sent **empty**. Spawned bosses are listed as `WorldMapBossInfo` (`_uid` = entity key, `_monster_id` = NpcId, `_pos`) which has **no timer**. Travel and Loot Highlights are client-side. In-memory clocks exist (`FieldBosses.StatesOnMap`) for when the table is exported. | **Gap.** Export `WorldMapFieldNamedTableItem`, confirm `_respawn_time` units on a live capture, then fill `_field_named_info_list`. Do not invent a wire format. |
| No server-wide chat broadcast on spawn/kill | Optional broadcast, **off by default**. `AnnouncementScope` is `Off` (default), `Zone`, or `Server`. `ChatBroadcast` default `false`. Older configs that only set `Announcements=true` still broadcast server-wide. | **Done.** |
| Per-participant centre text "Defeated {name}." then "Reward collected." | Each contributor is sent those strings through the known centre-notice packet **Notice_NT 0x8d2f**. Official titles ("Soul Ruler Kashapa", "Immortal Gartua", "Watcher Kaira") are in the unexported client string table; we use the short spawn name (Kashapa, Odium Gartua, …). Russian is our transliteration (OURS). | **Done**, with the title GAP. |
| Personal bound chest for every contributor (damage or heal), no single-tagger loot, no ranking UI | Everyone on the boss's hate table, plus anyone who healed an ally already on it, still on the map (dead or alive), gets their own roll of the client's `*_participant_all` table **into the bag**. The usual ground/kill/party loot from the non-participant table is skipped for field-ranked kills when `SharedLoot` is on. There is no ranking UI (none observed). | **Done.** |
| "Gartua's Loot Chest": guaranteed Artwork Scrap ×8 + 25% gear pool | Client data already has this: item **533700375** (`RewardBox` → `Box_FieldBoss_A_48_b`) is a 100% drop on the participant tables of **Odium Gartua** (npc 2400800) and the Island of Eternity boss (npc 2101074, Light-side twin). The box guarantees item 632520099 ×8 (the scrap; no display name in `items.json`) and a package at 2500/10000 = 25%. Same pattern for Kashapa: **533700373** / `Box_FieldBoss_A_46_b`; Lagta/Ragta: **533700374**. These are official item ids, not OURS. | **Done** (already in `loot.json` / `boxes.json`; now granted personally). |
| "Kaira's Merit Chest": guaranteed 800 Abyss Points + 1% gear | No spawn, npc, or item named Kaira / Watcher Kaira / Merit Chest in our export. Abyss `Box_Abyss_Boss_*` boxes have Abyss Point-like currencies, but we will not invent a mapping. | **Gap** (need client strings or a live item id). |
| Bind on pickup | Official chests are already distinct bound templates (the client labels them "(Bound)"). We grant those template ids as they are. | **Done** (no extra bind flag). |
| 100–200+ players in one fight | Hate + healer contributors, no cap. | **Done** (same path at any size). |
| Respawn hours (Gartua/Lagta 11 h, others hours, named elites shorter) | Unchanged: default 6 h, per-name overrides 11 h for Gartua/Lagta/Ragta/IslandOfEternity, named 30–60 min (OURS). Video countdowns (6 h 55 min on Eternal Gartua, 1 h 3 min on Kashapa, 35–48 min on many named) are consistent with hour-scale timers, not a reason to change defaults yet. | Keep; tune from a live capture if needed. |
| Timers survive a server restart | Still in memory only. A restart brings every field boss back at once. | **Gap** (would need a migration if we persist this; not added). |

## What it does

- **Who counts.** Hostile open-world `HeroMonster`s (never in a dungeon, a scripted layer, or a quest's support spawn).
  A **boss** is a spawn whose name contains `Boss` or `Gartua`, or one of grade 6+: `M_*_FieldBoss_*` (Kashapa, Lagta,
  Ragta, Beritra, Dragon), `M_D1_IoI_OdiumGartua`, `World_L_A_Single_IslandOfEternity_Boss`, `M_AR*_AbBoss_*`. The other
  HeroMonsters are **named elites**.
- **Respawn.** A killed boss comes back after `BossRespawnMinMinutes`–`BossRespawnMaxMinutes` (default 6 h). Per-name
  overrides take precedence: Gartua and Lagta 11 h (the retail guides' figures), and Ragta and the Island of Eternity
  boss 11 h (INFERRED as their Light-side twins). Named elites take 30–60 min (OURS). Everything else keeps the usual
  30 s. The corpse still goes 4 s after the kill (`Target.KilledAtMs`).
- **Personal rewards.** Everyone who damaged the boss, or healed someone already on its hate table, and is still on the
  map, rolls the boss's client participant table (`*_participant_all` in `loot.json`; 90 field bosses and elites have
  one) into their bag. That table already lists the official bound chest at 100% plus extra items (stones, and so on).
  The killer does not get a second, exclusive ground drop. `SharedLoot=false` turns this off and restores ordinary
  killer/party ground loot.
- **Notices.** Each of those contributors sees "Defeated {name}." then "Reward collected." (0x8d2f), in English or
  Russian. Optional kill/spawn broadcasts ("Alice and 2 others defeated the field boss Kashapa…") are **off** unless
  `AnnouncementScope` is `Zone` or `Server` (or a legacy `Announcements=true`). Chat is a separate flag, also off.
  The host's `FieldBossNoticeService` sends configured broadcasts every 5 s, using the wall clock for the delayed ones.

## Settings: `Aion:FieldBosses`

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | `false`: every monster respawns after 30 s, with no notices and no shares. |
| `BossRespawnMinMinutes` / `BossRespawnMaxMinutes` | `360` / `360` | Boss respawn window (1–2880). Each kill picks a time inside it. |
| `RespawnMinutesByName` | `Gartua 660, Lagta 660, Ragta 660, IslandOfEternity 660` | Fixed respawns by a part of the spawn name. |
| `NamedRespawnMinMinutes` / `NamedRespawnMaxMinutes` | `30` / `60` | Named elites (OURS). |
| `Announcements` | `false` | Legacy switch. If true and `AnnouncementScope` is Off, the scope is Server. |
| `AnnouncementScope` | `Off` | `Off` (official), `Zone` (same map), or `Server` (everyone). |
| `ChatBroadcast` | `false` | Also send a configured broadcast to chat in English. Official: off. |
| `ParticipantNotices` | `true` | "Defeated {name}." and "Reward collected." to each contributor. |
| `SharedLoot` | `true` | Personal participant-table reward in the bag for every contributor. |

Environment form: `Aion__FieldBosses__RespawnMinutesByName__Kashapa=360`,
`Aion__FieldBosses__AnnouncementScope=Zone`. The startup log prints the values.

## Not done

- Map Field Boss tab timers: 0x3A0C/0x3A0D is answered, but `_field_named_info_list` stays empty until
  `WorldMapFieldNamedTableItem` is exported and `_respawn_time` units are captured. See the gap table.
- Official display titles and Watcher Kaira's Merit Chest item id: no client string / item row in our export.
- Respawn times live in memory. A server restart brings every field boss back at once.
- A boss killed by an NPC respawns on the long timer but is not announced, and only players on the hate/healer lists
  get a chest.
