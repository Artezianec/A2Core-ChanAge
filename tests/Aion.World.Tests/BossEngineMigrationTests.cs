using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// The fights migrated to the engine (tools/data/boss-mechanics and compiled boss-phases.json scripts) against the legacy
/// hand-coded path (Aion:Bosses:Engine=false): the same scenario on both must give the same observable fight.
/// </summary>
public sealed class BossEngineMigrationTests
{
    const int BossKey = 9101, Tank = 100001, P2 = 100002, P3 = 100003;

    sealed class Side
    {
        public NpcAi Ai { get; }
        public Dictionary<int, Target> Map { get; } = [];
        public Target Boss { get; }
        public List<(int Key, Vector3d Position)> Players { get; }
        public List<(int Key, int Damage, long At)> Hits { get; } = [];
        public List<(int Key, Vector3d To)> Snaps { get; } = [];
        public long Now { get; private set; }

        public Side(int npc, bool engine, params (int Key, Vector3d Position)[] players)
        {
            Players = [.. players];
            Boss = new Target
            {
                Key = BossKey, NpcId = npc, Name = BossPhases.Embedded.Of(npc)!.Name, Hp = 1_000_000, HpMax = 1_000_000, Level = 63,
                Position = Vector3d.Zero, WalkSpeed = 0.001, RunSpeed = 0.001, Radius = 150, SubType = "HeroMonster", Grade = 2,
            };
            Map[BossKey] = Boss;
            Ai = new NpcAi(new Random(11), GameData.Embedded)
            {
                Bosses = new BossPhases(new Dictionary<int, BossScript> { [npc] = BossPhases.Embedded.Of(npc)! }),
                BossRules = BossOptions.Default with { Engine = engine },
            };
            Ai.Engage(Map, Boss, 0, Tank);
        }

        public void Tick(long ms, long step = 100)
        {
            for (var end = Now + ms; Now < end;)
            {
                Now += step;
                Func<int, int, List<byte[]>> hit = (key, damage) => { Hits.Add((key, damage, Now)); return []; };
                Ai.Tick(Map, Players, Now, hit);
                Ai.TickExpeditionMechanics(Now, Players, hit);
                Snaps.AddRange(Ai.ConsumeEdgeSnaps());
            }
        }

        public void Hp(double percent) => Boss.Hp = (int)(Boss.HpMax * percent / 100);

        /// <summary>Ticks until the boss starts winding up <paramref name="skill"/>; the wind-up's due time.</summary>
        public long WindUp(int skill, long limit = 30_000)
        {
            for (var waited = 0L; waited < limit; waited += 100)
            {
                Tick(100);
                if (Ai.FightOf(Boss)?.Winding is { } w && w.Slam.Skill == skill) return w.DueMs;
            }
            throw new Xunit.Sdk.XunitException($"skill {skill} never wound up");
        }

        public List<(int Key, int Damage)> HitsBetween(long from, long to, int min = int.MinValue, int max = int.MaxValue) =>
            Hits.Where(h => h.At >= from && h.At < to && h.Damage >= min && h.Damage <= max).Select(h => (h.Key, h.Damage)).ToList();
    }

    static (Side Engine, Side Legacy) Both(int npc, params (int Key, Vector3d Position)[] players)
    {
        var engine = new Side(npc, engine: true, players);
        var legacy = new Side(npc, engine: false, players);
        engine.Tick(500);
        legacy.Tick(500);
        Assert.NotNull(engine.Ai.EncounterOf(engine.Boss));
        Assert.Null(legacy.Ai.EncounterOf(legacy.Boss));
        return (engine, legacy);
    }

    static void Both(Side a, Side b, Action<Side> step)
    {
        step(a);
        step(b);
    }

    static (double, double, double, double)[] Walls(IEnumerable<(double MinX, double MaxX, double MinY, double MaxY)> walls) =>
        walls.Select(w => (Math.Round(w.MinX), Math.Round(w.MaxX), Math.Round(w.MinY), Math.Round(w.MaxY))).Order().ToArray();

