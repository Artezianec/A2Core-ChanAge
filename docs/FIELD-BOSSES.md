# Field bosses

Ported from [ckrealm/A2Core](https://github.com/ckrealm/A2Core) (branch `friends-server`, commit `77ac9ed`,
`FunFeatures.cs`/`GameSession.Fun.cs`/`FunNoticeService.cs`), licensed AGPL-3.0. Respawn timers that survive a restart,
the client's `WorldMapFieldNamed` table (`tools/data/field-bosses.json`, 85 bosses) and the official randomized ranges
are ported from [KiskEmu](https://github.com/Doxylamin/KiskEmu) (AGPL-3.0, commit `d54d69472d`). We use the client's
participant reward tables, and (2026-11) aligned loot and notices with official Aion 2 videos (YouTube
[fLkKHYHv4IQ](https://www.youtube.com/watch?v=fLkKHYHv4IQ),
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
| Field Boss map tab with Spawned / Time Left / Travel / Loot Highlights | The tab's request is known: **0x3A0C** `WorldMapContentsInfo_RQ` `{i32 map id}` and **0x3A0D** `WorldMapContentsInfo_RS` (`_result`, `_map_id`, `_field_named_info_list`, `_boss_info_list`, `_rift_info_list`, `_env_obj_list`), from the usmap. `WorldMapFieldNamedInfo` has `_data_id`, `_pos`, `_is_spawned`, `_respawn_time` (i64). `_data_id` is the **ID** of client table `WorldMapFieldNamed` (KiskEmu extracted the 85-row table from the Global/EU client of 2026-11-04 into `tools/data/field-bosses.json`: code, map, NpcData id). `_respawn_time` units for **this** packet are still unobserved. KiskEmu decoded announced times from their `0x9101` captures (2026-11-02 to 11-05) to the millisecond, but did not send the official table: its layout has an optional byte they could not explain. Inventing the units or that byte would show a wrong "Time Left", so the named/timer list is still sent **empty**. Spawned bosses are listed as `WorldMapBossInfo` (`_uid` = entity key, `_monster_id` = NpcId, `_pos`) which has **no timer**. Travel and Loot Highlights are client-side. Live clocks exist (`FieldBosses.StatesOnMap`) and official timers are stored. | **Gap.** Confirm `_respawn_time` units and the `0x9101` optional byte on a live capture, then fill `_field_named_info_list` with the known `_data_id`s. Do not invent a wire format. |
| No server-wide chat broadcast on spawn/kill | Optional broadcast, **off by default**. `AnnouncementScope` is `Off` (default), `Zone`, or `Server`. `ChatBroadcast` default `false`. Older configs that only set `Announcements=true` still broadcast server-wide. | **Done.** |
| Per-participant centre text "Defeated {name}." then "Reward collected." | Each contributor is sent those strings through the known centre-notice packet **Notice_NT 0x8d2f**. Official titles ("Soul Ruler Kashapa", "Immortal Gartua", "Watcher Kaira") are in the unexported client string table; we use the short spawn name (Kashapa, Odium Gartua, …). Russian is our transliteration (OURS). | **Done**, with the title GAP. |
| Personal bound chest for every contributor (damage or heal), no single-tagger loot, no ranking UI | Everyone on the boss's hate table, plus anyone who healed an ally already on it, still on the map (dead or alive), gets their own roll of the client's `*_participant_all` table **into the bag**. The usual ground/kill/party loot from the non-participant table is skipped for field-ranked kills when `SharedLoot` is on. There is no ranking UI (none observed). | **Done.** |
| "Gartua's Loot Chest": guaranteed Artwork Scrap ×8 + 25% gear pool | Client data already has this: item **533700375** (`RewardBox` → `Box_FieldBoss_A_48_b`) is a 100% drop on the participant tables of **Odium Gartua** (npc 2400800) and the Island of Eternity boss (npc 2101074, Light-side twin). The box guarantees item 632520099 ×8 (the scrap; no display name in `items.json`) and a package at 2500/10000 = 25%. Same pattern for Kashapa: **533700373** / `Box_FieldBoss_A_46_b`; Lagta/Ragta: **533700374**. These are official item ids, not OURS. | **Done** (already in `loot.json` / `boxes.json`; now granted personally). |
| "Kaira's Merit Chest": guaranteed 800 Abyss Points + 1% gear | No spawn, npc, or item named Kaira / Watcher Kaira / Merit Chest in our export. Abyss `Box_Abyss_Boss_*` boxes have Abyss Point-like currencies, but we will not invent a mapping. | **Gap** (need client strings or a live item id). |
| Bind on pickup | Official chests are already distinct bound templates (the client labels them "(Bound)"). We grant those template ids as they are. | **Done** (no extra bind flag). |
| 100–200+ players in one fight | Hate + healer contributors, no cap. | **Done** (same path at any size). |
| Respawn hours (Gartua/Lagta 11 h, others hours, named elites shorter) | Official CANDIDATE ranges from KiskEmu captures, rolled at the kill: **named 1.5–6 h** (grade 1–5 below level 65; observed ~1.8–5.9 h), **bosses 6–12 h** (grade 6+ or level 65+, including Gartua and Lagta; observed at least 10.8 h). The client has no period (`PeriodSpawnId` names a server table). `RespawnMinutesByName` is empty by default; set e.g. `Gartua=660` to pin the retail guides' 11 h figure. HeroMonsters not in the official 85-row table use the same configured windows but are not stored. | **Done** (CANDIDATE ranges). |
| Timers survive a server restart | Official WorldMapFieldNamed bosses are stored in `game.field_bosses` (migration 126), keyed by `(world_id, code)`. After a restart a killed boss stays dead until its announced time; the corpse is already gone. Staff "respawn all" / "respawn now" persist the early time so a later restart does not kill the boss again. | **Done.** |

## What it does

- **Who counts.** Hostile open-world `HeroMonster`s (never in a dungeon, a scripted layer, or a quest's support spawn).
  A **boss** is a spawn whose name contains `Boss` or `Gartua`, or one of grade 6+: `M_*_FieldBoss_*` (Kashapa, Lagta,
  Ragta, Beritra, Dragon), `M_D1_IoI_OdiumGartua`, `World_L_A_Single_IslandOfEternity_Boss`, `M_AR*_AbBoss_*`. The other
  HeroMonsters are **named elites**.
- **Respawn.** Official WorldMapFieldNamed bosses (85 rows in `tools/data/field-bosses.json`; the first spawn of that
  NPC on its map) come back after a time rolled at the kill from their range: named 1.5–6 h, bosses 6–12 h (CANDIDATE,
  KiskEmu captures). Other open-world HeroMonsters use the same configured windows (`BossRespawnMinMinutes` /
  `NamedRespawnMinMinutes`, and optional `RespawnMinutesByName`) but are not stored. Everything else keeps the usual
  30 s. The corpse still goes 4 s after the kill (`Target.KilledAtMs`). Official timers are written to
  `game.field_bosses` and applied on startup (`FieldBossWorld`), and they feed `FieldBosses.StatesOnMap`.
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
| `BossRespawnMinMinutes` / `BossRespawnMaxMinutes` | `360` / `720` | Boss respawn window (1–2880). Each kill picks a time inside it. Official CANDIDATE: 6–12 h. |
| `RespawnMinutesByName` | empty | Fixed respawns by a part of the spawn name, ahead of the window. |
| `NamedRespawnMinMinutes` / `NamedRespawnMaxMinutes` | `90` / `360` | Named elites. Official CANDIDATE: 1.5–6 h. |
| `Announcements` | `false` | Legacy switch. If true and `AnnouncementScope` is Off, the scope is Server. |
| `AnnouncementScope` | `Off` | `Off` (official), `Zone` (same map), or `Server` (everyone). |
| `ChatBroadcast` | `false` | Also send a configured broadcast to chat in English. Official: off. |
| `ParticipantNotices` | `true` | "Defeated {name}." and "Reward collected." to each contributor. |
| `SharedLoot` | `true` | Personal participant-table reward in the bag for every contributor. |

Environment form: `Aion__FieldBosses__RespawnMinutesByName__Kashapa=360`,
`Aion__FieldBosses__AnnouncementScope=Zone`. The startup log prints the values.

## Not done

- Map Field Boss tab timers: 0x3A0C/0x3A0D is answered and the official `_data_id`s are known
  (`tools/data/field-bosses.json`), but `_field_named_info_list` stays empty until `_respawn_time` units and the
  unexplained `0x9101` optional byte are captured. See the gap table. Do not invent a wire format.
- Official display titles and Watcher Kaira's Merit Chest item id: no client string / item row in our export.
- A staff dashboard page to list or edit stored ranges is not ported yet (the table can hold a custom
  `min_minutes`/`max_minutes` per boss).
- A boss killed by an NPC respawns on the long timer (and is stored, if it is an official field boss) but is not
  announced, and only players on the hate/healer lists get a chest.
