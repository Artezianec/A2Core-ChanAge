using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class MechanicTelegraphTests
{
    const int CircleSkill = 11050000;

    static byte[] Hex(string h) => Convert.FromHexString(h);

    [Fact]
    public void RangeNotice_matches_packet_structs_builder()
    {
        var expectedBody = Hex("d1070000cc771b000300000000000000000000c842000048430000484200003442401f0000");
        var packet = PlayerPackets.SkillRangeNoticeFilterLocation(2001, 1800140, 3, 0, 0,
            new Vector3d(100, 200, 50), 45f, 8000);
        Assert.Equal(MechanicTelegraphs.RangeNoticeFilterLocationNt, GameSessionTestHelpers.Op(packet));
        Assert.Equal(expectedBody, packet.AsSpan(2).ToArray());
    }

    [Fact]
    public void Boss_slam_sends_marker_then_damage_after_wind_up()
    {
        GameSession.ConfigureCombatTelegraphs(new CombatTelegraphOptions { GroundMarkers = true });
        try
        {
            const int bossNpc = 990_001;
            var script = new BossScript("Test", "Test_Dungeon", "boss",
                Telegraph: new BossTelegraph(CircleSkill, 3000, 1200, 300, Damage: 1.0, RadiusFrom: "skill-areas", WindUpFrom: "OURS"));
            var boss = new Target
            {
                Key = 2001, NpcId = bossNpc, Name = "M_Test_Boss", Hp = 100_000, HpMax = 100_000, Level = 45, Position = Vector3d.Zero,
                WalkSpeed = 1, RunSpeed = 1, Radius = 50, SubType = "HeroMonster", Grade = 6,
            };
            var ai = new NpcAi(new Random(7), GameData.Embedded)
            {
                Bosses = new BossPhases(new Dictionary<int, BossScript> { [bossNpc] = script }),
            };
            var map = new Dictionary<int, Target> { [boss.Key] = boss };
            var players = new List<(int, Vector3d)> { (100001, new Vector3d(150, 0, 0)) };
            ai.Engage(map, boss, 0, attackerKey: 100001);
            var castPackets = new List<byte[]>();
            for (var t = 100L; t <= 1_600; t += 100)
                castPackets.AddRange(ai.Tick(map, players, t, (_, _) => []));

            Assert.Contains(castPackets, p => GameSessionTestHelpers.Op(p) == 0x3809);
            var markerIndex = castPackets.FindIndex(p => GameSessionTestHelpers.Op(p) == MechanicTelegraphs.RangeNoticeFilterLocationNt);
            var castingIndex = castPackets.FindIndex(p => GameSessionTestHelpers.Op(p) == 0x3809);
            Assert.True(markerIndex > castingIndex);

            var due = ai.TelegraphDueAt(boss);
            Assert.NotNull(due);
            var landPackets = ai.Tick(map, players, due!.Value + 50, (_, _) => []);
            Assert.Contains(landPackets, p => GameSessionTestHelpers.Op(p) == 0x3804);
        }
        finally
        {
            GameSession.ConfigureCombatTelegraphs(CombatTelegraphOptions.Default);
        }
    }

    [Fact]
    public void Ground_markers_off_sends_no_0x3848()
    {
        GameSession.ConfigureCombatTelegraphs(new CombatTelegraphOptions { GroundMarkers = false });
        try
        {
            var (ai, map) = NpcAreaSkillTests.Monster();
            ai.Engage(map, map[2001], 0, attackerKey: 100001);
            var cast = ai.Tick(map, [(100001, new Vector3d(300, 0, 0))], 100, (_, _) => []);
            Assert.DoesNotContain(cast, p => GameSessionTestHelpers.Op(p) == MechanicTelegraphs.RangeNoticeFilterLocationNt);
        }
        finally
        {
            GameSession.ConfigureCombatTelegraphs(CombatTelegraphOptions.Default);
        }
    }

    [Fact]
    public void MechanicTelegraphStarted_hook_may_append_packets()
    {
        GameSession.ConfigureCombatTelegraphs(CombatTelegraphOptions.Default);
        var (ai, map) = NpcAreaSkillTests.Monster();
        var extra = new byte[] { 0x01, 0x02 };
        ai.MechanicTelegraphStarted = _ => [extra];
        ai.Engage(map, map[2001], 0, attackerKey: 100001);
        var cast = ai.Tick(map, [(100001, new Vector3d(300, 0, 0))], 100, (_, _) => []);
        Assert.Contains(cast, p => p.AsSpan().SequenceEqual(extra));
    }
}