    static (double, double, double, double)[] EngineWalls(Side s) =>
        s.Ai.EncounterOf(s.Boss)!.Zones.SingleOrDefault(z => z.Id == "fire_walls") is { } zone && s.Now < zone.EndsMs
            ? Walls(zone.Walls.Select(w => (w.MinX, w.MaxX, w.MinY, w.MaxY))) : [];

    static (double, double, double, double)[] LegacyWalls(Side s) =>
        s.Ai.ActiveFireWall is { } run && s.Now <= run.ActiveUntilMs ? Walls(run.Walls.Select(w => (w.MinX, w.MaxX, w.MinY, w.MaxY))) : [];

    [Theory]
    [InlineData(2300371, 1801185)]
    [InlineData(2320371, 1801191)]
    public void Kromede_fire_walls_match_the_hand_coded_grid_through_both_phases(int npc, int skill)
    {
        var (engine, legacy) = Both(npc, (Tank, new Vector3d(200, 0, 0)));
        Both(engine, legacy, s => s.Hp(74));
        var dueEngine = engine.WindUp(skill);
        var dueLegacy = legacy.WindUp(skill);
        Assert.Equal(dueLegacy, dueEngine);
        for (var t = 0; t < 40_000; t += 500)
        {
            Both(engine, legacy, s => s.Tick(500));
            Assert.Equal(LegacyWalls(legacy), EngineWalls(engine));
        }
        Assert.Empty(EngineWalls(engine));

        Both(engine, legacy, s => s.Hp(24));
        Assert.Equal(legacy.WindUp(skill, 60_000), engine.WindUp(skill, 60_000));
        for (var t = 0; t < 40_000; t += 500)
        {
            Both(engine, legacy, s => s.Tick(500));
            Assert.Equal(LegacyWalls(legacy), EngineWalls(engine));
        }
    }

    [Fact]
    public void Kromede_walls_block_movement_and_burn_the_same_way()
    {
        var (engine, legacy) = Both(2300371, (Tank, new Vector3d(200, 0, 0)));
        Both(engine, legacy, s => s.Hp(74));
        engine.WindUp(1801185);
        legacy.WindUp(1801185);
        Both(engine, legacy, s => s.Tick(1000));
        var wall = legacy.Ai.ActiveFireWall!.Walls.OrderBy(w => Math.Abs(w.MinX + w.MaxX) + Math.Abs(w.MinY + w.MaxY)).First();
        var inWall = new Vector3d((wall.MinX + wall.MaxX) / 2, (wall.MinY + wall.MaxY) / 2, 0);
        var centre = legacy.Ai.ActiveFireWall.Centre;
        Assert.Equal(legacy.Ai.FireWallBlocksMove(centre, inWall, legacy.Now), engine.Ai.FireWallBlocksMove(centre, inWall, engine.Now));
        Both(engine, legacy, s => s.Players.Add((P2, inWall)));
        var from = engine.Now;
        Both(engine, legacy, s => s.Tick(1500));
        int Burns(Side s) => s.HitsBetween(from, s.Now + 1, 8000, 12000).Count(h => h.Key == P2);
        Assert.InRange(Burns(engine), 1, 2);
        Assert.Equal(Burns(legacy), Burns(engine));
    }

    [Fact]
    public void Kromede_clones_split_at_50_percent_as_two_copies_of_the_boss()
    {
        var (engine, legacy) = Both(2300371, (Tank, new Vector3d(200, 0, 0)));
        Both(engine, legacy, s => s.Hp(49));
        Both(engine, legacy, s => s.Tick(200));
        var e = engine.Ai.TakeSummons().Single();
        var l = legacy.Ai.TakeSummons().Single();
        Assert.Equal((l.Adds.Npc, l.Adds.Count, l.Adds.Radius), (e.Adds.Npc, e.Adds.Count, e.Adds.Radius));
        Assert.Equal(2300371, e.Adds.Npc);
        Assert.Equal("clones", e.Group);
        Assert.Equal("clones", engine.Ai.EncounterOf(engine.Boss)!.Phase);
    }

