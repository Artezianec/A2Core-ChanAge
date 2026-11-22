using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class NpcAi
{
    public sealed class ExpeditionState
    {
        public Vector3d? Pillar;
        public int? SoakMarkPlayer;
        public bool AuldorOuterDot;
        public long OuterDotNextMs;
        public long RingsUntilMs;
        public int RingPulse;
        public long RingNextPulseMs;
        public Vector3d OrbCentre;
        public int BlueLightHits;
        public long BlueLightEndsMs;
        public long CloneLineNextMs;
        public Vector3d ArenaCentre;
        public bool WhirlpoolsActive;
        public long WhirlpoolNextMs;
        public List<Vector3d> WhirlpoolCentres { get; } = [];
    }

    readonly List<(int Key, Vector3d To)> edgeSnaps = [];
    public IReadOnlyList<(int Key, Vector3d To)> ConsumeEdgeSnaps()
    {
        if (edgeSnaps.Count == 0) return [];
        var copy = edgeSnaps.ToList();
        edgeSnaps.Clear();
        return copy;
    }

    public ExpeditionState? Expedition { get; private set; }
    ExpeditionState EnsureExpedition() => Expedition ??= new();

    public Func<BossMechanicTelegraphEventArgs, IReadOnlyList<byte[]>>? MechanicTelegraphStartedHook;

    List<byte[]> RaiseTelegraph(BossMechanicTelegraphEventArgs args) =>
        MechanicTelegraphStartedHook is { } hook ? [.. hook(args)] : [];

    internal List<byte[]> OnBossPhase(Target boss, Brain brain, BossPhase phase, long now)
    {
        var replies = new List<byte[]>();
        var state = EnsureExpedition();
        state.ArenaCentre = boss.Position;
        if (phase.Name == "clones" && KromedeFireWalls.IsKromedeBoss(boss.NpcId))
            summonRequests.Add(new SummonRequest(boss, brain.Fight!, new BossAdds(boss.NpcId, 2, 1500), Victim(brain)));
        if (ExpeditionBossMechanics.NuakumBossNpcIds.Contains(boss.NpcId) && phase.Name == "hunt")
            summonRequests.Add(new SummonRequest(boss, brain.Fight!, new BossAdds(ExpeditionBossMechanics.NuakumCloneNpc(), 1, 800), Victim(brain)));
        var mechanic = ExpeditionBossMechanics.MechanicForPhase(phase.Name);
        if (mechanic is null) return replies;
        switch (mechanic)
        {
            case "berk_pillar_cover":
                state.Pillar = ExpeditionBossMechanics.PillarAt(boss, VictimPosition(brain) ?? boss.Position);
                replies.AddRange(RaiseTelegraph(ExpeditionBossMechanics.Telegraph(boss, mechanic, 1800050, state.Pillar.Value, now, now + 12_000)));
                break;
            case "nuakum_blue_light":
                state.OrbCentre = boss.Position;
                state.BlueLightHits = 0;
                state.BlueLightEndsMs = now + ExpeditionBossMechanics.NuakumOrbConfig().WindowMs;
                replies.AddRange(RaiseTelegraph(ExpeditionBossMechanics.Telegraph(boss, mechanic, 0, state.OrbCentre, now, state.BlueLightEndsMs)));
                break;
            case "auldor_tempest_soak":
                state.SoakMarkPlayer = Victim(brain);
                break;
        }
        if (ExpeditionBossMechanics.AuldorBossNpcIds.Contains(boss.NpcId) && phase.At <= 30)
            state.AuldorOuterDot = true;
        if (ExpeditionBossMechanics.BakarmaBossNpcIds.Contains(boss.NpcId) && phase.Name == "wave_of_death" && phase.At <= 30)
        {
            state.WhirlpoolsActive = true;
            state.WhirlpoolNextMs = now;
            var off = 1_200.0;
            state.WhirlpoolCentres.Clear();
            state.WhirlpoolCentres.Add(boss.Position with { X = boss.Position.X + off });
            state.WhirlpoolCentres.Add(boss.Position with { X = boss.Position.X - off });
        }
        return replies;
    }

    internal void ClearExpeditionMechanics() => Expedition = null;

    internal bool SlamSkipsPlayer(Target boss, BossTelegraph slam, Vector3d playerAt)
    {
        if (slam.Mechanic is "berk_pillar_cover" or "wave_of_fury" && Expedition?.Pillar is { } pillar)
            return ExpeditionBossMechanics.InPillarCover(boss, pillar, playerAt);
        return false;
    }

    internal List<byte[]> SlamMechanicLand(Target boss, Brain brain, BossTelegraph slam, Vector3d centre, double yaw, long now)
    {
        var replies = new List<byte[]>();
        if (slam.Mechanic is "auldor_tempest_soak" or "tempest_soak" && Expedition?.SoakMarkPlayer is { } mark && currentPlayers is not null)
        {
            var cfg = ExpeditionBossMechanics.AuldorConfig();
            var markAt = currentPlayers?.FirstOrDefault(p => p.Key == mark).Position ?? centre;
            var stackers = Math.Max(1, currentPlayers?.Count(p => p.Position.DistanceSquared(markAt) <= cfg.StackRadius * cfg.StackRadius) ?? 1);
            var skill = data?.NpcSkill(slam.Skill);
            var raw = Damage(boss, skill ?? new NpcSkill(0, 0, 1, 1, 0, 50_000, 50_000), PlayerLevelOf?.Invoke(mark) ?? boss.Level);
            var each = Math.Max(1, (int)Math.Round((double)raw / Math.Max(1, stackers)));
            if (currentHitPlayer is { } hit && currentPlayers is not null)
            {
                foreach (var (key, pos) in currentPlayers)
                {
                    if (pos.DistanceSquared(markAt) > cfg.StackRadius * cfg.StackRadius) continue;
                    replies.AddRange(hit(key, each));
                }
            }
            return replies;
        }
        if (KromedeFireWalls.IsFireWallTelegraph(slam)) FireWallActivated(now);
        return replies;
    }

    public List<byte[]> TickExpeditionMechanics(
        long now,
        IEnumerable<(int Key, Vector3d Position)> players,
        Func<int, int, List<byte[]>> hitPlayer)
    {
        var replies = new List<byte[]>();
        replies.AddRange(TickFireWall(now, players, hitPlayer));
        if (Expedition is not { } state) return replies;
        var cfgA = ExpeditionBossMechanics.AuldorConfig();
        if (state.AuldorOuterDot && now >= state.OuterDotNextMs)
        {
            state.OuterDotNextMs = now + cfgA.OuterDotIntervalMs;
            var centre = state.ArenaCentre;
            if (centre == default) centre = players.FirstOrDefault().Position;
            foreach (var (key, pos) in players)
            {
                if (!ExpeditionBossMechanics.InOuterArenaDanger(centre, pos)) continue;
                var dmg = rng.Next(cfgA.OuterDotDamageMin, cfgA.OuterDotDamageMax + 1);
                replies.AddRange(hitPlayer(key, dmg));
            }
        }
        var cfgBWhirl = ExpeditionBossMechanics.BakarmaConfig();
        if (state.WhirlpoolsActive && now >= state.WhirlpoolNextMs)
        {
            state.WhirlpoolNextMs = now + cfgBWhirl.WhirlpoolIntervalMs;
            var r2 = cfgBWhirl.WhirlpoolRadius * cfgBWhirl.WhirlpoolRadius;
            foreach (var (key, pos) in players)
            {
                if (state.WhirlpoolCentres.All(c => pos.DistanceSquared(c) > r2)) continue;
                var dmg = rng.Next(cfgBWhirl.WhirlpoolDamageMin, cfgBWhirl.WhirlpoolDamageMax + 1);
                replies.AddRange(hitPlayer(key, dmg));
            }
        }
        if (state.RingNextPulseMs > 0 && now >= state.RingNextPulseMs && now < state.RingsUntilMs)
        {
            var cfgB = ExpeditionBossMechanics.BakarmaConfig();
            var band = cfgB.Rings[Math.Min(state.RingPulse, cfgB.Rings.Length - 1)];
            state.RingPulse++;
            state.RingNextPulseMs = now + cfgB.PulseMs;
            var ringCentre = state.ArenaCentre == default ? players.FirstOrDefault().Position : state.ArenaCentre;
            foreach (var (key, pos) in players)
            {
                var dist = Math.Sqrt(pos.DistanceSquared(ringCentre));
                if (dist >= band.SafeInner && dist <= band.SafeOuter) continue;
                var dmg = rng.Next(cfgB.DotDamageMin, cfgB.DotDamageMax + 1);
                replies.AddRange(hitPlayer(key, dmg));
            }
        }
        if (state.BlueLightEndsMs > now)
        {
            var orb = ExpeditionBossMechanics.NuakumOrbConfig();
            foreach (var (key, pos) in players)
            {
                if (pos.DistanceSquared(state.OrbCentre) <= orb.OrbRadius * orb.OrbRadius)
                    state.BlueLightHits = Math.Min(orb.HitsRequired, state.BlueLightHits + 1);
            }
        }
        foreach (var fight in brains.Values.Select(b => b.Fight).Where(f => f is not null)!)
        {
            if (now < state.CloneLineNextMs) continue;
            var cloneLine = ExpeditionBossMechanics.KromedeCloneConfig();
            var cloneKeys = fight!.Adds;
            if (cloneKeys.Count == 0) continue;
            state.CloneLineNextMs = now + cloneLine.EveryMs;
            foreach (var (key, pos) in players)
            {
                if (current?.GetValueOrDefault(cloneKeys[0]) is not { } clone) continue;
                if (!InSlam(clone, new BossTelegraph(cloneLine.Skill, 1, 1000, 1500), clone.Position, clone.Direction, pos)) continue;
                var skill = data?.NpcSkill(cloneLine.Skill);
                var dmg = Math.Max(1, (int)Math.Round((double)Damage(clone, skill ?? new NpcSkill(0, 0, 1, 1, 0, 8000, 12000), 60)));
                replies.AddRange(hitPlayer(key, dmg));
            }
        }
        return replies;
    }
}