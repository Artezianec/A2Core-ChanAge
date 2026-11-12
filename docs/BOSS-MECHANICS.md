# Official boss mechanics (Expedition finals)

Five party-expedition finals compared with official clear videos and guides, then
overlaid on `tools/data/boss-phases.json` via `tools/data/boss-phase-overrides.json`.
Generic scripts for every other boss (invented 70/40/20 adds and fury, "longest
cast" as a repeating slam) are unchanged. See `docs/BOSSES.md` for the engine.

Videos were downloaded 2026-11-11 with yt-dlp, no cookies, 720p, to
`/workspace/yt/bosses/<boss>/`. Frames and a per-file list are in
`/workspace/yt/bosses/MANIFEST.md` (not in git).

"Client" means exported Skill.dat / skill-areas.json / NpcData / MapEvent data.
"Guides / videos" means the sources below, not a client AI table — the client has
none. OURS is still marked where we invent wording, intervals, or skip a mechanic
the engine cannot run.

## Sources

| Boss | Dungeon | Videos | Written guides used for names and HP timing |
|---|---|---|---|
| Ultimate Berk (`KraoCave_Boss_1`, 2300171 / 2310171 / 2320171) | Krao Cave | [Kasum](https://www.youtube.com/watch?v=E8slBCTydgc) `E8slBCTydgc`, [Madsin](https://www.youtube.com/watch?v=X8qaN4CoQbs) `X8qaN4CoQbs`, [NoBS Hard](https://www.youtube.com/watch?v=Mt_21j5nSw0) `Mt_21j5nSw0` | dbaion2, grimvane, mobalytics, aion2tierlist |
| Divine Auldor (`UruCan_Stage03_Boss_1`, 2300218 / 2310218 / 2320218) | Urugugu Canyon | [Kasum](https://www.youtube.com/watch?v=DFFYV9hsc0w) `DFFYV9hsc0w`, [Madsin](https://www.youtube.com/watch?v=CVccGtQSLRs) `CVccGtQSLRs`, [ComplexNeko](https://www.youtube.com/watch?v=WBeawCe-PDQ) `WBeawCe-PDQ` | aion2maps, grimvane, aion.nerfplz (Tempest Strike 9.6 s) |
| Kromede's Desire (`Fire_Temple_Single_Boss_1`, 2300371 / 2310371 / 2320371) | Fire Temple | [Kasum](https://www.youtube.com/watch?v=Xknuet3lHTI) `Xknuet3lHTI`, [Madsin](https://www.youtube.com/watch?v=lPAX6cNiXO8) `lPAX6cNiXO8`, [ComplexNeko](https://www.youtube.com/watch?v=YWh7n3yHyvI) `YWh7n3yHyvI` | aion2data (official guide lines: "Find the gap", "I will crush you!", "Let desire consume you!"), daevaguides, dbaion2 |
| Transcendent Bakarma (`Draupnir_Abyss_Boss_Bakarma`, 2300471 / 2310471 / 2320471) | Draupnir | [Kasum](https://www.youtube.com/watch?v=iiAU7tX4dGk) `iiAU7tX4dGk`, [TheWhelps](https://www.youtube.com/watch?v=IxlBT9EBFog) `IxlBT9EBFog`, [Somonul](https://www.youtube.com/watch?v=E7OvTz1QYxw) `E7OvTz1QYxw` | grimvane, azphel ("Wretched worm, begone.", 18 s Wave of Death) |
| Ferocious Horn Nuakum (`…Ferk_01_V01`, 2300910 / 2310910 / 2320910) | Ferocious Horn Den | [NoBS](https://www.youtube.com/watch?v=EbdqcSNKZio) `EbdqcSNKZio`, [Madsin](https://www.youtube.com/watch?v=4lT8erUBNAM) `4lT8erUBNAM`, [ComplexNeko](https://www.youtube.com/watch?v=XLSG_nfKMWk) `XLSG_nfKMWk` | aion2maps, grimvane (hunt 75%/55%, Blue Light ~30%) |

Internal spawn names in parentheses; nameplates are from guides and datamines, not an L10N export (we have no skill/NPC name table in `tools/data`).

## Gap table

| Official | Ours before | Action |
|---|---|---|
| Easy MapEvents spawn HP/MP/SP recovery orbs at **70% and 30%**. Normal/Hard boss-room scripts have no HP branches (mechanics live in AI). | Invented trash **adds at 70%** (and Hard 40%) on every final. | **Removed** invented adds on these five. Easy orbs already come from MapEvents. |
| Named HP beats (Berk 75/50/25, Auldor 75/30, Kromede 75/50/25, Bakarma 70/30, Nuakum 75/55/30), with on-screen cast names. | Generic 70 call / 40 fury / 20 enrage, generic "gathers power" warning. | **Replaced** with official thresholds and named notices. No invented damage/haste on those beats. |
| Berk **Extinction for Eternity** at 50%: 8–9 s room-wide stagger check; fail = wipe. Client skill **1800140** (8 s, 100k, circle 9000). | 1800140 was a **repeating** slam every 18–30 s. | **Once-only stagger check** at 50%. Breaking the gauge (already sets `Held`) cancels the landing and does not retry. Fail uses the skill's own damage and area. |
| Berk **Wave of Fury** at ~75% and ~25% (some guides 35%): hide behind a summoned stone pillar. Closest skill **1800050** (6 s, 100k, circle 4500). | Not scripted (except the wrong repeating 1800140). | **Notice only.** We have no pillar/cover collision, so landing this as a room slam would wipe every group. |
| Auldor **Tempest Strike** at ~75% and ~30%: ~10 s cast, stack on a marked player to split the hit. Client **1601030** (9.57 s; Easy variant **1601035**). Skill.dat damage is 0. | Repeating slam was basic attack **1600870** (no area, invented 3 s wind-up). | Filler rotation uses real area skills (lane 1600970, slam 1600960, front arc 1600930). Tempest is a **once, no-land** 9.57 s cast plus a stack notice. Marked-player soak and damage split need engine work. Below 30% the outer arena becomes a DoT — also missing. |
| Kromede **Find the gap** fire walls at 75% and 25%: 16.5 s untargetable walls with gaps. Client **1801185** (16.5 s; Hard **1801191**, 15.1 s, circle 5000). Official lines: "I will crush you!", "Find the gap!". Clones from ~50% (Hard earlier). | Repeating frontal **1801080** only, plus invented adds. | 1801080/1801050/1801060 stay as a **filler rotation**. Fire walls are a **once, no-land** long cast plus the official line. Moving-wall gaps, burns, and clones need engine work. |
| Bakarma **Wave of Death** at ~70% and ~30%: screen darkens, jump outward rings, break an 18 s stagger or wipe. Client **1800655** (18 s, circle 6500) but Skill.dat damage is **0-0**. Quote: "Wretched worm, begone." | Repeating room skill **1800554**. Invented adds. | **Once-only stagger check** at 70% and 30% with 1800655. Fail currently lands the area for at most the engine minimum (not a true wipe — no damage row). Jump-rope rings, the inner safe ring, player teleport to the edge, and second-phase whirlpools need engine work. |
| Nuakum hunt at ~75% and ~55% (block a clone, then stack or spread by circle size); **Blue Light of Destruction** at ~30% (redirect four throws into a central orb, three hits). | Repeating telegraph was basic swipe **1802000** (cast 0, invented 3 s wind-up). Invented adds. | Filler rotation uses real area skills (1802090 / 1802060 / 1802100). Hunt and Blue Light are **notices only**. No 34 s cast is in this boss's skill list (longest damaging cast is 1.3 s); the orb skill id needs a live capture. |
| Conquest (Normal) **frenzy at 5:00**, Easy none, Hard 4:00 on some dungeons (patch notice; Urugugu/Vakron Hard already applied in `Patch20261028`). aion2data: frenzy is +100% move/cast/combat speed and +200%? / 100% damage boost — we take **damage ×2**, haste 0.5. | Frenzy only on Hard, 4:00, damage ×3 (OURS). | **Normal 5:00, damage ×2** on these five. Hard stays 4:00, now ×2 so it does not overshoot the guide figure; Urugugu Hard still skips the script frenzy so it does not stack with the patch. Easy remains without frenzy. Multipliers stay marked: the client table for the buff is not in our export. |
| Ground telegraph markers under AoEs. | Cast bar + our warning; no ground decal packets on record. | **Still missing.** Documented, not invented. |
| Donut (Ring) safe-inside hits. | `SkillAreas` treats Ring as a filled circle (outer extent only). | **Still missing** engine support. |

## What the engine does now

- A script may list `telegraphs` (a rotation of real area skills) instead of one slam.
- A phase telegraph may be `once` (fires at the HP threshold, then the filler rotation resumes) and `stagger_check` (a hold or stagger-gauge break cancels it and does not retry).
- `land: false` plays the wind-up and skill start/end but deals no damage (fire walls, soaks whose official hit we cannot yet reproduce).
- Easy Auldor remaps Tempest to **1601035**; Hard Kromede remaps the long cast to **1801191**.

Mid-bosses, other finals (Vakron / EleKing, Transcendence, Ludra Lagta/Kashapa/Durbati) and invented adds there are **not** in this pass.

## Still needs a live capture or engine work

- Ground telegraph effect messages (ids unknown).
- Pillar/cover collision (Berk Wave of Fury).
- Moving fire walls with gaps, clone NPCs (Kromede).
- Player mark + shared/split soak damage, tornado binds, outer-arena DoT (Auldor).
- Expanding jump-rope rings, edge teleport, whirlpools; wipe damage for 1800655 (Bakarma).
- Clone intercept, stack/spread circles, central orb redirect; Blue Light skill id (Nuakum).
- Official L10N skill names (warnings use guide names; wording of some notices is OURS).
- Frenzy buff values from a client table, if one exists.

Nothing here is deployed. Next free migration number remains **126** (no schema change).
