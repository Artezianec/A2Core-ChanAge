using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class BossRecoveryOrbTests
{
    const long Now = 1_760_300_000_000;

    [Fact]
    public void Boss_recovery_orb_spawn_matches_observed_53_byte_layout()
    {
        var pos = new Vector3d(100f, 200f, 50f);
        var pkt = GamePackets.BossRecoveryOrbSpawn(9217, 1_000_277, pos, 898702, 0f, 9217);
        Assert.Equal(53, pkt.Length);
        Assert.Equal(0x3634, (ushort)(pkt[0] | (pkt[1] << 8)));
        Assert.Contains(BitConverter.GetBytes(1_000_277), pkt.AsSpan());
        Assert.Contains((byte)28, pkt);
        Assert.Contains((byte)6, pkt);
    }

    [Fact]
    public void Pickup_config_maps_red_blue_green_to_hp_mp_sp()
    {
        var red = RecoveryOrbs.Pickup(1_000_277);
        var blue = RecoveryOrbs.Pickup(1_000_278);
        var green = RecoveryOrbs.Pickup(1_000_279);
        Assert.True(red.HpRatioPermille > 0 && red.MpRatioPermille == 0 && red.SpRatioPermille == 0);
        Assert.True(blue.MpRatioPermille > 0 && blue.HpRatioPermille == 0 && blue.SpRatioPermille == 0);
        Assert.True(green.SpRatioPermille > 0 && green.HpRatioPermille == 0 && green.MpRatioPermille == 0);
    }

    [Fact]
    public async Task Stepping_on_red_orb_restores_hp_and_despawns()
    {
        var store = new FakeCharacterStore { Progress = (10, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        var orbPos = new Vector3d(7309.9, -6974.4, 84.0);
        session.Teleport(210016, orbPos, Now + 10);
        session.SetResource('H', 100);
        var mpBefore = session.Resource.Mp;
        session.PlaceRecoveryOrbForTests(9217, 1_000_277, orbPos, Now + 30_000, bossKey: 5001);
        var pickup = session.TickRecoveryOrbsForTests(Now + 100);
        Assert.Contains(pickup, p => GameSessionTestHelpers.Op(p) == 0x3635);
        Assert.Contains(pickup, p => GameSessionTestHelpers.Op(p) == 0x8d00);
        Assert.True(session.Resource.Hp > 100);
        Assert.Equal(mpBefore, session.Resource.Mp);
    }

    [Fact]
    public async Task Stepping_on_blue_orb_restores_mp()
    {
        var store = new FakeCharacterStore { Progress = (10, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        var orbPos = new Vector3d(7309.9, -6974.4, 84.0);
        session.Teleport(210016, orbPos, Now + 10);
        var mpBefore = session.Resource.Mp;
        session.SetResource('M', Math.Max(1, mpBefore / 4));
        session.PlaceRecoveryOrbForTests(9218, 1_000_278, orbPos, Now + 30_000, bossKey: 5001);
        var pickup = session.TickRecoveryOrbsForTests(Now + 100);
        Assert.Contains(pickup, p => GameSessionTestHelpers.Op(p) == 0x3635);
        Assert.True(session.Resource.Mp > Math.Max(1, mpBefore / 4));
    }

    [Fact]
    public void Boss_skill_wave_spawns_three_recovery_orbs()
    {
        var session = GameSessionTestHelpers.Session(new FakeCharacterStore(), level: 45);
        var boss = new Target
        {
            Key = 5001, Name = "Boss", NpcId = 2701034, Position = Vector3d.Zero, Hp = 1000, HpMax = 1000,
            SubType = "HeroMonster", Grade = 5,
        };
        var spawn = session.SpawnBossRecoveryOrbWaveForTests(boss, Now);
        Assert.Equal(3, spawn.Count);
        Assert.All(spawn, p => Assert.Equal(0x3634, GameSessionTestHelpers.Op(p)));
        foreach (var envId in RecoveryOrbs.WaveEnvIds)
            Assert.Contains(spawn, p => p.AsSpan().IndexOf(BitConverter.GetBytes(envId)) >= 0);
    }
}
