using Aion.Protocol.Native;

namespace Aion.World;

// Player zones (tools/data/pc-zones.json): Spawn markers with no summon AI (Bittercold Wind damages enemies each tick; Divine Aura
// buffs allies and may pulse damage). PcSummons skips markers without attacks, so the cast places the marker and ZoneTick runs
// until lifetime ends (0x3642 flag 7).
partial class GameSession
{
    sealed record PlacedZone(int Key, string Group, int SkillId, PcZones.Zone Zone, CastRequest Request, byte ActionUid, int TicksLeft, long NextTickAt);

    readonly List<PlacedZone> zones = [];

    /// <summary>A cast zone skill: the caster's previous zone of the skill leaves and the new one appears at the target.</summary>
    List<byte[]> PlaceZone(int skillId, CastRequest request, byte actionUid, long now)
    {
        if (targets is null || PcZones.For(skillId) is not { } zone) return [];
        PlaytestDebug.Action("zone_placed",
            ("skillId", skillId), ("npc", zone.Npc), ("targetKey", request.TargetKey),
            ("lifetimeMs", zone.LifetimeMs), ("tickMs", zone.TickMs), ("ticks", zone.Ticks));
        var group = $"zone{skillId}";
        zones.RemoveAll(z => z.Group == group);
        var replies = Dismiss(group, now);
        var slot = Enumerable.Range(0, SummonSlots).FirstOrDefault(s => !targets.ContainsKey(SummonKey(s)));
        var key = SummonKey(slot);
        var centre = request.TargetKey != playerKey && targets.TryGetValue(request.TargetKey, out var locked) ? locked.Position : request.Location;
        var t = new Target
        {
            Key = key, Name = zone.Name, Hp = 1, HpMax = 1, NpcId = zone.Npc, Position = centre,
            Level = level, Direction = request.Yaw, Relationship = 22,   // ERelationshipEntity PC_Summon
            Immune = true, Radius = 1, OwnerKey = playerKey,
        };
        targets[key] = t;
        summonKeys[group] = key;
        seenNpcs.Add(key);
        replies.Add(GamePackets.NpcAppear(t.Appearance() with { Reason = NpcAppearance.NpcSpawnReason }));
        replies.AddRange(ZoneCastEffects(skillId, zone, request, actionUid, key));
        zones.Add(new PlacedZone(key, group, skillId, zone, request, actionUid, zone.Ticks, now + NpcAi.HitDelayMs));
        return replies;
    }

    /// <summary>Zones whose next tick is due hurt the enemies in their area; a zone with no ticks left leaves.</summary>
    List<byte[]> ZoneTick(long now)
    {
        if (zones.Count == 0) return [];
        var replies = new List<byte[]>();
        foreach (var placed in zones.ToList())
        {
            if (targets is null || !targets.TryGetValue(placed.Key, out var marker) || marker.Dead)
            {
                zones.Remove(placed);
                continue;
            }
            var current = placed;
            while (current.TicksLeft > 0 && now >= current.NextTickAt)
            {
                if (current.Zone.Damage is not null)
                    replies.AddRange(ZoneDamage(current, marker, current.NextTickAt));
                if (current.Zone.AllyAbnormals is { Length: > 0 })
                    replies.AddRange(ZoneAllyBuffs(current, marker, current.NextTickAt));
                current = current with { TicksLeft = current.TicksLeft - 1, NextTickAt = current.NextTickAt + Math.Max(1, current.Zone.TickMs) };
            }
            zones[zones.IndexOf(placed)] = current;
            if (current.TicksLeft > 0) continue;
            zones.Remove(current);
            replies.AddRange(Dismiss(current.Group, now));
        }
        return replies;
    }

