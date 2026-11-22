using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    public const int MechanicEnvObjKeyBase = 750_000;

    NpcAi CombatAi() => worldPlayer?.Map?.Ai ?? ai;

    void WireBossMechanicHooks(NpcAi fights)
    {
        fights.MechanicTelegraphStartedHook = OnMechanicTelegraphStarted;
    }

    void ClearBossMechanicHooks(NpcAi fights) => fights.MechanicTelegraphStartedHook = null;

    Target? BossTelegraphCaster(int bossKey)
    {
        if (worldPlayer?.Map?.Targets.TryGetValue(bossKey, out var mapTarget) == true) return mapTarget;
        if (targets?.TryGetValue(bossKey, out var solo) == true) return solo;
        return null;
    }

    /// <summary>Scripted mechanic hook: ground decals (#550) plus CANDIDATE pillar env spawn.</summary>
    IReadOnlyList<byte[]> OnMechanicTelegraphStarted(BossMechanicTelegraphEventArgs e)
    {
        var packets = new List<byte[]>();
        var caster = BossTelegraphCaster(e.BossKey);
        if (caster is null) return packets;
        var duration = (int)Math.Clamp(e.EndsMs - e.StartedMs, 0, 120_000);
        if (e.SkillId > 0 && duration > 0)
        {
            var area = SkillAreas.For(e.SkillId);
            if (area is not null && MechanicTelegraphs.WantsMarker(area))
            {
                packets.AddRange(MechanicTelegraphs.ForNpcArea(
                    CombatTelegraphRules, caster, e.SkillId, actionUid: 1, area, e.Centre, yawDegrees: 0, duration, e.StartedMs, null));
            }
        }
        if (e.MechanicId == "berk_pillar_cover" && ExpeditionBossMechanics.PillarEnvObjId() is int envId)
        {
            var key = MechanicEnvObjKeyBase + e.BossKey;
            packets.Add(GamePackets.EnvObjSpawn(key, envId, e.Centre, yaw: 0, e.StartedMs, uniqueKey: -Math.Abs(key), relationship: 7));
        }
        return packets;
    }

    List<byte[]> TickBossMechanics(NpcAi fights, long now)
    {
        IEnumerable<(int Key, Vector3d Position)> players;
        if (worldPlayer?.Map is { } map)
            players = map.Players.Values.Select(p => (p.Key, p.Position));
        else if (position is { } at && !dead)
            players = [(playerKey, at)];
        else
            return [];

        var replies = fights.TickExpeditionMechanics(now, players, (key, damage) =>
        {
            if (worldPlayer?.Map is { } m)
            {
                if (!m.Players.TryGetValue(key, out var victim)) return [];
                return victim.Session.NpcHitsPlayer(damage, now, fights.LastAttacker, fights.LastHitSkill, fights.LastHitSeq, fights.LastAttackerAt);
            }
            if (key != playerKey) return [];
            return NpcHitsPlayer(damage, now, fights.LastAttacker, fights.LastHitSkill, fights.LastHitSeq, fights.LastAttackerAt);
        });
        var mapId = worldMap;
        foreach (var (key, to) in fights.ConsumeEdgeSnaps())
        {
            if (worldPlayer?.Map is { } m && m.Players.TryGetValue(key, out var victim))
                replies.AddRange(victim.Session.Teleport(mapId, to, now));
            else if (key == playerKey)
                replies.AddRange(Teleport(mapId, to, now));
        }
        return replies;
    }
}