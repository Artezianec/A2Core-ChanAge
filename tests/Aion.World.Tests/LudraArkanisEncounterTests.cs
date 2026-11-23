using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// The Abyssal Forge (Rahu, Ketu, Ludra) and Shattered Arkanis (Siliator, Ruthilis, Talisra) encounters (docs/LUDRA-ARKANIS.md), and the
/// small engine additions they rely on: <c>rule.npc_alive</c>, <c>damage.ratio</c> and the debuff hook.
/// </summary>
public sealed class LudraArkanisEncounterTests
{
    const int AddNpc = 2300910;

    static string Rule(string extra = "") => BossEngineTests.Encounter($$"""
        { "id": "aura", "when": { "type": "timer", "after_ms": 200, "every_ms": 1000 }, {{extra}}
          "do": [{ "type": "telegraph", "at": "boss", "shape": "circle", "radius": 1000, "decal": false,
                   "on_hit": [{ "type": "debuff", "debuff": "aura", "abnormal": 19105031, "duration_ms": 60000 }] }] }
        """);

    [Fact]
    public void Npc_alive_gates_a_rule_on_a_living_npc_and_the_debuff_hook_gets_the_abnormal()
    {
        var calls = new List<(int Player, int Abnormal, long Duration)>();
        var rig = new BossEngineTests.Rig(Rule($""" "npc_alive": {AddNpc}, """));
        rig.Ai.MechanicDebuffHook = (player, abnormal, _, duration) => { calls.Add((player, abnormal, duration)); return []; };
        rig.Tick(2000);
        Assert.Empty(calls);

        var add = rig.Add(9002, new Vector3d(2000, 0, 0));
        rig.Tick(2000);
        Assert.NotEmpty(calls);
        Assert.All(calls, c => Assert.Equal((BossEngineTests.Tank, 19105031, 60000L), c));

        add.Hp = 0;
        rig.Tick(200);
        var seen = calls.Count;
        rig.Tick(3000);
        Assert.Equal(seen, calls.Count);
    }

    [Fact]
    public void A_rule_without_npc_alive_fires_regardless()
    {
        var calls = 0;
        var rig = new BossEngineTests.Rig(Rule());
        rig.Ai.MechanicDebuffHook = (_, _, _, _) => { calls++; return []; };
        rig.Tick(2000);
        Assert.True(calls > 0);
    }

    [Fact]
    public void Damage_ratio_uses_the_client_max_hp_ratio_row_of_the_skill()
    {
        var rig = new BossEngineTests.Rig(BossEngineTests.Encounter("""
            { "id": "orbs", "when": { "type": "timer", "after_ms": 200 },
              "do": [{ "type": "telegraph", "at": "boss", "shape": "circle", "radius": 1000, "decal": false, "damage": { "skill": 1803203, "ratio": true } }] }
            """));
        rig.Ai.PlayerHpOf = _ => (20_000, 20_000);
        rig.Tick(1000);
        // 1803203: MaxHPRatio 7000 = 70% of the victim's max HP.
        Assert.Contains(rig.Hits, h => h.Key == BossEngineTests.Tank && h.Damage == 14_000);
    }

    [Theory]
    [InlineData(2301006), InlineData(2300282), InlineData(2301007), InlineData(2300283), InlineData(2301014), InlineData(2300290)]
    [InlineData(2330707), InlineData(2350707), InlineData(2353707), InlineData(2356707)]
    [InlineData(2330708), InlineData(2350708), InlineData(2353708), InlineData(2356708)]
    [InlineData(2330709), InlineData(2350709), InlineData(2353709), InlineData(2356709)]
    public void Every_boss_of_both_dungeons_has_an_authored_encounter(int npc)
    {
        Assert.Empty(BossEncounters.Embedded.Errors);
        Assert.True(BossEncounters.Embedded.Authored(npc));
        var encounter = BossEncounters.Embedded.Resolve(npc, BossPhases.Embedded)!;
        Assert.False(encounter.Compiled);
        Assert.NotEmpty(encounter.Rotation);
    }

    [Fact]
    public void The_abyss_stones_follow_the_difficulty_of_the_boss()
    {
        var normal = BossEncounters.Embedded.Resolve(2301006, BossPhases.Embedded)!;
        var hard = BossEncounters.Embedded.Resolve(2300282, BossPhases.Embedded)!;
        Assert.Equal([2301015, 2301016, 2301017], normal.Rules.Where(r => r.NpcAlive is not null).Select(r => r.NpcAlive!.Value));
        Assert.Equal([2300291, 2300292, 2300293], hard.Rules.Where(r => r.NpcAlive is not null).Select(r => r.NpcAlive!.Value));
    }

