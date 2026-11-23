using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>One test (or more) per engine trigger and primitive (docs/BOSS-MECHANICS-ENGINE.md), on a synthetic encounter.</summary>
public sealed class BossEngineTests
{
    internal const int Npc = 2399001, BossKey = 9001, Tank = 100001, P2 = 100002, P3 = 100003;
    const int Mark = 7777;

    /// <summary>A boss at the origin fighting <see cref="Tank"/> (at +X, so the boss faces +X), on an encounter built from rules JSON.</summary>
    internal sealed class Rig
    {
        public NpcAi Ai { get; }
        public Dictionary<int, Target> Map { get; } = [];
        public Target Boss { get; }
        public List<(int Key, Vector3d Position)> Players { get; } = [(Tank, new Vector3d(300, 0, 0))];
        public List<(int Key, int Damage, long At)> Hits { get; } = [];
        public List<BossMechanicTelegraphEventArgs> EnvEvents { get; } = [];
        public long Now { get; private set; }

        public Rig(string encounterJson, int hp = 100_000, BossPhases? scripts = null, int npc = Npc)
        {
            var encounters = BossEncounters.Parse(("test.json", encounterJson));
            Assert.Empty(encounters.Errors);
            Boss = new Target
            {
                Key = BossKey, NpcId = npc, Name = "Engine_Test_Boss", Hp = hp, HpMax = hp, Level = 60, Position = Vector3d.Zero,
                WalkSpeed = 0.001, RunSpeed = 0.001, Radius = 150, SubType = "HeroMonster", Grade = 2,
            };
            Map[BossKey] = Boss;
            Ai = new NpcAi(new Random(7), GameData.Embedded) { Bosses = scripts ?? BossPhases.None, Encounters = encounters };
            Ai.MechanicTelegraphStartedHook = e => { EnvEvents.Add(e); return []; };
            Ai.Engage(Map, Boss, 0, Tank);
        }

        public EncounterRun Run => Ai.EncounterOf(Boss) ?? throw new InvalidOperationException("no engine fight");

        public List<byte[]> Tick(long ms = 100)
        {
            var replies = new List<byte[]>();
            for (var end = Now + ms; Now < end;)
            {
                Now += 100;
                replies.AddRange(Ai.Tick(Map, Players, Now, (key, damage) => { Hits.Add((key, damage, Now)); return []; }));
            }
            return replies;
        }

        public void Hp(double percent) => Boss.Hp = (int)(Boss.HpMax * percent / 100);

        /// <summary>Hits with the test's marker damage (7777), so the boss's ordinary attacks never count.</summary>
        public List<int> Marked() => Hits.Where(h => h.Damage == Mark).Select(h => h.Key).ToList();

        public bool Logged(string action, string? resultStartsWith = null) =>
            Run.Log.Any(l => l.Action == action && (resultStartsWith is null || l.Result.StartsWith(resultStartsWith, StringComparison.Ordinal)));

        public void Place(int key, double x, double y)
        {
            Players.RemoveAll(p => p.Key == key);
            Players.Add((key, new Vector3d(x, y, 0)));
        }

        public Target Add(int key, Vector3d at)
        {
            var add = new Target { Key = key, NpcId = 2300910, Name = "Add", Hp = 1000, HpMax = 1000, Level = 60, Position = at, WalkSpeed = 0.001, RunSpeed = 0.001 };
            Map[key] = add;
            return add;
        }
    }

    internal static string Encounter(string rules, string extra = "") => $$"""
        { "id": "engine_test", "dungeon": "Engine_Test_Normal", "npcs": { "Normal": [{{Npc}}] }, {{extra}} "rules": [{{rules}}] }
        """;

    const string Hit = """ "damage": { "fixed": [7777, 7777] } """;

    // ---- loading and difficulty -------------------------------------------------------------------------------------

    [Fact]
    public void Embedded_encounters_load_without_errors_and_cover_the_five_finals_on_every_difficulty()
    {
        var catalog = BossEncounters.Embedded;
        Assert.Empty(catalog.Errors);
        foreach (var family in new[] { 171, 218, 371, 471, 910 })
            foreach (var prefix in new[] { 230, 231, 232 })
                Assert.True(catalog.Authored(prefix * 10_000 + family), $"{prefix * 10_000 + family} has no authored encounter");
    }