    [Fact]
    public void Kromede_clone_lines_hit_players_in_front_of_each_living_clone_every_8_seconds()
    {
        var engine = new Side(2300371, engine: true, (Tank, new Vector3d(200, 0, 0)), (P2, new Vector3d(3000, 400, 0)));
        engine.Tick(500);
        engine.Hp(49);
        engine.Tick(200);
        var request = engine.Ai.TakeSummons().Single();
        var clone = new Target { Key = 5001, NpcId = 2300371, Name = "Clone", Hp = 1000, HpMax = 1000, Level = 63, Position = new Vector3d(2000, 400, 0), Direction = 0 };
        engine.Map[clone.Key] = clone;
        request.Fight.Adds.Add(clone.Key);
        request.Fight.Groups["clones"] = [clone.Key];
        var from = engine.Now;
        engine.Tick(17_000);
        var lines = engine.Ai.EncounterOf(engine.Boss)!.Log.Count(l => l.Rule == "clone_line" && l.Action == "clone_line" && l.Targets == 1);
        Assert.InRange(lines, 2, 3);
        clone.Hp = 0;
        engine.Tick(9_000);
        Assert.Equal(lines, engine.Ai.EncounterOf(engine.Boss)!.Log.Count(l => l.Rule == "clone_line" && l.Action == "clone_line" && l.Targets == 1));
    }

    [Fact]
    public void Berk_pillar_rises_where_the_hand_coded_one_did_and_shadows_the_same_players()
    {
        var covered = new Vector3d(1600, 0, 0);
        var exposed = new Vector3d(-1500, 300, 0);
        var (engine, legacy) = Both(2300171, (Tank, new Vector3d(200, 0, 0)), (P2, covered), (P3, exposed));
        Both(engine, legacy, s => s.Hp(74));
        var dueEngine = engine.WindUp(1800050);
        var dueLegacy = legacy.WindUp(1800050);
        Assert.Equal(dueLegacy, dueEngine);
        var pillar = legacy.Ai.Expedition!.Pillar!.Value;
        var cover = Assert.Single(engine.Ai.EncounterOf(engine.Boss)!.Covers);
        Assert.Equal(pillar.X, cover.At.X, 1);
        Assert.Equal(pillar.Y, cover.At.Y, 1);
        Both(engine, legacy, s => s.Tick(dueEngine - s.Now + 100));
        var hitEngine = engine.HitsBetween(dueEngine, dueEngine + 100).Select(h => h.Key).Order().ToList();
        var hitLegacy = legacy.HitsBetween(dueLegacy, dueLegacy + 100).Select(h => h.Key).Order().ToList();
        Assert.Equal(hitLegacy, hitEngine);
        Assert.DoesNotContain(P2, hitEngine);
        Assert.Contains(P3, hitEngine);
    }

    [Fact]
    public void Berk_extinction_is_a_stagger_check_cast_at_50_percent()
    {
        var (engine, legacy) = Both(2300171, (Tank, new Vector3d(200, 0, 0)));
        Both(engine, legacy, s => s.Hp(74));
        Assert.Equal(legacy.WindUp(1800050), engine.WindUp(1800050));
        Both(engine, legacy, s => s.Tick(8000));
        Both(engine, legacy, s => s.Hp(49));
        Assert.Equal(legacy.WindUp(1800140), engine.WindUp(1800140));
        Assert.True(engine.Ai.FightOf(engine.Boss)!.Winding!.Value.Slam.StaggerCheck);
    }

    [Fact]
    public void Thresholds_crossed_in_one_tick_queue_their_casts_instead_of_dropping_the_first()
    {
        // Legacy: Extinction replaced Wave of Fury before it wound up. The engine casts both, in threshold order.
        var engine = new Side(2300171, engine: true, (Tank, new Vector3d(200, 0, 0)));
        engine.Tick(500);
        engine.Hp(49);
        var fury = engine.WindUp(1800050);
        engine.Tick(fury - engine.Now + 100);
        Assert.True(engine.WindUp(1800140) > fury);
    }