    /// <summary>Timed NoEffect / Spawn rows of the cast skill (client preview VFX; Bittercold Wind 1528000011 / 1528000021).</summary>
    List<byte[]> ZoneCastEffects(int skillId, PcZones.Zone zone, CastRequest request, byte actionUid, int markerKey)
    {
        if (zone.CastEffects is not { Length: > 0 } rows) return [];
        var lockedTarget = request.TargetKey != 0 ? request.TargetKey : playerKey;
        var replies = new List<byte[]>();
        foreach (var row in rows)
        {
            var to = row.Target switch
            {
                var t when string.Equals(t, "marker", StringComparison.OrdinalIgnoreCase) => markerKey,
                var t when string.Equals(t, "target", StringComparison.OrdinalIgnoreCase) => lockedTarget,
                _ => playerKey,
            };
            var effectId = SkillSideData.WireEffectId(row.EffectId, skillId);
            replies.Add(GamePackets.SkillEffect(to, playerKey, skillId, actionUid, effectId));
        }
        return replies;
    }

    List<byte[]> ZoneDamage(PlacedZone placed, Target marker, long at)
    {
        if (placed.Zone.Damage is not { } damageRow) return [];
        var area = placed.Zone.Blast(SkillAreas.For(placed.SkillId));
        var victims = targets!.Values
            .Where(t => TrapVictim(t) && area.Contains(marker.Position, marker.Direction, t.Position, t.Radius))
            .OrderBy(t => t.Position.DistanceSquared(marker.Position)).Take(Math.Max(1, area.Max)).ToList();
        var skillLevel = CastSkillLevel(placed.SkillId);
        var pulseFx = SkillSideData.WireEffectId(damageRow.EffectId, placed.SkillId);
        var replies = new List<byte[]>
        {
            GamePackets.SkillEffect(marker.Key, playerKey, placed.SkillId, placed.ActionUid, pulseFx),
        };
        foreach (var victim in victims)
        {
            var wired = damageRow with { EffectId = SkillSideData.WireEffectId(damageRow.EffectId, placed.SkillId) };
            var hit = PlayerRowHits([wired], victim.Defense, victimLevel: victim.Level, skillLevel: skillLevel)[0];
            var questKill = questLog?.OpenKillNpcIds().Contains(victim.NpcId) == true;
            var mapEventKill = mapEventRuns.Any(run => run.OpenKillNpcIds().Contains(victim.NpcId));
            replies.AddRange(ApplyScheduledCombatHit(new ScheduledCombatHit(placed.SkillId, placed.Request, placed.ActionUid, victim.Key, hit, 1, at,
                CombatHitKind.Npc, questKill, mapEventKill), at));
        }
        return replies;
    }

    List<byte[]> ZoneAllyBuffs(PlacedZone placed, Target marker, long at)
    {
        if (placed.Zone.AllyAbnormals is not { Length: > 0 } abnormalIds) return [];
        var area = placed.Zone.Blast(SkillAreas.For(placed.SkillId));
        var duration = placed.Zone.AbnormalDurationMs ?? Math.Max(placed.Zone.TickMs + 200, 1_000);
        var replies = new List<byte[]>();
        if (position is { } here && !dead && area.Contains(marker.Position, marker.Direction, here, NpcAi.PlayerRadius))
            foreach (var abnormalId in abnormalIds)
                replies.AddRange(EngineApplyAbnormal(abnormalId, duration, playerKey, placed.SkillId, at));
        if (worldPlayer?.Map is not { } map) return replies;
        foreach (var ally in map.Players.Values)
        {
            if (ally.Session.dead || ally.Key == playerKey) continue;
            if (ally.Session.profileRace != profileRace || PvpHitKind(ally.Key) is not null) continue;
            if (!area.Contains(marker.Position, marker.Direction, ally.Position, NpcAi.PlayerRadius)) continue;
            foreach (var abnormalId in abnormalIds)
                replies.AddRange(ally.Session.EngineApplyAbnormal(abnormalId, duration, playerKey, placed.SkillId, at));
        }
        return replies;
    }

    /// <summary>Tests: the zones this session has placed and not yet ended.</summary>
    internal int PlacedZoneCount => zones.Count;

    /// <summary>Tests: cast zone skill <paramref name="skillId"/> on <paramref name="targetKey"/> (0: at <paramref name="at"/>).</summary>
    internal List<byte[]> PlaceZoneForTest(int skillId, int targetKey, Vector3d at, long now) =>
        PlaceZone(skillId, new CastRequest(skillId, 0, 0, false, targetKey == 0 ? playerKey : targetKey, 0, at), 1, now);
}