    [Fact]
    public void Ludra_has_the_official_ten_minute_berserk_and_thalythra_the_five_minute_one()
    {
        Assert.Equal(600_000, BossEncounters.Embedded.Resolve(2301014, BossPhases.Embedded)!.Rules.Single(r => r.When.Type == "enrage_timer").When.AfterMs!.Value.Value);
        foreach (var npc in new[] { 2330709, 2350709, 2353709, 2356709 })
            Assert.Equal(300_000, BossEncounters.Embedded.Resolve(npc, BossPhases.Embedded)!.Rules.Single(r => r.When.Type == "enrage_timer").When.AfterMs!.Value.Value);
    }

    /// <summary>1803000 is the existing compiled script's OURS slam (no client row), kept as it was.</summary>
    [Fact]
    public void Every_client_skill_an_encounter_casts_exists_as_an_npc_skill()
    {
        var data = GameData.Embedded;
        foreach (var npc in new[] { 2301006, 2301007, 2301014, 2330707, 2330708, 2330709, 2353707, 2353708, 2353709, 2356709 })
            foreach (var cast in BossEncounters.Embedded.Resolve(npc, BossPhases.Embedded)!.Rotation.Where(c => c.Skill != 1803000))
                Assert.True(data.NpcSkill(cast.Skill!.Value) is not null, $"{npc}: skill {cast.Skill} is not an NPC skill row");
    }

    [Theory]
    [InlineData(2301006), InlineData(2300282), InlineData(2301007), InlineData(2300283), InlineData(2301014), InlineData(2300290)]
    [InlineData(2330707), InlineData(2356708), InlineData(2330709), InlineData(2353709), InlineData(2356709)]
    public void The_real_encounter_runs_a_whole_fight_without_faults_and_hurts_the_party(int npc)
    {
        var home = npc switch
        {
            2301006 or 2300282 => new Vector3d(-75015, 104561, 45140),
            2301007 or 2300283 => new Vector3d(75010, 85042, 45640),
            _ => Vector3d.Zero,
        };
        var boss = new Target
        {
            Key = 9101, NpcId = npc, Name = "Boss", Hp = 5_000_000, HpMax = 5_000_000, Level = 70, Position = home,
            WalkSpeed = 0.001, RunSpeed = 0.001, Radius = 300, SubType = "HeroMonster", Grade = 2,
        };
        var map = new Dictionary<int, Target> { [boss.Key] = boss };
        var stones = new[] { 2301015, 2301016, 2301017, 2300291, 2300292, 2300293 };
        for (var i = 0; i < stones.Length; i++)
            map[9200 + i] = new Target { Key = 9200 + i, NpcId = stones[i], Name = "Stone", Hp = 1000, HpMax = 1000, Level = 70, Position = home, WalkSpeed = 0.001, RunSpeed = 0.001 };
        var players = new List<(int Key, Vector3d Position)>
            { (100001, new Vector3d(home.X + 500, home.Y + 0, home.Z)), (100002, new Vector3d(home.X + -400, home.Y + 300, home.Z)), (100003, new Vector3d(home.X + 0, home.Y + -600, home.Z)) };
        var ai = new NpcAi(new Random(3), GameData.Embedded) { Bosses = BossPhases.Embedded, Encounters = BossEncounters.Embedded, PlayerHpOf = _ => (30_000, 30_000) };
        var abnormals = new HashSet<int>();
        ai.MechanicDebuffHook = (_, abnormal, _, _) => { abnormals.Add(abnormal); return []; };
        ai.Engage(map, boss, 0, 100001);
        var damage = 0L;
        for (long now = 0; now < 130_000; now += 100)
        {
            if (now == 40_000) boss.Hp = (int)(boss.HpMax * 0.45);
            if (now == 80_000) boss.Hp = (int)(boss.HpMax * 0.18);
            ai.Tick(map, players, now + 100, (key, dmg) => { damage += dmg; return []; });
        }
        Assert.NotNull(ai.EncounterOf(boss));
        Assert.True(damage > 0, "the boss never hurt anyone");
        if (npc is 2301006 or 2301007) Assert.Contains(19105031, abnormals);
        if (npc is 2300282 or 2300283) Assert.Contains(19105031, abnormals);
    }
}