    [Theory]
    [InlineData(2300218, 1601030)]
    [InlineData(2310218, 1601035)]
    public void Auldor_tempest_marks_the_target_and_splits_the_hit_like_the_hand_coded_soak(int npc, int skill)
    {
        var (engine, legacy) = Both(npc, (Tank, new Vector3d(200, 0, 0)), (P2, new Vector3d(300, 100, 0)), (P3, new Vector3d(-2500, 0, 0)));
        Both(engine, legacy, s => s.Hp(74));
        var dueEngine = engine.WindUp(skill);
        var dueLegacy = legacy.WindUp(skill);
        Assert.Equal(dueLegacy, dueEngine);
        Assert.Equal(legacy.Ai.Expedition!.SoakMarkPlayer, engine.Ai.EncounterOf(engine.Boss)!.Mark("tempest"));
        Both(engine, legacy, s => s.Tick(dueEngine - s.Now + 100));
        var landEngine = engine.HitsBetween(dueEngine, dueEngine + 100).Order().ToList();
        var landLegacy = legacy.HitsBetween(dueLegacy, dueLegacy + 100).Order().ToList();
        Assert.Equal(landLegacy, landEngine);
        Assert.Equal([Tank, P2], landEngine.Select(h => h.Key));
    }

    [Fact]
    public void Auldor_outer_arena_burns_below_30_percent_on_the_same_beat()
    {
        var (engine, legacy) = Both(2300218, (Tank, new Vector3d(200, 0, 0)), (P3, new Vector3d(5000, 0, 0)));
        Both(engine, legacy, s => s.Hp(29));
        Both(engine, legacy, s => s.Tick(200));
        var from = engine.Now;
        Both(engine, legacy, s => s.Tick(5000));
        int Burns(Side s) => s.HitsBetween(from, s.Now + 1, 6000, 9000).Count(h => h.Key == P3);
        Assert.InRange(Burns(engine), 4, 6);
        Assert.Equal(Burns(legacy), Burns(engine));
    }

    [Fact]
    public void Bakarma_wave_of_death_snaps_to_the_edge_and_pulses_the_same_rings()
    {
        var players = new[] { (Tank, new Vector3d(200, 0, 0)), (P2, new Vector3d(2000, 0, 0)), (P3, new Vector3d(0, 3500, 0)) };
        var (engine, legacy) = Both(2300471, players);
        Both(engine, legacy, s => s.Hp(69));
        var dueEngine = engine.WindUp(1800655);
        var dueLegacy = legacy.WindUp(1800655);
        Assert.Equal(dueLegacy, dueEngine);
        Both(engine, legacy, s => s.Tick(100));
        Assert.Equal(legacy.Snaps.Select(s => (s.Key, Math.Round(s.To.X), Math.Round(s.To.Y))).Order(),
            engine.Snaps.Select(s => (s.Key, Math.Round(s.To.X), Math.Round(s.To.Y))).Order());
        var start = engine.Now;
        Both(engine, legacy, s => s.Tick(dueEngine - s.Now + 8000));
        foreach (var (key, _) in players)
            Assert.Equal(legacy.HitsBetween(start, dueLegacy - 100, 15000, 25000).Count(h => h.Key == key),
                engine.HitsBetween(start, dueEngine - 100, 15000, 25000).Count(h => h.Key == key));
        Assert.NotEmpty(engine.HitsBetween(start, dueEngine, 15000, 25000));
    }

    [Fact]
    public void Bakarma_whirlpools_open_below_30_percent_either_side_of_the_boss()
    {
        var (engine, legacy) = Both(2300471, (Tank, new Vector3d(200, 0, 0)), (P2, new Vector3d(1200, 0, 0)), (P3, new Vector3d(0, 1200, 0)));
        Both(engine, legacy, s => s.Hp(29));
        var from = engine.Now;
        Both(engine, legacy, s => s.Tick(6000));
        int Swirls(Side s, int key) => s.HitsBetween(from, s.Now + 1, 8000, 14000).Count(h => h.Key == key);
        Assert.InRange(Swirls(engine, P2), 3, 5);
        Assert.Equal(Swirls(legacy, P2), Swirls(engine, P2));
        Assert.Equal(0, Swirls(engine, P3));
    }

