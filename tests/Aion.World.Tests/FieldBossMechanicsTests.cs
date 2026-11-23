using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>The field and rift boss encounters (tools/data/boss-mechanics/field_*.json, abyss_rift_wh6.json) run on the generic engine.</summary>
public sealed class FieldBossMechanicsTests
{
    const int BossKey = 9101, Tank = 100001;

    sealed class Rig
    {
        public NpcAi Ai { get; } = new(new Random(11), GameData.Embedded);
        public Dictionary<int, Target> Map { get; } = [];
        public Target Boss { get; }
        public long Now { get; private set; }

        public Rig(int npc)
        {
            var spawn = GameData.Embedded.NpcFromTemplate(npc, "boss", [0, 0, 0], 0)!;
            Boss = new Target
            {
                Key = BossKey, NpcId = npc, Name = spawn.Name, Hp = 1_000_000, HpMax = 1_000_000, Level = spawn.Level,
                Position = Vector3d.Zero, WalkSpeed = 0.001, RunSpeed = 0.001, Radius = 150, SubType = "HeroMonster", Grade = spawn.Grade,
               
            };
            Map[BossKey] = Boss;
            Ai.Engage(Map, Boss, 0, Tank);
        }

        public void Tick(long ms)
        {
            for (var end = Now + ms; Now < end;)
            {
                Now += 100;
                Ai.Tick(Map, [(Tank, new Vector3d(200, 0, 0))], Now, (_, _) => []);
            }
        }

        public void Hp(double percent) => Boss.Hp = (int)(Boss.HpMax * percent / 100);

        public bool WindsUp(int skill, long limit)
        {
            for (var waited = 0L; waited < limit; waited += 100)
            {
                Tick(100);
                if (Ai.FightOf(Boss)?.Winding is { } w && w.Slam.Skill == skill) return true;
            }
            return false;
        }
    }

    [Theory]
    [InlineData(2101074)]
    [InlineData(2400800)]
    public void Gartua_runs_the_authored_rotation_and_the_long_odyle_absorption_cast(int npc)
    {
        var rig = new Rig(npc);
        rig.Tick(500);
        Assert.Equal("field_gartua", rig.Ai.EncounterOf(rig.Boss)!.Id);
        Assert.True(rig.WindsUp(1601160, 30_000), "Annihilation (client cooldown 15 s)");
        Assert.True(rig.WindsUp(1601150, 30_000), "Compression (stun 2.5 s)");
        Assert.True(rig.WindsUp(1601205, 90_000), "Odyle Absorption (15 s cast)");
        Assert.InRange(rig.Ai.FightOf(rig.Boss)!.Winding!.Value.DueMs - rig.Now, 15_000, 16_000);   // the client's 15 s cast
    }

    [Fact]
    public void Turncoat_Ducal_casts_smite_ground_and_the_greatsword_plunge()
    {
        var rig = new Rig(2600521);
        rig.Tick(500);
        Assert.Equal("field_ducal", rig.Ai.EncounterOf(rig.Boss)!.Id);
        Assert.True(rig.WindsUp(1808250, 30_000), "Smite Ground");
        Assert.True(rig.WindsUp(1808270, 40_000), "Greatsword Plunge");
    }

    [Fact]
    public void A_rift_boss_is_invulnerable_for_25_seconds_twice_and_no_more()
    {
        var rig = new Rig(2670118);
        rig.Tick(500);
        Assert.Equal("abyss_rift_wh6", rig.Ai.EncounterOf(rig.Boss)!.Id);
        Assert.False(rig.Boss.Immune);
        rig.Hp(60);
        rig.Tick(300);
        Assert.True(rig.Boss.Immune);
        rig.Tick(24_000);
        Assert.True(rig.Boss.Immune);
        rig.Tick(1_500);
        Assert.False(rig.Boss.Immune);
        rig.Hp(30);
        rig.Tick(300);
        Assert.True(rig.Boss.Immune);
        rig.Tick(26_000);
        Assert.False(rig.Boss.Immune);
        rig.Hp(10);
        rig.Tick(2_000);
        Assert.False(rig.Boss.Immune);
    }
}
