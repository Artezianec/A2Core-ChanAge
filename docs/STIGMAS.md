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
- **Costs (recovered).** Learning a stigma (level 1) costs 1 shard, levels 2-5 cost 1 each, 6-10 cost 2, 11-15 cost 4,
  16-20 cost 8: 75 shards for one stigma at 20. 20 is the last row.
- **Shards (CANDIDATE).** 1 at level 22 (the Ascension quest's shard), then 1 per level from 23 to 39 and 2 per level
  from 40 to 45: 16 at 37, 30 at 45. Global's other sources (Abyss, Nightmare and Shugo Festival shops, achievements) are
  not modelled; `Aion:Stigmas:ShardRate` multiplies the levelling shards instead. Shards are never stored: they are the
  level's total less what the stigma levels cost.
- **Slots (CANDIDATE).** 4 stigma slots open at 22, 27, 32 and 37 (global). `Slots=6` opens slots 5 and 6 at 42 and 45
  (Korea; OURS levels). A stigma casts only while it is on a quick slot, and only as many stigmas as there are open slots
  may sit on quick slots (`kQuickSlot_StigmaMaxCount`).
- **Power (OURS).** A stigma's level is its skill level for damage, on the same scale as skill-point mastery
  (`Combat.SkillLevelDamageScale`). Global's specialties at 5/10/15/20 are not modelled.
- **Cast data.** Name, cooldown, casting time, weapons, target and MP/HP/SP cost are recovered (Skill.dat facts in the
  catalogue). Damage is OURS: a stigma aimed at a target borrows a comparable class skill's damage rows; self stigmas
  (buffs, stances) have none until their SkillEffect rows are exported, so they cast but do nothing yet.
- **Reset (OURS: free).** Resetting one stigma or all forgets them, refunds every shard and clears their quick slots.
  Taking one level back refunds that level.
- **Level-down.** A character whose stigmas cost more than its level gives (GM level-down, lower ShardRate) has every
  stigma forgotten and refunded at the next load; stigmas of another class are dropped too.
- **`/stigma`** (or `//stigma`) in chat shows any player their shards, open slots and stigmas.

## Settings (`Aion:Stigmas`)

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Turns stigmas on. Off: stigma skills stay unlearnable and every skill-point message behaves as before. |
| `Slots` | `4` | Stigma slots (0 to 6). 4 = global, 6 = Korea. |
| `ShardRate` | `1` | Multiplies the shards from levelling (0 to 100). |
| `MaxLevel` | `20` | Highest stigma level (1 to 20). |
| `RequireSlot` | `true` | A stigma casts only from a quick slot. Set `false` if the client registers stigmas some other way. |
| `ShardUpdateSendsSpent` | `true` | 0x5105 for StigmaShard carries the shards spent (like the OBSERVED skill-point update); `false` sends the shards left. |

In an env file: `Aion__Stigmas__Enabled=true`, `Aion__Stigmas__ShardRate=3`.

## Messages (CANDIDATE)

The shapes come from the usmap and from the skill-point messages we OBSERVED (GameSession.SkillLevels.cs).

| Action | Client | Server |
|---|---|---|
| Learn | SkillLevelUp_RQ 0x383f {skill} | 0x3840 {result, skill}, AddSkillList 0x5101 (reason SkillPointLevelUp), SkillPointUpdate 0x5105 {count 1, type StigmaShard 2, point, 0} |
| Raise | SkillLevelUp_RQ 0x383f | 0x3840, SkillLevelUpdate 0x3813 (reason SkillPointLevelUp), 0x5105 |
| One level back | SkillLevelDown_RQ 0x3841 | 0x3842, 0x3813 (reason SkillPointLevelDown), 0x5105 |
| Reset | SkillLevelReset_RQ 0x3843 {type Stigma 2, skill or 0 for all} | 0x3844 {result, type, skill, modify list, points [StigmaShard, left]}, RemoveSkillList 0x5102 (opcode CANDIDATE), 0x5105 |
| Slot | QuickSlotSet_RQ 0x5601 (EQuickSlotType Stigma 4, or a skill slot) | 0x5602 result 0, else `kSkill_Acquire_NotFoundData` 6185 (not learned), `kQuickSlot_StigmaMaxCount` 6492, `kQuickSlot_MismatchSlotType` 6485 |

Refusals answer EResult codes: 6187 InvalidClass (not this class's stigma), 6186 InvalidLevel (at the cap), 6188
NotEnoughCharLevel (below 22), 6191 NotEnoughSkillPoint (too few shards). World entry and level-ups add the learned stigmas
to the skill list (0x5100) and send a StigmaShard 0x5105 after the skill-point one.

## Storage

Migration `100_character_stigmas.sql`: `game.character_stigmas (character_id, skill_id, level, updated_at)`, apart from
`game.character_skills` (which the level trigger manages). Stigma slots are ordinary quick slots.

## Data

`python tools/stigmas.py build --catalog <game-catalog-v2/entries.jsonl>` rebuilds `tools/data/stigmas.json` (stigma
lists, cost ladder, cast facts); `check` compares it with the catalogue (tests/test_stigmas.py runs it when the catalogue
is present, or with `AION_CATALOG=<path>`).

## To replace the CANDIDATE values

| Value | Client table |
|---|---|
| Shards and slots per level | Exp.dat `BonusStigmaPoint`, `BonusStigmaSuperiorPoint`, `StigmaSkillContextSlotMax` |
| Reset prices | GlobalSettingTable `stigma_skill_reset_cost`, `stigma_skill_reset_all_cost` |
| What each stigma does | Skill.dat SkillEffect rows for the stigma skills |
| The messages | A capture of an official stigma purchase, reset and slotting |