    [Fact]
    public void Nuakum_hunt_sends_the_hunting_clone_and_blue_light_opens_the_orb()
    {
        var (engine, legacy) = Both(2300910, (Tank, new Vector3d(200, 0, 0)));
        Both(engine, legacy, s => s.Hp(74));
        Both(engine, legacy, s => s.Tick(200));
        var e = engine.Ai.TakeSummons().Single();
        var l = legacy.Ai.TakeSummons().Single();
        Assert.Equal((l.Adds.Npc, l.Adds.Count, l.Adds.Radius, l.VictimKey), (e.Adds.Npc, e.Adds.Count, e.Adds.Radius, e.VictimKey));
        Both(engine, legacy, s => s.Hp(29));
        Both(engine, legacy, s => s.Tick(200));
        var orb = Assert.Single(engine.Ai.EncounterOf(engine.Boss)!.Orbs);
        Assert.Equal((3, 450.0), (orb.Required, orb.Radius));
        Assert.Equal(legacy.Ai.Expedition!.BlueLightEndsMs - legacy.Now, orb.EndsMs - engine.Now, 200.0);
    }

    [Theory]
    [InlineData(2300171, 300, 2.0)]
    [InlineData(2320171, 240, 2.0)]
    [InlineData(2310171, 400, 1.0)]
    public void Enrage_timer_matches_the_frenzy_on_every_difficulty(int npc, int seconds, double damage)
    {
        var (engine, legacy) = Both(npc, (Tank, new Vector3d(200, 0, 0)));
        Both(engine, legacy, s => s.Tick(seconds * 1000L + 1000, step: 1000));
        Assert.Equal(legacy.Ai.FightOf(legacy.Boss)!.Damage, engine.Ai.FightOf(engine.Boss)!.Damage);
        Assert.Equal(damage, engine.Ai.FightOf(engine.Boss)!.Damage);
        Assert.Equal(legacy.Ai.FightOf(legacy.Boss)!.Haste, engine.Ai.FightOf(engine.Boss)!.Haste);
    }

    /// <summary>A mid-boss that still runs the compiled generic script (not authored), so these parity tests keep covering the compile path.</summary>
    internal static int GenericMid { get; } = BossPhases.Embedded.All
        .Where(b => b.Script is { Role: "mid", Telegraph: not null } && BossEncounters.Embedded.Resolve(b.NpcId, BossPhases.Embedded) is { Compiled: true })
        .Select(b => b.NpcId).Min();

    [Theory]
    [InlineData(0)]
    [InlineData(2300171)]
    [InlineData(2300910)]
    public void Rotation_slams_wind_up_on_the_same_schedule(int npc)
    {
        if (npc == 0) npc = GenericMid;
        var (engine, legacy) = Both(npc, (Tank, new Vector3d(200, 0, 0)));
        var dues = (Engine: new List<(long, int)>(), Legacy: new List<(long, int)>());
        for (var t = 0; t < 90_000; t += 100)
        {
            Both(engine, legacy, s => s.Tick(100));
            if (engine.Ai.FightOf(engine.Boss)?.Winding is { } we && (dues.Engine.Count == 0 || dues.Engine[^1].Item1 != we.DueMs)) dues.Engine.Add((we.DueMs, we.Slam.Skill));
            if (legacy.Ai.FightOf(legacy.Boss)?.Winding is { } wl && (dues.Legacy.Count == 0 || dues.Legacy[^1].Item1 != wl.DueMs)) dues.Legacy.Add((wl.DueMs, wl.Slam.Skill));
        }
        Assert.NotEmpty(dues.Engine);
        Assert.Equal(dues.Legacy, dues.Engine);
    }

    [Fact]
    public void A_compiled_generic_fight_runs_its_phases_like_the_legacy_loop()
    {
        var (engine, legacy) = Both(GenericMid, (Tank, new Vector3d(200, 0, 0)));
        Assert.True(engine.Ai.EncounterOf(engine.Boss)!.Encounter.Compiled);
        Both(engine, legacy, s => s.Hp(49));
        Both(engine, legacy, s => s.Tick(300));
        var (fe, fl) = (engine.Ai.FightOf(engine.Boss)!, legacy.Ai.FightOf(legacy.Boss)!);
        Assert.Equal((fl.Reached, fl.Damage, fl.Haste), (fe.Reached, fe.Damage, fe.Haste));
    }
}
