using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Boss phases, adds, slams and the frenzy (NpcAi.Bosses, boss-phases.json; ported from ckrealm/A2Core c299686, AGPL-3.0).</summary>
public sealed class BossPhaseTests
{
    // Circle 500 around the caster in skill-areas.json (the same skill NpcAreaSkillTests uses).
    const int CircleSkill = 11050000;
    const int BossKey = 2001, BossNpc = 990_001, AddNpc = 2290017;
    const int Tank = 100001, Healer = 100002, Faraway = 100003;

    static (NpcAi Ai, Dictionary<int, Target> Map, Target Boss) Boss(BossScript script, BossOptions? rules = null)
    {
        var boss = new Target
        {
            Key = BossKey, NpcId = BossNpc, Name = "M_Test_Boss", Hp = 100_000, HpMax = 100_000, Level = 45, Position = Vector3d.Zero,
            WalkSpeed = 1, RunSpeed = 1, Radius = 50, SubType = "HeroMonster", Grade = 6,
        };
        var ai = new NpcAi(new Random(7), GameData.Embedded)
        {
            Bosses = new BossPhases(new Dictionary<int, BossScript> { [BossNpc] = script }),
            BossRules = rules ?? BossOptions.Default,
        };
        return (ai, new Dictionary<int, Target> { [BossKey] = boss }, boss);
    }

    static readonly List<(int Key, Vector3d Position)> Players =
        [(Tank, new Vector3d(150, 0, 0)), (Healer, new Vector3d(-150, 0, 0)), (Faraway, new Vector3d(4000, 0, 0))];

    static List<byte[]> Run(NpcAi ai, Dictionary<int, Target> map, long from, long to, List<int> damaged)
    {
        var replies = new List<byte[]>();
        for (var t = from; t <= to; t += 100)
            replies.AddRange(ai.Tick(map, Players, t, (key, _) => { damaged.Add(key); return []; }));
        return replies;
    }

    [Fact]
    public void Embedded_scripts_load_with_client_shapes_and_marked_invented_values()
    {
        var all = BossPhases.Embedded.All.ToList();
        Assert.Equal(68, all.Count);
        var slams = all.SelectMany(b => new[] { b.Script.Telegraph }.Concat(b.Script.Telegraphs ?? []).Concat((b.Script.Phases ?? []).Select(p => p.Telegraph))).OfType<BossTelegraph>().ToList();
        Assert.All(slams, s => Assert.Contains(s.RadiusFrom, new[] { "skill-areas", "OURS" }));
        Assert.All(slams, s => Assert.Contains(s.WindUpFrom, new[] { "client cast", "OURS" }));
        Assert.All(slams.Where(s => s.RadiusFrom == "skill-areas"), s => Assert.True(SkillAreas.For(s.Skill) is { IsArea: true }));
        Assert.True(slams.Count(s => s.RadiusFrom == "skill-areas") > slams.Count(s => s.RadiusFrom == "OURS"));
        Assert.All(slams.Where(s => s.Warning is { Length: > 0 }), s => Assert.False(string.IsNullOrEmpty(s.WarningRu)));
        // Every add is a real NPC template (the host spawns them from NpcData).
        foreach (var adds in all.SelectMany(b => b.Script.Phases ?? []).Select(p => p.Adds).OfType<BossAdds>())
            Assert.NotNull(GameData.Embedded.NpcFromTemplate(adds.Npc, "", [0, 0, 0], 0));
        Assert.NotNull(BossPhases.Embedded.Wave(600092, "FierceHornCave_Wave_01"));
    }

