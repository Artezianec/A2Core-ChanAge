using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class NpcAi
{
    public KromedeFireWalls.Run? ActiveFireWall { get; internal set; }
    public int FireWallPhasesTriggered { get; private set; }

    /// <summary>Second fire-wall phase within the same fight (25% after 75%).</summary>
    bool FireWallSecondPhase => FireWallPhasesTriggered >= 1;

    internal List<byte[]> StartWindUpMechanic(Target boss, BossTelegraph slam, Vector3d centre, long now)
    {
        if (slam.Mechanic != "bakarma_jump_rings") return [];
        var state = EnsureExpedition();
        var cfg = ExpeditionBossMechanics.BakarmaConfig();
        var windUp = Math.Max(SlamWindUpMs(slam), SlamMinWindUpMs);
        state.ArenaCentre = centre;
        state.RingsUntilMs = now + windUp + cfg.PulseMs * cfg.Rings.Length;
        state.RingPulse = 0;
        state.RingNextPulseMs = now + cfg.PulseMs;
        if (currentPlayers is not null)
        {
            foreach (var (key, pos) in currentPlayers)
                edgeSnaps.Add((key, ExpeditionBossMechanics.ArenaEdge(centre, pos, cfg.EdgeSnapRadius)));
        }
        return RaiseTelegraph(ExpeditionBossMechanics.Telegraph(boss, slam.Mechanic, slam.Skill, centre, now, state.RingsUntilMs));
    }

    internal List<byte[]> MaybeStartFireWall(Target boss, BossTelegraph slam, Vector3d centre, long now)
    {
        if (!KromedeFireWalls.IsFireWallTelegraph(slam)) return [];
        var windUp = Math.Max(SlamWindUpMs(slam), SlamMinWindUpMs);
        var run = KromedeFireWalls.Start(boss.Key, slam.Skill, centre, now, windUp, FireWallSecondPhase);
        ActiveFireWall = run;
        return RaiseTelegraph(KromedeFireWalls.TelegraphEvent(run, boss.NpcId, now));
    }

    public void FireWallActivated(long now)
    {
        if (ActiveFireWall is null) return;
        FireWallPhasesTriggered++;
        ActiveFireWall.ActiveUntilMs = now + KromedeFireWalls.ActiveDurationMs;
        ActiveFireWall.NextDotMs = now + KromedeFireWalls.DotIntervalMs;
    }

    public void ClearFireWall()
    {
        ActiveFireWall = null;
        FireWallPhasesTriggered = 0;
    }

    /// <summary>Blocks entering a fire-wall cell (legacy grid, or an engine zone with blocks_movement); standing in a gap is allowed.</summary>
    public bool FireWallBlocksMove(Vector3d from, Vector3d to, long now)
    {
        if (EngineBlocksMove(from, to, now)) return true;
        if (ActiveFireWall is null || now > ActiveFireWall.ActiveUntilMs) return false;
        return KromedeFireWalls.InWall(ActiveFireWall, to) && !KromedeFireWalls.InWall(ActiveFireWall, from);
    }

    /// <summary>Periodic burn while standing inside a wall segment.</summary>
    public List<byte[]> TickFireWall(
        long now,
        IEnumerable<(int Key, Vector3d Position)> players,
        Func<int, int, List<byte[]>> hitPlayer)
    {
        var replies = new List<byte[]>();
        if (ActiveFireWall is not { } run) return replies;
        if (now > run.ActiveUntilMs)
        {
            ActiveFireWall = null;
            return replies;
        }
        KromedeFireWalls.Advance(run, now);
        if (now < run.NextDotMs) return replies;
        run.NextDotMs = now + KromedeFireWalls.DotIntervalMs;
        foreach (var (key, pos) in players)
        {
            if (!KromedeFireWalls.InWall(run, pos)) continue;
            var damage = KromedeFireWalls.DotDamage(rng);
            replies.AddRange(hitPlayer(key, damage));
        }
        return replies;
    }

}