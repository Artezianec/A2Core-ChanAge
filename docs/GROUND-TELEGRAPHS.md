# Ground AoE telegraphs

Red ground decals during area skills come from **SkillRangeNoticeFilterLocation_NT** (usmap name). We had cast bars and server-side hit shapes but did not send this packet before.

## Packet (CANDIDATE)

| Item | Value |
|---|---|
| Live opcode | `0x3848` (dev `0x36F1` + `0x157`, same run as `0x3847` SkillCooltimeUpdate_NT) |
| Builder | `PlayerPackets.SkillRangeNoticeFilterLocation` |
| Schema | `tools/packet_structs.py` → `DevPacketData_GameServer_SkillRangeNoticeFilterLocation_NT` |

Body (usmap order; matches the Python builder, **no official capture**):

1. `_key` i32 — caster entity key  
2. `_skill_id` i32  
3. `_skill_action_uid` u8  
4. `_effect_seq` i32 (first damage row uses `1`)  
5. `_skill_process_uid` i32 (`0`, CANDIDATE)  
6. `_effect_pos` Vec3 — filter centre (same placement as `0x3802` for NPC area skills)  
7. `_effect_dir` f32 — facing in degrees  
8. `_effect_time_ms` i32 — ms until the first `0x3804` (or `0` to clear, CANDIDATE on cancel)

## Config

`Aion:Combat:GroundMarkers` (default **on**). Environment: `Aion__Combat__GroundMarkers=false`.

## When it is sent

- **Boss slam** — with `0x3809`, duration = wind-up + `HitDelayMs`; clear on hold/stun (`effect_time_ms=0`, CANDIDATE).  
- **NPC area skill** — at `0x3809` or `0x3802`, duration until first hit (`NpcLandMs`).  
- **Player ground / area skill** — in the cast batch after `0x3802`, duration = `SkillTiming.HitLandMs` for row 0.

Shapes come from `tools/data/skill-areas.json` (SkillEffectFilter). Donut safe-zone rendering is still wrong server-side (see `docs/BOSS-MECHANICS.md`).

## Hook for boss scripts

`NpcAi.MechanicTelegraphStarted` — `Func<MechanicTelegraphs.Started, IReadOnlyList<byte[]>>?`

`MechanicTelegraphs.Started` fields: `Caster`, `SkillId`, `ActionUid`, `EffectSeq`, `Centre`, `YawDegrees`, `DurationMs`, `AtMs`.

Return extra packets to append after the marker (custom walls, notices). Fire Temple grid work can subscribe here without patching core slam logic.

Example:

```csharp
ai.MechanicTelegraphStarted = evt =>
{
    if (evt.SkillId == 1801185) return [GameSession.Notice("Find the gap!", null, 22, "FF8040")];
    return [];
};
```

## Verification in game

1. Set `Aion:Combat:GroundMarkers=true` (default).  
2. Pull a mob with a circle AoE (or fight a phased boss with `skill-areas` slam).  
3. You should see a red ground shape during wind-up; moving out before it finishes should avoid damage.  
4. Toggle `false` and confirm decals disappear (cast bar / hits unchanged).

If decals are missing with the flag on, capture a fight pcap — opcode or field layout may differ from the usmap guess.