    [Fact]
    public void A_phase_at_its_HP_threshold_announces_hits_harder_and_calls_its_adds()
    {
        var script = new BossScript("Test", "Test_Dungeon", "boss", Phases:
        [
            new BossPhase(50, "half", Damage: 1.5, Haste: 0.8, Adds: new BossAdds(AddNpc, 2, 300), Notice: "The boss calls for help!", NoticeRu: "Босс зовёт на помощь!"),
            new BossPhase(20, "last", Damage: 2.0, Haste: 0.6),
        ]);
        var (ai, map, boss) = Boss(script);
        var damaged = new List<int>();
        ai.Engage(map, boss, 0, Tank);
        Run(ai, map, 100, 500, damaged);
        var fight = ai.FightOf(boss);
        Assert.NotNull(fight);
        Assert.Equal(0, fight!.Reached);
        Assert.Empty(ai.TakeSummons());

        boss.Hp = 45_000;
        var replies = Run(ai, map, 600, 700, damaged);
        Assert.Equal(1, fight.Reached);
        Assert.Equal(1.5, fight.Damage);
        Assert.Equal(0.8, fight.Haste);
        var notice = GameSession.Notice("The boss calls for help!", "Босс зовёт на помощь!", 22, "FFB040");
        Assert.Contains(replies, p => p.AsSpan().SequenceEqual(notice));
        var request = Assert.Single(ai.TakeSummons());
        Assert.Equal(2, request.Adds.Count);
        Assert.Equal(Tank, request.VictimKey);

        // The host places the adds; they belong to the fight and go when the boss dies.
        var adds = BossSummons.Spawn(GameData.Embedded, map, request.Adds.Npc, boss.Position, request.Adds.Count, request.Adds.Radius);
        Assert.Equal(2, adds.Count);
        Assert.All(adds, a => Assert.True(BossSummons.IsSummon(a.Key)));
        request.Fight.Adds.AddRange(adds.Select(a => a.Key));

        boss.Hp = 10_000;
        Run(ai, map, 800, 900, damaged);
        Assert.Equal(2, fight.Reached);
        Assert.Equal(2.0, fight.Damage);

        boss.Hp = 0;
        var gone = ai.Killed(boss);
        Assert.Null(ai.FightOf(boss));
        Assert.All(adds, a => Assert.True(a.Dead && a.CorpseRemoved));
        Assert.Equal(2, gone.Count(p => p.AsSpan().SequenceEqual(GamePackets.Death(adds[0].Key, 7)) || p.AsSpan().SequenceEqual(GamePackets.Death(adds[1].Key, 7))));
    }

    [Fact]
    public void A_telegraphed_slam_winds_up_then_hits_everyone_inside_its_client_shape_and_misses_the_rest()
    {
        Assert.True(SkillAreas.For(CircleSkill) is { IsArea: true, OnTarget: false });
        var script = new BossScript("Test", "Test_Dungeon", "boss",
            Telegraph: new BossTelegraph(CircleSkill, 3000, 1200, 300, Damage: 1.0, Warning: "Slam!", WarningRu: "Удар!", RadiusFrom: "skill-areas", WindUpFrom: "OURS"));
        var (ai, map, boss) = Boss(script);
        var damaged = new List<int>();
        ai.Engage(map, boss, 0, Tank);
        var replies = Run(ai, map, 100, 1_600, damaged);
        var due = ai.TelegraphDueAt(boss);
        Assert.NotNull(due);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3809);
        Assert.Contains(replies, p => p.AsSpan().SequenceEqual(GameSession.Notice("Slam!", "Удар!", 22, "FF8040")));
        Assert.DoesNotContain(Healer, damaged);   // nothing lands during the wind-up

