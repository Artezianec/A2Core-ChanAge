# Stigmas

Stigmas the way global Aion 2 plays them. Ported from [ckrealm/A2Core](https://github.com/ckrealm/A2Core) (branch
`friends-server`, commits `8416c5a` and `dbc178b`, AGPL-3.0) and rebuilt on our skill-point code. **Off by default**: the
stigma messages are CANDIDATE (no official stigma purchase has been captured), so turn them on to test with a client.

Every value is marked as in CONTRIBUTING.md: **recovered** (the client's own tables), **CANDIDATE** (inferred), **OURS**.

## How it plays

- **Stigma skills (recovered).** Each class has 13 stigma skills: SkillAcquireData rows with `AcquireType Stigma` (117 in
  all, 8 classes plus the Brawler, client class `Fighter`). They are not in any level kit: a character buys them with
  Stigma Shards. `tools/data/stigmas.json` lists them.
- **Opening (recovered).** Every Stigma row needs character level 22. Level 1 also needs Ascension grade 3, which we do
  not check (OURS).
- **Costs (CONFIRMED, global).** Learning a stigma (level 1) costs 1 shard, levels 2-5 cost 1 each, 6-10 cost 2, 11-15
  cost 4, 16-20 cost 8: 15 shards to reach 10, 75 to reach 20. 20 is the last row. 13 stigmas per class. A KR launch
  video used a different early ladder; see Official observations. The default stays the global ladder.
- **Shards (CANDIDATE levelling + CONFIRMED shop item).** 1 at level 22 (the Ascension quest's shard), then 1 per level
  from 23 to 39 and 2 per level from 40 to 45: 16 at 37, 30 at 45. `Aion:Stigmas:ShardRate` multiplies the levelling
  shards only. Extra shards from shops, the Stigma Shard item and staff grants are stored per character
  (`game.character_stigma_bonus`) and count toward the load-time trim budget. Available shards are (levelling total +
  bonus) less what the stigma levels cost.
- **Slots (CONFIRMED, global).** 4 stigma slots open at 22, 27, 32 and 37 (global). `Slots=6` opens slots 5 and 6 at 42
  and 45 (Korea; OURS levels). A stigma casts only while it is on a quick slot, and only as many stigmas as there are
  open slots may sit on quick slots (`kQuickSlot_StigmaMaxCount`).
- **Power (OURS).** A stigma's level is its skill level for damage, on the same scale as skill-point mastery
  (`Combat.SkillLevelDamageScale`). Global's specialties at 5/10/15/20 are not modelled.
- **Cast data.** Name, cooldown, casting time, weapons, target and MP/HP/SP cost are recovered (Skill.dat facts in the
  catalogue). Damage is OURS: a stigma aimed at a target borrows a comparable class skill's damage rows; self stigmas
  (buffs, stances) have none until their SkillEffect rows are exported, so they cast but do nothing yet.
- **Reset (OBSERVED cost).** Resetting one stigma or all forgets them, refunds every spent shard and clears their quick
  slots. Official KR launch (Nov 2025) charged **100,000 Kinah** on the confirm button and refunded all spent shards.
  `Aion:Stigmas:ResetKinah` defaults to 100000; 0 is free (a later KR/TW video claimed a free reset). The same amount is
  charged for one skill or all; we have no official all-reset price. If the character has too little Kinah, the reset is
  refused with generic result 1 (we have no named not-enough-Kinah EResult on this path) and nothing is forgotten.
  Taking one level back refunds that level and does not charge Kinah.
- **Level-down and load trim.** A character whose stigmas cost more than its levelling shards plus bonus shards (GM
  level-down, a lower `ShardRate`) is trimmed at the next load: levels are reduced, then whole stigmas forgotten, only
  until the cost fits the budget (highest levels first, then highest skill ids). Bonus shards keep purchased progress
  from being wiped. Stigmas of another class are dropped, levels above `MaxLevel` are clamped, and quick slots of
  forgotten stigmas are cleared. A 0x5105 shard update is sent with the entry skill-point sync when the stigma tab is
  open (level 22+).
- **`/stigma`** (or `//stigma`) in chat shows any player their shards, open slots and stigmas. Staff may
  `//stigma grant <count>` to add bonus shards.

## Settings (`Aion:Stigmas`)

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Turns stigmas on. Off: stigma skills stay unlearnable and every skill-point message behaves as before. |
| `Slots` | `4` | Stigma slots (0 to 6). 4 = global, 6 = Korea. |
| `ShardRate` | `1` | Multiplies the shards from levelling (0 to 100). Bonus shards are not multiplied. |
| `MaxLevel` | `20` | Highest stigma level (1 to 20). |
| `ResetKinah` | `100000` | Kinah charged for one reset (one skill or all). `0` is free. |
| `RequireSlot` | `true` | A stigma casts only from a quick slot. Set `false` if the client registers stigmas some other way. |
| `ShardUpdateSendsSpent` | `true` | 0x5105 for StigmaShard carries the shards spent (like the OBSERVED skill-point update); `false` sends the shards left. **Not changed:** this default is CANDIDATE. Note that a reset's 0x3844 always carries shards *left*, so one reset currently sends left in 0x3844 and spent in 0x5105. Do not flip the default without a capture of an official reset. |

In an env file: `Aion__Stigmas__Enabled=true`, `Aion__Stigmas__ShardRate=3`.

## Messages (CANDIDATE)

The shapes come from the usmap and from the skill-point messages we OBSERVED (GameSession.SkillLevels.cs).

| Action | Client | Server |
|---|---|---|
| Learn | SkillLevelUp_RQ 0x383f {skill} | 0x3840 {result, skill}, AddSkillList 0x5101 (reason SkillPointLevelUp), SkillPointUpdate 0x5105 {count 1, type StigmaShard 2, point, 0} |
| Raise | SkillLevelUp_RQ 0x383f | 0x3840, SkillLevelUpdate 0x3813 (reason SkillPointLevelUp), 0x5105 |
| One level back | SkillLevelDown_RQ 0x3841 | 0x3842, 0x3813 (reason SkillPointLevelDown), 0x5105 |
| Reset | SkillLevelReset_RQ 0x3843 {type Stigma 2, skill or 0 for all} | 0x3844 {result, type, skill, modify list, points [StigmaShard, **left**]}, RemoveSkillList 0x5102 (opcode CANDIDATE), 0x5105 (spent or left per `ShardUpdateSendsSpent`). A type other than Stigma (2) is refused (`kSkill_Acquire_InvalidClass` 6187) and does not forget anything. |
| Slot | QuickSlotSet_RQ 0x5601 (EQuickSlotType Stigma 4, or a skill slot) | 0x5602 result 0, else `kSkill_Acquire_NotFoundData` 6185 (not learned), `kQuickSlot_StigmaMaxCount` 6492, `kQuickSlot_MismatchSlotType` 6485 |

Refusals answer EResult codes: 6187 InvalidClass (not this class's stigma, or a reset with the wrong acquire type), 6186
InvalidLevel (at the cap), 6188 NotEnoughCharLevel (below 22), 6191 NotEnoughSkillPoint (too few shards). A failed store
save answers generic result 1 (the same as a skill-point save failure), not `NotFoundData` 6185. A refused level-up
below 22 does not send 0x5105, matching the login sync (the stigma tab is still closed). World entry and successful
level-ups add the learned stigmas to the skill list (0x5100) and send a StigmaShard 0x5105 after the skill-point one when
the character is at least level 22.

## Storage

Migration `100_character_stigmas.sql`: `game.character_stigmas (character_id, skill_id, level, updated_at)`, apart from
`game.character_skills` (which the level trigger manages). Stigma slots are ordinary quick slots.

Migration `125_character_stigma_bonus.sql`: `game.character_stigma_bonus (character_id, shards, updated_at)` holds bonus
shard points from the Stigma Shard item, shops and `//stigma grant`. Numbered 125 because 124 is reserved for a
parallel field-boss branch.

## Official observations

Sources, all looked at on 2026-11-11:

- [oHzyEGdzZso](https://www.youtube.com/watch?v=oHzyEGdzZso) — KR launch, 2025-11-19, Korean. Stigma window, upgrade and
  reset. 0:11–0:20 acquire; 2:16–2:47 level-up on 지연 폭발 (Delayed Explosion); 3:02–3:11 reset dialog.
- [kvniUsnS6cg](https://www.youtube.com/watch?v=kvniUsnS6cg) — global English guide, 2026-10-20. 0:23–1:28 window, 13
  skills, 4 slots, costs (15 shards to 10, 75 to 20); 2:08–2:23 Korea has more slots and cap 25, global is 4 / 20;
  6:41–7:01 reset refunds shards (dialog not shown; author unsure about a cost).
- [8HgwN6H758A](https://www.youtube.com/watch?v=8HgwN6H758A) — later KR/TW English guide, 2026-09-06. 4:17–5:03 Abyss
  Growth shop buys a shard, toast “Obtained 1 Stigma Shard points”, then Level Up; 10:14–11:46 claims reset is free and
  shows a 6-slot grid with levels 20/25.
- [Jkj0yvi8E7o](https://www.youtube.com/watch?v=Jkj0yvi8E7o) — 2026-10-10, narrates global slot unlocks 22 / 27 / 32 / 37
  (no stigma window on screen).
- [Best Stigmas Aion 2 Guide (Odealo, Oct 2026, global)](https://odealo.com/articles/best-stigmas-aion-2-guide) —
  slots 22/27/32/37, cap 20, 13 per class, cost ladder 1/2/4/8 (75 to 20). Also says shards come from the Abyss Point
  shop (rising tier prices), Nightmare and Shugo shops.

What we took from them:

| Fact | Status | What we do |
|---|---|---|
| Global 4 slots at 22/27/32/37, cap 20, 13 stigmas/class | CONFIRMED (guide + kvniUsnS6cg) | Already the default; marked CONFIRMED in `tools/data/stigmas.json`. |
| Cost ladder 1 / 2 / 4 / 8 per level in bands 1–5 / 6–10 / 11–15 / 16–20 (15 to 10, 75 to 20) | CONFIRMED (guide + kvniUsnS6cg; already recovered from SkillAcquireData) | Unchanged default. |
| KR launch upgrade clicks on Delayed Explosion: 1, 1, 2, 2 for 1→2, 2→3, 3→4, 4→5; reset at LV5 refunded 6 shards | OBSERVED regional / older ladder (oHzyEGdzZso 2:16–3:11) | Documented only. If learn costs 1, those four upgrades plus learn would be 7, but the dialog said 6; we do not guess which. The global default stays 1+1+1+1+1 = 5 to reach 5. |
| Reset dialog “지연 폭발 LV 5 성장을 초기화하시겠습니까?” / “사용된 스티그마 샤드 6개를 돌려받습니다.” with a yellow confirm costing 100,000 Kinah; all spent shards refunded | OBSERVED (oHzyEGdzZso 3:02–3:11) | `ResetKinah` defaults to 100000; shards are refunded as before. A later KR/TW video claims a free reset: set `ResetKinah=0`. |
| Level-up toast “[지연 폭발] 스킬이 LV 2로 상승했습니다.” | OBSERVED (oHzyEGdzZso ~2:16) | Almost certainly client-side from the skill-level update we already send. No matching server system-message id is in our tables, so we do not add a server chat/system line. |
| Trade Shop → Abyss tab: “Stigma Shard (Bound)” listed at 10,000 with weekly purchase limit 0/50. Guide: Abyss Point shop with rising tier prices, plus Nightmare and Shugo shops | OBSERVED UI + recovered GameShopGoods | Item **531900010** “Stigma Shard (Bound)” (`Charge_StigmaShardPoint_1_A_s_01a_b`) is in Item.dat: `GetStigmaShard` +1, `AutoUseWhenAcquire`. Already in `tools/data/shops.json`: Abyss upgrade lists (Light 401015113–116, Dark 501015113–116) at **10,000 / 15,000 / 20,000 / 25,000 Abyss Points** (`930100003`), limit 50 per listing; Nightmare upgrade 1,500 Phantasmal Fragments (`930100017`), limit 35; FieldEvent usable 300 Centuryroot Tokens (`930100011`), limit 35. Coin shops now resolve those `goods_lists` (`coin_shop_abyss`, `coin_shop_nightmare`). Buying or using the item grants bonus shard points. The video’s “10,000 Kinah” is the first Abyss-Point price; the currency in the client tables is Abyss Points, not Kinah. The export has `LimitPeriod` 0 (lifetime “all”), while the video showed a weekly 0/50; we did not overwrite the recovered period. `coin_shop_festa` is not mapped to FieldEvent lists (name mismatch) — staff can still `//stigma grant`. Dev menu `Item_StigmaShard` (`acquireitem 930100004`) is not an Item.dat row. |

## Shop and item gaps

- Superior Stigma Shard (531900027) and the 180-point voucher (531900026) are not implemented (global cap is 20; those are for KR 20→25 / a max-point item).
- Unstable Stigma Shard (632000002) morph is not implemented.
- No official `stigma_skill_reset_all_cost` was captured, so reset-all uses the same `ResetKinah` as a single-skill reset.

## Data

`python tools/stigmas.py build --catalog <game-catalog-v2/entries.jsonl>` rebuilds `tools/data/stigmas.json` (stigma
lists, cost ladder, cast facts); `check` compares it with the catalogue (tests/test_stigmas.py runs it when the catalogue
is present, or with `AION_CATALOG=<path>`).

## To replace the CANDIDATE values

| Value | Client table |
|---|---|
| Levelling shards and slots per level | Exp.dat `BonusStigmaPoint`, `BonusStigmaSuperiorPoint`, `StigmaSkillContextSlotMax` |
| Reset prices (one vs all) | GlobalSettingTable `stigma_skill_reset_cost`, `stigma_skill_reset_all_cost` |
| Shop limit period (video weekly vs export 0) | GameShopGoods `LimitDurationType` on the 401015113–116 / 501015113–116 rows |
| What each stigma does | Skill.dat SkillEffect rows for the stigma skills |
| The messages | A capture of an official stigma purchase, reset and slotting |
