using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Regular mobs: slow groggy fill, rare stagger; bosses use capture 233052 separately.</summary>
public sealed class StaggerNormalMobGaugeTests
{
    const long Now = 1_760_320_000_000;
    const int Combustion = 16_040_000;

    static byte[] Cast(int targetKey) =>
        new PacketWriter().U16(0x3800).U16(0).I32(Combustion).U8(2).Varint(targetKey).F32(0).F32(100).F32(200).F32(300).ToArray();

    static async Task<GameSession> EnterElementalistAsync()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { World = new SharedWorld(GameData.Embedded), CharacterLevel = 45, WorldId = store.WorldId },
            new Random(11)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("StaggerNorm").U8(1).U32(6).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    [Fact]
    public void Grade_3_normal_monster_has_no_groggy_on_map_load()
    {
        var cherub = Combat.MapTargets(GameSessionTestHelpers.LiveGameData, 100010).Values.First(t => t.NpcId == 2290000);
        Assert.Equal("NormalMonster", cherub.SubType);
        Assert.False(StaggerGaugeCombat.HasBossGauge(cherub));
        Assert.Equal(0, cherub.GroggyMax);
        Assert.Equal(0, cherub.StaggerGaugeMax);
    }

    [Fact]
    public void Combustion_adds_at_most_one_gauge_per_hit_on_regular_mob()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var normal = Combat.MapTargets(data, 100010).Values.First(t => t.NpcId == 2290000);
        StaggerGaugeCombat.InitRegularMobGaugeForTests(normal);
        var gauge = StaggerGaugeCombat.GaugeDamage(data, Combustion, normal, hitDamage: 50);
        Assert.Equal(StaggerGaugeCombat.RegularMobMaxGroggyDrainPerHit, gauge);
    }

    [Fact]
    public async Task Field_normal_does_not_stagger_break_before_death_from_typical_rotation()
    {
        var session = await EnterElementalistAsync();
        const int map = 1010;
        session.Teleport(map, GameSessionTestHelpers.MapStartOrDefault(map), Now + 1000);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 1010);
        await session.TickAsync(Now + 1020);
        var prey = session.Targets!.Values.First(t => t.SubType == "NormalMonster" && !t.Dead);
        prey.Hp = prey.HpMax;
        var t = Now + 60_000;
        await session.HandleAsync(Cast(prey.Key), t);
        var hits = await GameSessionTestHelpers.AfterSkillHitsAsync(session, t, NpcAi.HitDelayMs + 80);
        prey = session.Targets!.Values.First(x => x.Key == prey.Key);
        Assert.Equal(0, prey.StaggerUntil);
        if (prey.StaggerGauge > 0)
            Assert.True(prey.StaggerGauge < StaggerGaugeCombat.DefaultGaugeMax);
    }

    [Fact]
    public void Groggy_drain_packet_uses_trailer_02_per_capture_233052()
    {
        var boss = new Target
        {
            Key = 898702, Name = "Boss", HpMax = 3000, Hp = 3000, SubType = "HeroMonster", Grade = 5,
        };
        StaggerGaugeCombat.InitBossGauge(boss);
        var pkt = GamePackets.UpdateGroggyInfo(boss.Key, 100, 97);
        Assert.Equal(0xe005, pkt[0] | (pkt[1] << 8));
        Assert.Equal((byte)2, pkt[^1]);
    }
}