        Run(ai, map, 1_700, due!.Value + 200, damaged);
        Assert.Equal(1, ai.SlamsLanded);
        Assert.Contains(Healer, damaged);         // inside the circle, not the boss's victim: only the slam reaches them
        Assert.DoesNotContain(Faraway, damaged);
    }

    [Fact]
    public void The_frenzy_comes_after_its_time_and_speeds_the_boss_up()
    {
        var script = new BossScript("Test", "Test_Dungeon", "boss",
            Frenzy: new BossScriptFrenzy(2_000, 3.0, 0.5, "The boss is enraged!", "Босс в ярости!"));
        var (ai, map, boss) = Boss(script);
        var damaged = new List<int>();
        ai.Engage(map, boss, 0, Tank);
        Run(ai, map, 100, 1_500, damaged);
        var fight = ai.FightOf(boss)!;
        Assert.False(fight.Frenzied);
        var replies = Run(ai, map, 1_600, 2_600, damaged);
        Assert.True(fight.Frenzied);
        Assert.Equal(3.0, fight.Damage);
        Assert.Equal(0.5, fight.Haste);
        Assert.Contains(replies, p => p.AsSpan().SequenceEqual(GameSession.Notice("The boss is enraged!", "Босс в ярости!", 24, "FF4040")));
    }

    [Fact]
    public void FrenzySeconds_overrides_the_script_and_Frenzy_false_turns_it_off()
    {
        var script = new BossScript("Test", "Test_Dungeon", "boss", Frenzy: new BossScriptFrenzy(60_000, 3.0, 0.5));
        var (ai, map, boss) = Boss(script, new BossOptions { FrenzySeconds = 1 });
        ai.Engage(map, boss, 0, Tank);
        Run(ai, map, 100, 1_500, []);
        Assert.True(ai.FightOf(boss)!.Frenzied);

        var (off, offMap, offBoss) = Boss(script, new BossOptions { Frenzy = false, FrenzySeconds = 1 });
        off.Engage(offMap, offBoss, 0, Tank);
        Run(off, offMap, 100, 1_500, []);
        Assert.False(off.FightOf(offBoss)!.Frenzied);
    }

    [Fact]
    public void A_dungeon_whose_frenzy_the_patch_notice_sets_never_stacks_the_script_frenzy()
    {
        // Urugugu Canyon Hard: Patch20261028.FrenzyMs already doubles its bosses' hits after 4 minutes (GameSession.DungeonBossDamage).
        Assert.Contains(MapEventTables.Embedded.AllDungeons, d => d.Value.Name == "Urugugu_Canyon_Hard" && Patch20261028.FrenzyMs(d.Value) > 0);
        var script = new BossScript("Test", "Urugugu_Canyon_Hard", "boss", Frenzy: new BossScriptFrenzy(1_000, 3.0, 0.5));
        var (ai, map, boss) = Boss(script);
        ai.Engage(map, boss, 0, Tank);
        Run(ai, map, 100, 2_000, []);
        Assert.False(ai.FightOf(boss)!.Frenzied);
    }

    [Fact]
    public void Official_Berk_has_no_invented_adds_and_uses_a_stagger_check_at_half_health()
    {
        var berk = BossPhases.Embedded.Of(2300171);
        Assert.NotNull(berk);
        Assert.Equal("official-overlay", berk!.Source);
        Assert.Equal("Ultimate Berk", berk.Display);
        Assert.Equal(new[] { 75, 50, 25 }, berk.Ordered.Select(p => p.At).ToArray());
        Assert.DoesNotContain(berk.Ordered, p => p.Adds is not null);
        Assert.Equal(1800060, berk.Rotation[0].Skill);
        var extinction = Assert.Single(berk.Ordered, p => p.At == 50).Telegraph;
        Assert.NotNull(extinction);
        Assert.Equal(1800140, extinction!.Skill);
        Assert.True(extinction.Once);
        Assert.True(extinction.StaggerCheck);
        Assert.True(extinction.Land);
        Assert.Equal(300_000, berk.Frenzy?.AfterMs);
    }

    [Fact]
    public void A_stagger_check_is_cancelled_when_the_boss_is_held_and_is_not_retried()
    {
        var script = new BossScript("Test", "Test_Dungeon", "boss",
            Telegraph: new BossTelegraph(CircleSkill, 3000, 1200, 300, Warning: "Fill!", WarningRu: "Бей!", RadiusFrom: "skill-areas", Once: true, StaggerCheck: true));
        var (ai, map, boss) = Boss(script);
        var damaged = new List<int>();
        ai.Engage(map, boss, 0, Tank);
        Run(ai, map, 100, 1_600, damaged);
        Assert.NotNull(ai.TelegraphDueAt(boss));
        Assert.Empty(damaged);

        boss.Held = true;
        Run(ai, map, 1_700, 2_000, damaged);
        Assert.Null(ai.TelegraphDueAt(boss));
        Assert.Empty(damaged);
        Assert.Equal(0, ai.SlamsLanded);
        // A cancelled stagger-check is not retried as the same wipe (NextSlamMs jumps by the filler interval).
        Run(ai, map, 2_100, 4_000, damaged);
        Assert.Equal(0, ai.SlamsLanded);
    }

    [Fact]
    public void A_no_land_telegraph_winds_up_but_hits_nobody()
    {
        var script = new BossScript("Test", "Test_Dungeon", "boss",
            Telegraph: new BossTelegraph(CircleSkill, 3000, 1200, 300, Warning: "Walls!", WarningRu: "Стены!", RadiusFrom: "skill-areas", Once: true, Land: false));
        var (ai, map, boss) = Boss(script);
        var damaged = new List<int>();
        ai.Engage(map, boss, 0, Tank);
        var replies = Run(ai, map, 100, 1_600, damaged);
        var due = ai.TelegraphDueAt(boss);
        Assert.NotNull(due);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3809);
        Run(ai, map, 1_700, due!.Value + 200, damaged);
        Assert.Equal(1, ai.SlamsLanded);
        Assert.Empty(damaged);
    }

    [Fact]
    public void Phases_off_leaves_the_boss_unscripted()
    {
        var script = new BossScript("Test", "Test_Dungeon", "boss", Phases: [new BossPhase(90, "p", Damage: 2)]);
        var (ai, map, boss) = Boss(script, new BossOptions { Phases = false });
        ai.Engage(map, boss, 0, Tank);
        boss.Hp = 10;
        Run(ai, map, 100, 500, []);
        Assert.Null(ai.FightOf(boss));
    }
}