    [Theory]
    [InlineData("""{ "id": "x", "npcs": { "Normal": [1] }, "rules": [{ "id": "r", "when": { "type": "hp_sideways" }, "do": [] }] }""", "unknown trigger")]
    [InlineData("""{ "id": "x", "npcs": { "Normal": [1] }, "rules": [{ "id": "r", "when": { "type": "fight_start" }, "do": [{ "type": "explode" }] }] }""", "unknown action")]
    [InlineData("""{ "id": "x", "npcs": { "Normal": [1] }, "rules": [{ "id": "r", "when": { "type": "fight_start" }, "do": [{ "type": "notice" }] }] }""", "text")]
    [InlineData("""{ "id": "x", "npcs": { "Normal": [1] }, "rules": [{ "id": "r", "when": { "type": "fight_start" }, "do": [{ "type": "zone", "radius": "$nope" }] }] }""", "nope")]
    [InlineData("""{ "id": "x", "npcs": { "Normal": [1] }, "rules": [], "colour": 3 }""", "JSON")]
    [InlineData("""{ "id": "x", "npcs": { "Medium": [1] }, "rules": [] }""", "difficulty")]
    public void Bad_files_are_rejected_with_a_readable_error(string json, string expected)
    {
        var catalog = BossEncounters.Parse(("bad.json", json));
        Assert.False(catalog.Authored(1));
        Assert.Contains(catalog.Errors, e => e.File == "bad.json" && e.Message.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_authoring_guide_examples_load()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "docs/BOSS-MECHANICS-ENGINE.md"))) dir = Directory.GetParent(dir)?.FullName;
        var guide = File.ReadAllText(Path.Combine(dir!, "docs/BOSS-MECHANICS-ENGINE.md"));
        var examples = guide[guide.IndexOf("## Examples", StringComparison.Ordinal)..];
        var blocks = System.Text.RegularExpressions.Regex.Matches(examples, "```json\n(.*?)```", System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value.Trim()).Take(3).ToArray();
        Assert.Equal(3, blocks.Length);
        var last = blocks[2];
        var rules = $"{blocks[0]}, {blocks[1]}, {last[(last.IndexOf('[') + 1)..last.LastIndexOf(']')]}";
        var catalog = BossEncounters.Parse(("guide.json",
            $$"""{ "id": "guide", "npcs": { "Normal": [1] }, "vars": { "soft_ms": 300000, "hard_ms": 420000 }, "rules": [{{rules}}] }"""));
        Assert.Empty(catalog.Errors);
        Assert.Equal(6, catalog.All.Single().Rules.Count);
    }

    [Fact]
    public void Difficulty_merges_vars_remaps_skills_and_filters_rules()
    {
        var hard = BossEncounters.Embedded.Resolve(2320371, BossPhases.Embedded)!;
        var easy = BossEncounters.Embedded.Resolve(2310371, BossPhases.Embedded)!;
        var normal = BossEncounters.Embedded.Resolve(2300371, BossPhases.Embedded)!;
        Assert.Equal(("Hard", "Easy", "Normal"), (hard.Difficulty, easy.Difficulty, normal.Difficulty));
        Assert.Equal(1801191, hard.Skill(1801185));
        Assert.Equal(1801185, normal.Skill(1801185));
        Assert.Equal(240_000, hard.Vars["frenzy_ms"]);
        Assert.Equal(15_100, hard.Vars["wall_wind_up"]);
        Assert.Contains(normal.Rules, r => r.Id == "frenzy");
        Assert.DoesNotContain(easy.Rules, r => r.Id == "frenzy");
        Assert.Equal("Fire_Temple_Hard", hard.Dungeon);
    }

    [Fact]
    public void Generic_boss_phases_scripts_compile_into_engine_rules()
    {
        var script = BossPhases.Embedded.Of(2300104)!;
        var compiled = BossEncounters.Embedded.Resolve(2300104, BossPhases.Embedded)!;
        Assert.True(compiled.Compiled);
        Assert.Equal(script.Ordered.Count + (script.Frenzy is null ? 0 : 1), compiled.Rules.Count);
        Assert.All(compiled.Rules.Take(script.Ordered.Count), r => Assert.Equal("hp_below", r.When.Type));
    }

    [Fact]
    public void Time_scale_and_damage_scale_apply_per_difficulty()
    {
        var def = BossEncounters.Parse(("t.json", """
            { "id": "t", "npcs": { "Normal": [1], "Hard": [2] }, "vars": { "wait": 1000 },
              "difficulty": { "Hard": { "time_scale": 0.5, "damage_scale": 2 } }, "rules": [] }
            """));
        var hard = def.Resolve(2, BossPhases.None)!;
        Assert.Equal(500, hard.Ms(new Num(0, "wait")));
        Assert.Equal(2, hard.DamageScale);
        Assert.Equal(1000, def.Resolve(1, BossPhases.None)!.Ms(new Num(0, "wait")));
    }

    // ---- triggers ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Hp_below_fires_once_and_hp_every_fires_per_step()
    {
        var rig = new Rig(Encounter("""
            { "id": "half", "when": { "type": "hp_below", "pct": 50 }, "do": [{ "type": "counter", "counter": "half", "add": 1 }] },
            { "id": "tenths", "when": { "type": "hp_every", "step": 10 }, "do": [{ "type": "counter", "counter": "tenths", "add": 1 }] }
            """));
        rig.Tick();
        rig.Hp(45);
        rig.Tick(300);
        rig.Hp(10);
        rig.Tick(300);
        Assert.Equal(1, rig.Run.Counters["half"]);
        Assert.Equal(9, rig.Run.Counters["tenths"]);
    }

    [Fact]
    public void Timers_count_from_the_fight_or_the_phase_and_stop_after_count()
    {
        var rig = new Rig(Encounter("""
            { "id": "tick", "when": { "type": "timer", "after_ms": 1000, "every_ms": 1000, "count": 3 }, "do": [{ "type": "counter", "counter": "t", "add": 1 }] },
            { "id": "p2", "when": { "type": "timer", "after_ms": 2500 }, "do": [{ "type": "phase", "phase": "p2" }] },
            { "id": "in_p2", "when": { "type": "timer", "from": "phase", "after_ms": 500 }, "phase": "p2", "do": [{ "type": "counter", "counter": "p", "add": 1 }] }
            """));
        rig.Tick(2200);
        Assert.Equal(2, rig.Run.Counters["t"]);
        rig.Tick(6000);
        Assert.Equal(3, rig.Run.Counters["t"]);
        Assert.Equal("p2", rig.Run.Phase);
        Assert.Equal(1, rig.Run.Counters["p"]);
    }

    [Fact]
    public void Enrage_timer_soft_enrage_multiplies_and_hard_enrage_kills()
    {
        var rig = new Rig(Encounter("""
            { "id": "soft", "when": { "type": "enrage_timer", "after_ms": 1000 }, "do": [{ "type": "enrage", "damage_mul": 2, "haste_mul": 0.5 }] },
            { "id": "hard", "when": { "type": "enrage_timer", "after_ms": 3000 }, "do": [{ "type": "enrage", "hard": true, "every_ms": 1000 }] }
            """));
        rig.Tick(1200);
        var fight = rig.Ai.FightOf(rig.Boss)!;
        Assert.Equal((2.0, 0.5), (fight.Damage, fight.Haste));
        Assert.DoesNotContain(rig.Hits, h => h.Damage == NpcAi.LethalDamage);
        rig.Tick(2500);
        Assert.Contains(rig.Hits, h => h.Key == Tank && h.Damage == NpcAi.LethalDamage);
    }

    [Fact]
    public void Phase_enter_and_exit_trigger_and_rules_filter_by_phase()
    {
        var rig = new Rig(Encounter("""
            { "id": "go", "when": { "type": "hp_below", "pct": 80 }, "do": [{ "type": "phase", "phase": "burn" }] },
            { "id": "leave", "when": { "type": "hp_below", "pct": 40 }, "do": [{ "type": "phase", "phase": "calm" }] },
            { "id": "enter", "when": { "type": "phase_enter", "phase": "burn" }, "do": [{ "type": "counter", "counter": "enter", "add": 1 }] },
            { "id": "exit", "when": { "type": "phase_exit", "phase": "burn" }, "do": [{ "type": "counter", "counter": "exit", "add": 1 }] },
            { "id": "only_burn", "when": { "type": "hp_every", "step": 10 }, "phase": "burn", "once": false, "do": [{ "type": "counter", "counter": "burn_steps", "add": 1 }] }
            """), hp: 1000);
        rig.Hp(75);
        rig.Tick(200);
        rig.Hp(55);
        rig.Tick(200);
        rig.Hp(35);
        rig.Tick(200);
        rig.Hp(5);
        rig.Tick(200);
        Assert.Equal(1, rig.Run.Counters["enter"]);
        Assert.Equal(1, rig.Run.Counters["exit"]);
        Assert.Equal("calm", rig.Run.Phase);
        Assert.Equal(4, rig.Run.Counters["burn_steps"]);
    }

    [Fact]
    public void Players_in_area_fires_after_the_hold_time()
    {
        var rig = new Rig(Encounter("""
            { "id": "crowd", "when": { "type": "players_in_area", "at": "boss", "area": { "shape": "circle", "radius": 500 }, "min": 2, "hold_ms": 1000 },
              "do": [{ "type": "counter", "counter": "crowd", "add": 1 }] }
            """));
        rig.Tick(1500);
        Assert.False(rig.Run.Counters.ContainsKey("crowd"));
        rig.Place(P2, 200, 100);
        rig.Tick(500);
        Assert.False(rig.Run.Counters.ContainsKey("crowd"));
        rig.Tick(800);
        Assert.Equal(1, rig.Run.Counters["crowd"]);
    }

    [Fact]
    public void Npc_killed_fires_when_a_listed_npc_dies()
    {
        var rig = new Rig(Encounter("""
            { "id": "mid", "when": { "type": "npc_killed", "npc": 2300910 }, "do": [{ "type": "counter", "counter": "mid", "add": 1 }] }
            """));
        var mid = rig.Add(5100, new Vector3d(9000, 9000, 0));
        rig.Tick(300);
        Assert.False(rig.Run.Counters.ContainsKey("mid"));
        mid.Hp = 0;
        rig.Tick(200);
        Assert.Equal(1, rig.Run.Counters["mid"]);
    }

    // ---- telegraphs and shapes --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("""  "shape": "circle", "radius": 600 """, new[] { Tank, P3 })]
    [InlineData("""  "shape": "donut", "inner": 400, "radius": 1000 """, new[] { P2, P3 })]
    [InlineData("""  "shape": "cone", "angle": 90, "radius": 1000 """, new[] { Tank })]
    [InlineData("""  "shape": "rect", "length": 1000, "width": 400 """, new[] { Tank })]
    [InlineData("""  "shape": "line", "length": 2000, "width": 400 """, new[] { Tank, P3 })]
    [InlineData("""  "shape": "safe_zone", "radius": 600 """, new[] { P2 })]
    [InlineData("""  "shape": "circle", "radius": 600, "invert": true """, new[] { P2 })]
    public void Telegraph_shapes_hit_exactly_the_players_inside_after_the_delay(string shape, int[] expected)
    {
        var rig = new Rig(Encounter($$"""
            { "id": "t", "when": { "type": "timer", "after_ms": 200 }, "do": [{ "type": "telegraph", "at": "boss", {{shape}}, "delay_ms": 1000, {{Hit}} }] }
            """));
        rig.Place(P2, 300, 600);
        rig.Place(P3, -500, 0);
        rig.Tick(800);
        Assert.Empty(rig.Marked());
        rig.Tick(700);
        Assert.Equal(expected.Order(), rig.Marked().Order());
    }

    [Fact]
    public void Telegraph_count_and_interval_repeat_and_offsets_place_several_shapes()
    {
        var rig = new Rig(Encounter($$"""
            { "id": "t", "when": { "type": "fight_start" }, "do": [{ "type": "telegraph", "at": "boss", "offsets": [[300, 0], [-500, 0]], "shape": "circle",
              "radius": 100, "count": 3, "interval_ms": 1000, {{Hit}} }] }
            """));
        rig.Place(P3, -500, 0);
        rig.Tick(2500);
        Assert.Equal(3, rig.Marked().Count(k => k == Tank));
        Assert.Equal(3, rig.Marked().Count(k => k == P3));
    }

    [Fact]
    public void Steps_change_the_shape_on_each_iteration_of_a_repeat()
    {
        var rig = new Rig(Encounter($$"""
            { "id": "rings", "when": { "type": "fight_start" }, "do": [{ "type": "repeat", "every_ms": 1000, "count": 2, "do": [
              { "type": "telegraph", "at": "boss", "steps": [{ "shape": "circle", "radius": 400 }, { "shape": "donut", "inner": 400, "radius": 800 }], {{Hit}} }] }] }
            """));
        rig.Place(P2, 600, 0);
        rig.Tick(1100);
        Assert.Equal([Tank], rig.Marked());
        rig.Tick(1000);
        Assert.Equal([Tank, P2], rig.Marked());
    }

    [Fact]
    public void Success_and_fail_branches_and_outcome_events_chain_rules()
    {
        var rig = new Rig(Encounter($$"""
            { "id": "t", "when": { "type": "timer", "after_ms": 100 }, "do": [{ "type": "telegraph", "id": "blast", "at": "boss", "shape": "circle", "radius": 50, {{Hit}},
              "on_success": [{ "type": "counter", "counter": "dodged", "add": 1 }] }] },
            { "id": "after", "when": { "type": "event", "event": "blast.success" }, "do": [{ "type": "counter", "counter": "event", "add": 1 }] }
            """));
        rig.Tick(400);
        Assert.Equal(1, rig.Run.Counters["dodged"]);
        Assert.Equal(1, rig.Run.Counters["event"]);
    }

    // ---- zones ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Zone_ticks_damage_on_players_inside_until_it_expires()
    {
        var rig = new Rig(Encounter($$"""
            { "id": "z", "when": { "type": "fight_start" }, "do": [{ "type": "zone", "id": "puddle", "at": "boss", "shape": "circle", "radius": 500,
              "tick_ms": 500, "first_tick_ms": 0, "duration_ms": 2000, {{Hit}} }] }
            """));
        rig.Place(P2, 2000, 0);
        rig.Tick(3000);
        Assert.Equal(4, rig.Marked().Count(k => k == Tank));
        Assert.DoesNotContain(P2, rig.Marked());
        Assert.False(rig.Run.ZoneActive("puddle"));
    }

    [Fact]
    public void Grid_zone_builds_walls_with_gaps_and_blocks_movement_into_them()
    {
        var rig = new Rig(Encounter($$"""
            { "id": "z", "when": { "type": "fight_start" }, "do": [{ "type": "zone", "id": "walls", "at": "boss", "shape": "grid", "grid": { "gap_patterns": [[3]] },
              "tick_ms": 1000, "blocks_movement": true, "duration_ms": 5000, {{Hit}} }] }
            """));
        rig.Tick(200);
        var zone = Assert.Single(rig.Run.Zones);
        Assert.NotEmpty(zone.Walls);
        var wall = zone.Walls.OrderBy(w => Math.Abs(w.MinX + w.MaxX) + Math.Abs(w.MinY + w.MaxY)).First();
        var inWall = new Vector3d((wall.MinX + wall.MaxX) / 2, (wall.MinY + wall.MaxY) / 2, 0);
        var gap = new Vector3d(-5000 + 3 * 2800 + 1400, 0, 0);
        Assert.DoesNotContain(zone.Walls, w => w.Contains(gap));
        Assert.True(rig.Ai.FireWallBlocksMove(gap, inWall, rig.Now));
        Assert.False(rig.Ai.FireWallBlocksMove(inWall, gap, rig.Now));
        rig.Place(Tank, inWall.X, inWall.Y);
        rig.Tick(1500);
        Assert.Contains(Tank, rig.Marked());
        rig.Tick(5000);
        Assert.False(rig.Ai.FireWallBlocksMove(gap, inWall, rig.Now));
    }

    // ---- soak / stack / spread / cover ------------------------------------------------------------------------------

    [Fact]
    public void Soak_fails_without_enough_players_and_succeeds_with_them()
    {
        var rig = new Rig(Encounter("""
            { "id": "s1", "when": { "type": "timer", "after_ms": 200 }, "do": [{ "type": "soak", "id": "tower", "at": "boss", "radius": 500, "min_players": 2,
              "damage": { "fixed": [1000, 1000] }, "decal": false, "on_fail": [{ "type": "counter", "counter": "fail", "add": 1 }] }] },
            { "id": "s2", "when": { "type": "timer", "after_ms": 1000 }, "do": [{ "type": "soak", "at": "boss", "radius": 500, "min_players": 2,
              "damage": { "fixed": [1000, 1000] }, "decal": false, "on_success": [{ "type": "counter", "counter": "ok", "add": 1 }] }] }
            """));
        rig.Tick(500);
        Assert.Equal(1, rig.Run.Counters["fail"]);
        rig.Place(P2, 100, 100);
        rig.Tick(800);
        Assert.Equal(1, rig.Run.Counters["ok"]);
    }

    [Fact]
    public void Stack_splits_one_hit_among_everyone_on_the_mark()
    {
        var rig = new Rig(Encounter("""
            { "id": "m", "when": { "type": "fight_start" }, "do": [{ "type": "mark", "name": "target", "target": "victim" },
              { "type": "stack", "at": "mark:target", "radius": 400, "delay_ms": 500, "damage": { "fixed": [9000, 9000] }, "decal": false }] }
            """));
        rig.Place(P2, 400, 100);
        rig.Place(P3, 3000, 0);
        rig.Tick(800);
        var split = rig.Hits.Where(h => h.Damage == 4500).Select(h => h.Key).Order().ToList();
        Assert.Equal([Tank, P2], split);
        Assert.DoesNotContain(rig.Hits, h => h.Key == P3 && h.Damage is 4500 or 9000);
    }

    [Fact]
    public void Spread_fails_and_hurts_more_when_circles_overlap()
    {
        var rig = new Rig(Encounter("""
            { "id": "sp", "when": { "type": "fight_start" }, "do": [{ "type": "spread", "id": "bombs", "at": "each_player", "radius": 300, "delay_ms": 300,
              "damage": { "fixed": [1000, 1000] }, "decal": false, "on_fail": [{ "type": "counter", "counter": "fail", "add": 1 }] }] }
            """));
        rig.Place(P2, 400, 0);
        rig.Place(P3, 5000, 0);
        rig.Tick(600);
        Assert.Equal(1, rig.Run.Counters["fail"]);
        Assert.Contains(rig.Hits, h => h.Key == Tank && h.Damage == 2000);
        Assert.Contains(rig.Hits, h => h.Key == P3 && h.Damage == 1000);
    }

    [Fact]
    public void Cover_objects_spawn_an_env_object_and_shadow_players_from_the_boss()
    {
        var rig = new Rig(Encounter("""
            { "id": "c", "when": { "type": "fight_start" }, "do": [{ "type": "cover", "id": "pillar", "at": "boss", "toward": "victim", "distance": 1000,
              "length": 900, "width": 1400, "env_obj": 1400623, "duration_ms": 2000 }] }
            """));
        rig.Tick(200);
        var cover = Assert.Single(rig.Run.Covers);
        Assert.Equal(1000, cover.At.X, 1);
        Assert.True(rig.Ai.Covered(rig.Boss, rig.Run, new Vector3d(1400, 0, 0), rig.Now));
        Assert.False(rig.Ai.Covered(rig.Boss, rig.Run, new Vector3d(600, 0, 0), rig.Now));
        Assert.False(rig.Ai.Covered(rig.Boss, rig.Run, new Vector3d(1400, 1500, 0), rig.Now));
        Assert.Contains(rig.EnvEvents, e => e.SpawnEnv && e.EnvObjIds!.Contains(1400623));
        rig.Tick(2500);
        Assert.Contains(rig.EnvEvents, e => e.Despawn);
        Assert.Empty(rig.Run.Covers);
    }

    // ---- displacement / movement ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("knockback", 1300)]
    [InlineData("pull", 0)]
    [InlineData("to_radius", 4800)]
    public void Displace_moves_players_relative_to_the_anchor(string mode, double expectedX)
    {
        var rig = new Rig(Encounter($$"""
            { "id": "d", "when": { "type": "fight_start" }, "do": [{ "type": "displace", "mode": "{{mode}}", "at": "boss", "distance": 1000, "radius": 4800 }] }
            """));
        rig.Tick(100);
        var (key, to) = Assert.Single(rig.Ai.ConsumeEdgeSnaps());
        Assert.Equal(Tank, key);
        Assert.Equal(expectedX, to.X, 1);
    }

    [Fact]
    public void Move_puts_the_boss_on_the_point()
    {
        var rig = new Rig(Encounter("""
            { "id": "m", "when": { "type": "fight_start" }, "do": [{ "type": "move", "to": { "x": 2000, "y": -500 } }] }
            """));
        rig.Tick(100);
        Assert.Equal((2000.0, -500.0), (rig.Boss.Position.X, rig.Boss.Position.Y));
    }

    // ---- adds / shields / stagger / parts ---------------------------------------------------------------------------

    [Fact]
    public void Summon_groups_adds_and_adds_dead_add_died_and_shield_until_adds_dead_follow_them()
    {
        var rig = new Rig(Encounter("""
            { "id": "call", "when": { "type": "fight_start" }, "do": [
              { "type": "summon", "npc": 2300910, "count": 2, "radius": 300, "group": "guards", "at": { "x": 1000, "y": 0 } },
              { "type": "shield", "id": "ward", "invuln": true, "until": "adds_dead:guards" }] },
            { "id": "one_down", "when": { "type": "add_died", "group": "guards" }, "once": false, "do": [{ "type": "counter", "counter": "died", "add": 1 }] },
            { "id": "all_down", "when": { "type": "adds_dead", "group": "guards" }, "do": [{ "type": "counter", "counter": "all", "add": 1 }] },
            { "id": "broken", "when": { "type": "shield_broken", "shield": "ward" }, "do": [{ "type": "counter", "counter": "broken", "add": 1 }] }
            """));
        rig.Tick(100);
        var request = Assert.Single(rig.Ai.TakeSummons());
        Assert.Equal(("guards", 2300910, 2), (request.Group, request.Adds.Npc, request.Adds.Count));
        Assert.Equal(1000, request.At!.Value.X);
        Assert.True(rig.Boss.Immune);
        var a = rig.Add(5001, new Vector3d(1000, 0, 0));
        var b = rig.Add(5002, new Vector3d(1100, 0, 0));
        request.Fight.Groups["guards"] = [a.Key, b.Key];
        a.Hp = 0;
        rig.Tick(200);
        Assert.Equal(1, rig.Run.Counters["died"]);
        Assert.False(rig.Run.Counters.ContainsKey("all"));
        b.Hp = 0;
        rig.Tick(200);
        Assert.Equal(2, rig.Run.Counters["died"]);
        Assert.Equal(1, rig.Run.Counters["all"]);
        Assert.Equal(1, rig.Run.Counters["broken"]);
        Assert.False(rig.Boss.Immune);
    }

    [Fact]
    public void Summon_with_a_wave_starts_the_map_wave()
    {
        var rig = new Rig(Encounter("""
            { "id": "w", "when": { "type": "fight_start" }, "do": [{ "type": "summon", "wave": "Boss_Wave_1" }] }
            """));
        rig.Tick(100);
        Assert.Equal(["Boss_Wave_1"], rig.Ai.TakeWaveStarts());
    }

    [Fact]
    public void Shield_hp_pool_holds_the_boss_hp_until_emptied_and_expiry_runs_on_expire()
    {
        var rig = new Rig(Encounter("""
            { "id": "s", "when": { "type": "fight_start" }, "do": [{ "type": "shield", "id": "barrier", "hp": 5000, "on_break": [{ "type": "counter", "counter": "broke", "add": 1 }] }] },
            { "id": "e", "when": { "type": "timer", "after_ms": 1000 }, "do": [{ "type": "shield", "id": "timed", "invuln": true, "duration_ms": 1000,
              "on_expire": [{ "type": "counter", "counter": "expired", "add": 1 }] }] }
            """));
        rig.Tick(200);
        rig.Boss.Hp -= 3000;
        rig.Tick(100);
        Assert.Equal(100_000, rig.Boss.Hp);
        rig.Boss.Hp -= 4000;
        rig.Tick(100);
        Assert.Equal(98_000, rig.Boss.Hp);
        Assert.Equal(1, rig.Run.Counters["broke"]);
        rig.Tick(2000);
        Assert.Equal(1, rig.Run.Counters["expired"]);
        Assert.False(rig.Boss.Immune);
    }

    [Fact]
    public void Stagger_check_succeeds_when_the_boss_is_staggered_in_the_window_and_fails_otherwise()
    {
        var rig = new Rig(Encounter("""
            { "id": "a", "when": { "type": "timer", "after_ms": 100 }, "do": [{ "type": "stagger_check", "id": "gauge1", "window_ms": 2000,
              "on_success": [{ "type": "counter", "counter": "ok", "add": 1 }] }] },
            { "id": "b", "when": { "type": "timer", "after_ms": 3000 }, "do": [{ "type": "stagger_check", "id": "gauge2", "window_ms": 1000,
              "on_fail": [{ "type": "counter", "counter": "fail", "add": 1 }] }] },
            { "id": "watch", "when": { "type": "stagger_broken" }, "do": [{ "type": "counter", "counter": "broken", "add": 1 }] }
            """));
        rig.Tick(500);
        rig.Boss.StaggerUntil = rig.Now + 1000;
        rig.Tick(200);
        Assert.Equal(1, rig.Run.Counters["ok"]);
        Assert.Equal(1, rig.Run.Counters["broken"]);
        rig.Tick(4000);
        Assert.Equal(1, rig.Run.Counters["fail"]);
    }

    [Fact]
    public void Parts_break_from_damage_and_fire_part_broken()
    {
        var rig = new Rig(Encounter("""
            { "id": "p", "when": { "type": "fight_start" }, "do": [{ "type": "parts", "parts": [{ "id": "horn", "hp": 2000, "on_break": [{ "type": "counter", "counter": "on_break", "add": 1 }] }] }] },
            { "id": "pb", "when": { "type": "part_broken", "part": "horn" }, "do": [{ "type": "counter", "counter": "trigger", "add": 1 }] }
            """));
        rig.Tick(200);
        rig.Boss.Hp -= 1500;
        rig.Tick(100);
        Assert.False(rig.Run.Counters.ContainsKey("trigger"));
        rig.Boss.Hp -= 1000;
        rig.Tick(200);
        Assert.Equal(1, rig.Run.Counters["trigger"]);
        Assert.Equal(1, rig.Run.Counters["on_break"]);
    }

    [Fact]
    public void Parts_from_client_read_monster_parts_data()
    {
        const int boss = 2000063;
        var rig = new Rig(Encounter("""
            { "id": "p", "when": { "type": "fight_start" }, "do": [{ "type": "parts", "from_client": true }] }
            """).Replace(Npc.ToString(), boss.ToString()), npc: boss);
        rig.Tick(200);
        Assert.Equal(NpcBehaviorCases.Embedded.PartsFor(boss).Count, rig.Run.Parts.Count);
        Assert.All(rig.Run.Parts, p => Assert.Equal(2000, p.Hp));
    }

    // ---- debuffs / tank swap / orbs ---------------------------------------------------------------------------------

    [Fact]
    public void Debuff_stacks_reach_max_fire_stacks_reached_and_cleanse_clears_them()
    {
        var rig = new Rig(Encounter("""
            { "id": "d", "when": { "type": "timer", "after_ms": 100, "every_ms": 500, "count": 3 }, "do": [{ "type": "debuff", "debuff": "burn", "target": "victim", "max": 3,
              "on_max": [{ "type": "counter", "counter": "max", "add": 1 }] }] },
            { "id": "two", "when": { "type": "stacks_reached", "debuff": "burn", "stacks": 2 }, "do": [{ "type": "counter", "counter": "two", "add": 1 }] },
            { "id": "c", "when": { "type": "timer", "after_ms": 3000 }, "do": [{ "type": "cleanse", "debuff": "burn" }] }
            """));
        rig.Tick(1500);
        Assert.Equal(3, rig.Run.Stacks("burn", Tank));
        Assert.Equal(1, rig.Run.Counters["two"]);
        Assert.Equal(1, rig.Run.Counters["max"]);
        rig.Tick(2000);
        Assert.Equal(0, rig.Run.Stacks("burn", Tank));
    }

    [Fact]
    public void Tank_swap_turns_the_boss_to_another_player_at_max_stacks()
    {
        var rig = new Rig(Encounter("""
            { "id": "ts", "when": { "type": "timer", "after_ms": 100, "every_ms": 500, "count": 2 }, "do": [{ "type": "tank_swap", "debuff": "sunder", "max": 2 }] }
            """));
        rig.Place(P2, 400, 200);
        rig.Tick(800);
        Assert.Equal(P2, rig.Ai.VictimOf(rig.Boss));
        Assert.Equal(2, rig.Run.Stacks("sunder", Tank));
    }

    [Fact]
    public void Orb_counts_touches_and_completes_or_fails_in_its_window()
    {
        var rig = new Rig(Encounter("""
            { "id": "o", "when": { "type": "fight_start" }, "do": [{ "type": "orb", "id": "lights", "at": { "x": 2000, "y": 0 }, "offsets": [[0, 0], [0, 1000]], "radius": 200,
              "required": 2, "window_ms": 3000, "env_obj": 1400623, "on_complete": [{ "type": "counter", "counter": "done", "add": 1 }] }] },
            { "id": "o2", "when": { "type": "timer", "after_ms": 4000 }, "do": [{ "type": "orb", "id": "late", "at": { "x": -3000, "y": 0 }, "radius": 200, "required": 1,
              "window_ms": 1000, "on_fail": [{ "type": "counter", "counter": "missed", "add": 1 }] }] }
            """));
        rig.Tick(200);
        Assert.Equal(2, rig.EnvEvents.Count(e => e.SpawnEnv));
        rig.Place(P2, 2000, 0);
        rig.Tick(200);
        Assert.False(rig.Run.Counters.ContainsKey("done"));
        rig.Place(P2, 2000, 1000);
        rig.Tick(200);
        Assert.Equal(1, rig.Run.Counters["done"]);
        Assert.Equal(2, rig.EnvEvents.Count(e => e.Despawn));
        rig.Tick(5000);
        Assert.Equal(1, rig.Run.Counters["missed"]);
    }

    // ---- flow -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Counter_reached_delay_random_and_emit_drive_rules()
    {
        var rig = new Rig(Encounter("""
            { "id": "count", "when": { "type": "timer", "after_ms": 100, "every_ms": 100, "count": 3 }, "do": [{ "type": "counter", "counter": "c", "add": 1 }] },
            { "id": "three", "when": { "type": "counter_reached", "counter": "c", "value": 3 }, "do": [{ "type": "delay", "ms": 500, "do": [{ "type": "emit", "event": "go" }] }] },
            { "id": "go", "when": { "type": "event", "event": "go" }, "do": [{ "type": "random", "choices": [{ "weight": 0, "do": [{ "type": "counter", "counter": "never", "add": 1 }] },
              { "weight": 1, "do": [{ "type": "counter", "counter": "picked", "add": 1 }] }] }] }
            """));
        rig.Tick(500);
        Assert.False(rig.Run.Counters.ContainsKey("picked"));
        rig.Tick(600);
        Assert.Equal(1, rig.Run.Counters["picked"]);
        Assert.False(rig.Run.Counters.ContainsKey("never"));
    }

    [Fact]
    public void Wipe_kills_everyone_and_notice_caption_env_and_decal_send_packets()
    {
        var rig = new Rig(Encounter("""
            { "id": "show", "when": { "type": "fight_start" }, "do": [
              { "type": "notice", "text": "Run!", "text_ru": "Бегите!" },
              { "type": "caption", "talk": "STR_TEST_TALK" },
              { "type": "env", "env_obj": 1400533, "at": "boss", "duration_ms": 500 },
              { "type": "decal", "skill": 1800040, "at": "victim", "duration_ms": 2000 }] },
            { "id": "end", "when": { "type": "timer", "after_ms": 1000 }, "do": [{ "type": "wipe" }] }
            """));
        rig.Place(P2, 500, 500);
        var first = rig.Tick(100);
        Assert.NotEmpty(first);
        Assert.Contains(rig.EnvEvents, e => e.SpawnEnv && e.EnvObjIds!.Contains(1400533));
        rig.Tick(600);
        Assert.Contains(rig.EnvEvents, e => e.Despawn);
        rig.Tick(600);
        Assert.Equal([Tank, P2], rig.Hits.Where(h => h.Damage == NpcAi.LethalDamage).Select(h => h.Key).Order());
    }

    [Fact]
    public void Modify_and_set_rotation_change_the_boss()
    {
        var rig = new Rig(Encounter("""
            { "id": "m", "when": { "type": "hp_below", "pct": 50 }, "do": [{ "type": "modify", "mode": "mul", "damage_mul": 1.5, "haste_mul": 0.8 },
              { "type": "set_rotation", "rotation": [{ "type": "cast", "skill": 1800040, "every_ms": 5000, "wind_up_ms": 1000 }] }] }
            """));
        rig.Hp(40);
        rig.Tick(200);
        var fight = rig.Ai.FightOf(rig.Boss)!;
        Assert.Equal((1.5, 0.8), (fight.Damage, fight.Haste));
        Assert.Equal(1800040, Assert.Single(fight.Rotation).Skill);
    }

    // ---- casts through the slam pipeline ----------------------------------------------------------------------------

    [Fact]
    public void Cast_winds_up_through_the_slam_pipeline_and_runs_during_and_on_land()
    {
        var rig = new Rig(Encounter("""
            { "id": "big", "when": { "type": "hp_below", "pct": 90 }, "do": [{ "type": "cast", "skill": 1800040, "wind_up_ms": 2500, "land": false,
              "during": [{ "type": "counter", "counter": "during", "add": 1 }], "on_land": [{ "type": "counter", "counter": "landed", "add": 1 }] }] }
            """));
        rig.Tick(200);
        rig.Hp(85);
        rig.Tick(200);
        Assert.NotNull(rig.Ai.TelegraphDueAt(rig.Boss));
        Assert.Equal(1, rig.Run.Counters["during"]);
        Assert.False(rig.Run.Counters.ContainsKey("landed"));
        rig.Tick(4000);
        Assert.Equal(1, rig.Run.Counters["landed"]);
        Assert.True(rig.Logged("cast", "landed"));
    }

    [Fact]
    public void A_broken_cast_runs_on_break_and_counts_as_a_stagger_success()
    {
        var rig = new Rig(Encounter("""
            { "id": "big", "when": { "type": "hp_below", "pct": 90 }, "do": [{ "type": "cast", "id": "doom", "skill": 1800040, "wind_up_ms": 5000, "stagger_check": true,
              "on_break": [{ "type": "counter", "counter": "broken", "add": 1 }] }] },
            { "id": "won", "when": { "type": "event", "event": "doom.success" }, "do": [{ "type": "counter", "counter": "won", "add": 1 }] }
            """));
        rig.Tick(200);
        rig.Hp(85);
        rig.Tick(500);
        Assert.NotNull(rig.Ai.TelegraphDueAt(rig.Boss));
        rig.Boss.Held = true;
        rig.Tick(200);
        Assert.Null(rig.Ai.TelegraphDueAt(rig.Boss));
        Assert.Equal(1, rig.Run.Counters["broken"]);
        Assert.Equal(1, rig.Run.Counters["won"]);
    }

    [Fact]
    public void The_engine_log_records_rules_and_actions()
    {
        var rig = new Rig(Encounter("""
            { "id": "start", "when": { "type": "fight_start" }, "do": [{ "type": "notice", "text": "Hi" }] }
            """));
        rig.Tick(100);
        Assert.Contains(rig.Run.Log, l => l.Rule == "fight" && l.Action == "start");
        Assert.Contains(rig.Run.Log, l => l.Rule == "start" && l.Action == "rule:fight_start");
        Assert.Contains(rig.Run.Log, l => l.Rule == "start" && l.Action == "notice" && l.Targets == 1);
    }

    [Fact]
    public void Engine_off_leaves_authored_bosses_on_the_legacy_path()
    {
        var script = BossPhases.Embedded.Of(2300371)!;
        var ai = new NpcAi(new Random(1), GameData.Embedded)
        {
            Bosses = new BossPhases(new Dictionary<int, BossScript> { [2300371] = script }),
            BossRules = BossOptions.Default with { Engine = false },
        };
        var boss = new Target { Key = 1, NpcId = 2300371, Name = "K", Hp = 100, HpMax = 100, Level = 63, Position = Vector3d.Zero, WalkSpeed = 1, RunSpeed = 1 };
        var map = new Dictionary<int, Target> { [1] = boss };
        ai.Engage(map, boss, 0, Tank);
        ai.Tick(map, [(Tank, new Vector3d(300, 0, 0))], 100, (_, _) => []);
        Assert.NotNull(ai.FightOf(boss));
        Assert.Null(ai.EncounterOf(boss));
    }
}
