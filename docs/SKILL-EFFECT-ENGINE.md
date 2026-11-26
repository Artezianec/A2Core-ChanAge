# Skill effect engine

One data-driven path for what skills and abnormals *do* (damage side effects, heals, resource gains, procs, passives,
stacking, life cycle), for players and NPCs. Effects are resolved by client effect type, never by skill id.

Source of truth: a client table export generated locally from the client (Skill, SkillEffect, SkillEffectLv,
SkillEffectFilter, SkillAbnormal, SkillAbnormalEffect, SkillAbnormalEffectLv), read through `tools/client_export.py`.
The export itself is not committed; regenerate it locally, then run the tools below.

## Pieces

| Piece | Role |
|---|---|
| `tools/skill_effect_engine.py` | Generator: export → `tools/data/skill-effect-engine.json` (`--check` fails when stale). Run by `tools/client_skill_sync.py`. |
| `tools/data/skill-effect-engine.json` | GENERATED. Never edit by hand. |
| `server/Aion.World/SkillEffectRegistry.cs` | Every handled `ESkillEffectType` / `EAbnormalEffectType` with its handler family and whether it worked before the engine (`legacy`). Unknown types are logged once (`skill_effect_unknown`, with the skill name). |
| `server/Aion.World/SkillEffectEngine.cs` | Loads the JSON; per-level values; drain and HP-ratio math; feature flags. |
| `server/Aion.World/AbnormalEngine.cs` `Resolve` | Abnormal stacking / replacement in one place (v3). |
| `server/Aion.World/GameSession.SkillEffectEngine.cs` | Session side: triggers, proc skills, drains, CastSkill, life cycle, the API for the boss engine. |
| `server/Aion.World/NpcAi.SkillEffectEngine.cs` | NPC HP-ratio damage. |
| `tools/skill_effect_coverage.py` | Coverage report → `tools/reports/skill-effect-coverage.md` (types and rows handled before / after, passives per class). |

Regenerate after a new client export:

```bash
python tools/skill_effect_engine.py        # data
python tools/skill_effect_coverage.py      # report
# or everything: python tools/client_skill_sync.py
```

## Data file sections

