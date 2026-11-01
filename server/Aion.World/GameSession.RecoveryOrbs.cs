using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    public const int RecoveryOrbFirstKey = 960_000;

    sealed record ActiveRecoveryOrb(int Key, int EnvObjId, Vector3d Position, long ExpireAt, int BossKey);

    readonly List<ActiveRecoveryOrb> activeRecoveryOrbs = [];
    readonly Dictionary<int, long> recoveryOrbWaveCooldown = [];
    int nextRecoveryOrbKey = RecoveryOrbFirstKey;

    void WireRecoveryOrbHooks(NpcAi ai) =>
        ai.MonsterSkillStartHook = TrySpawnBossRecoveryOrbWave;

    void ClearRecoveryOrbHooks(NpcAi ai) => ai.MonsterSkillStartHook = null;

    IReadOnlyList<byte[]> TrySpawnBossRecoveryOrbWave(Target boss, int skillId, long now)
    {
        if (boss.OwnerKey != 0 || boss.Dead) return [];
        if (!RecoveryOrbs.WaveSkills.Contains(skillId)) return [];
        if (recoveryOrbWaveCooldown.TryGetValue(boss.Key, out var readyAt) && now < readyAt) return [];
        recoveryOrbWaveCooldown[boss.Key] = now + RecoveryOrbs.WaveSkillCooldownMs;
        return SpawnBossRecoveryOrbWave(boss, now);
    }

    List<byte[]> SpawnBossRecoveryOrbWave(Target boss, long now)
    {
        var replies = new List<byte[]>();
        var centre = boss.Position;
        for (var i = 0; i < RecoveryOrbs.WaveEnvIds.Length; i++)
        {
            var angle = i * 2 * Math.PI / RecoveryOrbs.WaveEnvIds.Length;
            var radius = 350.0;
            var envId = RecoveryOrbs.WaveEnvIds[i];
            var pos = centre with
            {
                X = centre.X + Math.Cos(angle) * radius,
                Y = centre.Y + Math.Sin(angle) * radius,
            };
            var key = nextRecoveryOrbKey++;
            var expire = now + RecoveryOrbs.LifetimeMs(envId);
            activeRecoveryOrbs.Add(new(key, envId, pos, expire, boss.Key));
            replies.Add(RecoveryOrbs.SpawnPacket(key, envId, pos, boss.Key, now));
        }
        return replies;
    }

    List<byte[]> TickRecoveryOrbs(long now)
    {
        if (activeRecoveryOrbs.Count == 0) return [];
        var replies = new List<byte[]>();
        for (var i = activeRecoveryOrbs.Count - 1; i >= 0; i--)
        {
            var orb = activeRecoveryOrbs[i];
            if (now >= orb.ExpireAt)
            {
                activeRecoveryOrbs.RemoveAt(i);
                replies.Add(GamePackets.EnvObjDespawn(orb.Key));
                continue;
            }
            if (position is not { } at || resources is null || dead) continue;
            var r = RecoveryOrbs.PickupRadius(orb.EnvObjId);
            if (at.DistanceSquared(orb.Position) > r * r) continue;
            activeRecoveryOrbs.RemoveAt(i);
            replies.Add(GamePackets.EnvObjDespawn(orb.Key));
            if (ApplyRecoveryOrbPickup(RecoveryOrbs.Pickup(orb.EnvObjId)))
                replies.Add(resources.Update());
        }
        return replies;
    }

    void ClearRecoveryOrbs()
    {
        activeRecoveryOrbs.Clear();
        recoveryOrbWaveCooldown.Clear();
        nextRecoveryOrbKey = RecoveryOrbFirstKey;
    }

    internal List<byte[]> SpawnBossRecoveryOrbWaveForTests(Target boss, long now) => SpawnBossRecoveryOrbWave(boss, now);

    internal void PlaceRecoveryOrbForTests(int key, int envObjId, Vector3d position, long expireAt, int bossKey) =>
        activeRecoveryOrbs.Add(new(key, envObjId, position, expireAt, bossKey));

    internal List<byte[]> TickRecoveryOrbsForTests(long now) => TickRecoveryOrbs(now);

    bool ApplyRecoveryOrbPickup(RecoveryOrbs.PickupEffect effect)
    {
        if (resources is null) return false;
        var changed = false;
        if (effect.HpRatioPermille > 0)
        {
            var add = Math.Max(1, resources.HpMax * effect.HpRatioPermille / 10_000);
            var next = Math.Min(resources.HpMax, resources.Hp + add);
            if (next != resources.Hp) { resources.Hp = next; changed = true; }
        }
        if (effect.MpRatioPermille > 0)
        {
            var add = Math.Max(1, resources.MpMax * effect.MpRatioPermille / 10_000);
            var next = Math.Min(resources.MpMax, resources.Mp + add);
            if (next != resources.Mp) { resources.Mp = next; changed = true; }
        }
        if (effect.SpRatioPermille > 0)
        {
            var add = Math.Max(1, resources.SpMax * effect.SpRatioPermille / 10_000);
            var next = Math.Min(resources.SpMax, resources.Sp + add);
            if (next != resources.Sp) { resources.Sp = next; changed = true; }
        }
        return changed;
    }
}