| Section | Contents |
|---|---|
| `skill_effect_types`, `abnormal_effect_types` | Row count per client type (the coverage universe). |
| `passives` | Passive skill → its `Abnormal_Passive` abnormals (boxes.json first, else the SkillEffect rows). |
| `triggers` | Abnormal → its `TriggerSkillBy*` rows: `on` (OnHit / OnDamaged / OnKill / OnGetAbnormal / OnAddAbnormal / OnBlock / OnEvade / OnWeaponBlock / OnShieldBlock), `proc_skill`, `chance` (/10000), `cooldown_ms`, `max_count`, filters (`results`, `element`, `boss_only`, `target_effect`, `victim`, `category`, `match`), `level_group`, owning `skill`. Later rows of the same type with another proc are upgrade tiers (`variant`, not fired). |
| `proc_skills` | Proc skill → its SkillEffect rows with `side` (self / target from SkillEffectFilter). Trigger procs and the skills of `CastSkill` rows. |
| `drains` | Skill → Damage-row life steal: ratio [13] (/10000 of the damage), cap [14] (/10000 of the caster's max HP), hit results [25], kind [26]. |
| `ratio_damage` | NPC skill → Damage row calc type [10] `MaxHPRatio` / `CurrentHPRatio`, ratio [11] (/10000), cap [12]. |
| `npc_abnormals` | NPC skill → `[abnormal, min ms, max ms, chance, level]` for skills `ported-skill-abnormals.json` lacks (one row per abnormal). |
| `skill_elements` | PC skill → `AttributeType` (for element-filtered triggers). |
| `abnormal_effects` | SkillAbnormalEffect rows of referenced abnormals no other table carries (first upgrade tier per type). `AbnormalSpecs.Info` falls back to these last. |
| `effect_levels`, `abnormal_effect_levels` | SkillEffectLv / SkillAbnormalEffectLv rows of referenced groups, compacted: only levels whose values change; the server takes the highest stored level ≤ the wanted one. |

## Runtime

### Triggers and procs

`FireClientTriggers(TriggerEvent)` walks the player's active abnormals, including the hidden passive buffs
(`RefreshPassiveStatBuffs` now adds every learned passive abnormal the tables know). For each matching trigger row:

1. Level: the owning skill's `SkillLevel` (passives), else the buff level; `TriggerAt` applies the SkillAbnormalEffectLv row.
2. Filters: hit result (Normal / Critical), element of the triggering skill, boss-only (`HeroMonster` / `LegendMonster`),
   target abnormal effect (`NpcHasAbnormalEffect`), kill victim (PC / NPC), incoming abnormal category / id / group.
3. Internal cooldown per (abnormal, effect row), max count per buff instance, chance roll.
4. `CastProcSkill` runs the proc's rows at that level: heals (`HpHeal`, `MpHeal`, `SpHeal`, `APHeal`, `*Ratio`) on the
   player; `Abnormal_True` / `Abnormal` / `Abnormal_Cc` on the player (self side, through `AddBuff`) or the other side
   (`ApplyNpcDebuff` / PvP `TryApplyPlayerDebuff`); `Dispel*`; `DecreaseCooltime`; `Damage` (flat / weapon-ratio row through
   `PlayerRowHits`) on the target. `Spawn` / `AggroTransfer` procs are logged, not run.

Events and where they are raised:

| Event | Hook |
|---|---|
| OnHit | `ApplyScheduledNpcHit` → `AfterCombatHitDealt` (every landed hit with damage > 0) |
| OnDamaged | `PlayerDamageReplies` → `AfterPlayerDamaged` (HP actually lost; the attacker is the target side) |
| OnKill | `PlayerKilledNpc` (the player's own kill) → `AfterPlayerKill` |
| OnGetAbnormal | `AddNewBuff` → `AfterBuffAdded` (not for hidden passives, not from inside a proc) |
| OnBlock / OnEvade / OnWeaponBlock / OnShieldBlock / OnAddAbnormal | Wired, but no server event raises them yet (player block / evade / parry rolls are not modelled). |

Recursion: a proc may trigger at most one more level (`triggerDepth`).

`tools/data/passive-triggers.json` (the older improvised table) still loads, but with client triggers on, an entry is skipped
when the skill's passive abnormals carry client trigger rows. Today all 37 entries are covered.

### Life steal

`AfterCombatHitDealt`: when the hit's skill has a `drains` row, heal `damage × ratio / 10000`, capped at
`cap / 10000 × max HP`, for the listed hit results. Kind `...Once`: once per cast (one heal within 400 ms per skill).

### Abnormal life cycle

| Type | Behaviour |
|---|---|
| `Mot`, `Sot` | `[interval, min, max, ...]` MP / SP per tick (like `Hot`). |
| `Mot_Ratio`, `Sot_Ratio` | `[interval, ratio, ?, cap]` of the max over the duration (like `Hot_Ratio`). |
| `CastSkill` | `[interval ms, skill, ...]`: the player's own abnormal casts its proc every interval (first after one interval); 9999999+ = once on landing (CANDIDATE). Target-side rows hit the selected target. |
| `OnExpireAbnormal` | `[abnormal, reason, ms]`: reason `TimeOut` / `None` → the next abnormal when this one runs out. |
| `AbnormalClear` `HpDecreaseCount` | `[HpDecreaseCount, hits, chance]`: removed after that many HP-losing hits. |
| `DecreaseCooltimeByKill` | `[CurrentRatio, ratio, skill, chance]`: on a kill, the skill's running cooldown shrinks by the ratio. |
| `ImmuneBind` | No values: immunity to `Bind`. |
| `HpBarrier` | `[None, min, max, ratio]`: min..max absorbed, else ratio /10000 of max HP. |

### Stacking (v3)

`AbnormalEngine.Resolve(active, abnormalId, level, durationMs, rule)` decides Add / Refresh / Reject and what to remove,
from `abnormal-stack-rules.json` (SkillAbnormal GroupId, AbnormalOverlapCount, AbnormalOverlapTimeType, AbnormalReplaceType):

- Same abnormal: `Separate` stacks up to the overlap count, then refreshes the oldest; `Longer` keeps the later expiry;
  `Renew` / `None` restart it.
- Same group, other abnormal: `Replace` / `None` remove it; `Priority` / `PriorityReplace` reject the newcomer when the
  active one has a higher level (CANDIDATE: `DisplayPriority` is 50 on nearly every row, so level is the only order);
  `Time` rejects the newcomer when the active one has more time left than the new duration (CANDIDATE).

`GameSession.AddBuff` uses it when `AION_ABNORMAL_ENGINE_STACKING` is on; off keeps the older same-id refresh /
same-group replace path unchanged. Decisions are logged as `abnormal_stack`.

### NPC skills

- HP-ratio Damage rows (`ratio_damage`): `NpcAi.RatioDamage` replaces the level-curve damage in the main hit
  (`Resolve`), volleys (`MonsterVolley` hits) and splash (`Splash`), against players (`NpcAi.PlayerHpOf`, set by
  `GameSession` like `DefenseOf`) and NPC victims. Defense does not reduce it (OURS). Example: Bakarma's 1800655 is 500% of
  the victim's max HP after an 18 s cast. Boss mechanic code (`NpcAi.Bosses.cs`, `NpcAi.BossMechanics.cs`) is untouched.
- Abnormal rows: `PortedSkillAbnormals.NpcRows` falls back to `npc_abnormals`.

### API for the boss mechanics engine and scripts

```csharp
// An abnormal on the player through the engine's stacking and rows; an NPC caster makes it a debuff (immunities apply).
List<byte[]> GameSession.EngineApplyAbnormal(int abnormalId, int durationMs, int casterKey, int sourceSkill, long now);
// A proc skill's rows (heals, abnormals, damage) as the player's, at a level, against a target key.
List<byte[]> GameSession.EngineCastProcSkill(int procSkill, int level, int targetKey, long now);
// HP-ratio damage of an NPC skill, or null.
int? SkillEffectEngine.RatioDamageAgainst(int skillId, int victimHp, int victimHpMax);
```

The replies are the caller's to send.

The boss mechanics engine uses both: `debuff` actions with an `abnormal` id go through `EngineApplyAbnormal` (`GameSession.OnMechanicDebuff`, wired to
`NpcAi.MechanicDebuffHook`), and `damage.hp_ratio` in an encounter file is the same MaxHPRatio share as `NpcRatioDamage` (flag `AION_NPC_RATIO_DAMAGE`).

## Caster movement (Teleport_Caster rows)

`SkillMoves` (`GameSession.SkillMoves.cs`) moves the caster for the `Teleport_Caster`, `Pre_Cast_Teleport_Caster` and
`Pre_SkillStart_Teleport_Caster` rows of `skill-areas.json` (`effects`), by row, never by skill id. The row is
`[type, location, distance cm, angle degrees, height cm]`:

| Location | Destination |
|---|---|
| `TargetLocation_CasterToTargetDirection` | the target's edge on the caster's side (angle 180: Leaping Slam 11190000 lands in front of its target). |
| `TargetLocation_TargetDirection` | the target's edge in the direction of its own facing (angle 180 = its back: Shadowstrike 13070000). |
| `CasterLocation_*` / `SpawnLocation_*` | a dash from the caster toward the target, capped at the row's distance. |
| `MapLocation_*` | the row's absolute `x,y,z`. |

The row's distance is added to the target's radius for the target locations; 0 falls back to the edge plus the melee gap.
The height value is ignored (2D). `Teleport_Caster` applies when the skill's first hit lands, the `Pre_` rows at the cast
start. The caster's client gets `CharMoveUpdateReject_RS`, the others `CharMoveStop_NT` (CANDIDATE: no live capture of the
official skill-teleport message).

## Flags

| Variable | Default | Off |
|---|---|---|
| `AION_ABNORMAL_ENGINE_STACKING` | on | `AddBuff` legacy stacking. |
| `AION_SKILL_ENGINE_TRIGGERS` | on | No client triggers, drains, CastSkill or life-cycle rows; passive-triggers.json only. |
| `AION_NPC_RATIO_DAMAGE` | on | HP-ratio NPC skills use the level curve. |

`0`, `false`, `off`, `no` turn a flag off. The static properties on `SkillEffectEngine` can be set in tests.

## Debug (PlaytestDebug actions)

`skill_trigger` (event, abnormal, effect row, proc, level, rows run), `skill_drain`, `skill_effect_unknown` (once per
type), `abnormal_stack` (stacking decision), `abnormal_unknown_effect` (older abnormal path).

## CANDIDATE / OURS readings

- OnDamaged HP-heal procs with a cooldown ≥ 30 s (Blood Absorption 26%, Revitalization Contract 35%, 60 s) fire only at or
  below half HP: the export has no HP-threshold column.
- Priority replacement by level and Time replacement by remaining time (above).
- CastSkill intervals ≥ 9999999 ms = once.
- Drain "once": 400 ms window per skill.

## Known gaps (need client data or more server systems)

See `tools/reports/skill-effect-coverage.md` for the full list. The largest:

- `EndCastSkill` / `EndCastSkillByCaster` / `CastSkillByCaster` (abnormal rows that cast at the end or as the caster):
  need the caster-side context.
- `Move_CannotUseSkill`, `SoftMove`, `BlockFlight`, `BlockMoveControl`, `BlockTargetSkill`: movement / targeting states.
- `SkillTransform`, `GaugeEffect`, `AdjustSecondaryStatMaxValue`, `StatConvert` (passives: 14 rows), `StanceMissDamage`.
- Upgrade-tier trigger rows (`variant`, e.g. Absorb Essence AB4 → 15760008 MpHeal 38): the export does not say what unlocks them.
- `ConvertDamageToMP` / `ConvertDamageToHpHeal` `[5000, 1000, ...]`: meaning of the two values unknown.
- `Toggle`, `Rebirth`, `ExchangeResource`, `Multiply_Dot`, `SelfDamage` SkillEffect rows.
