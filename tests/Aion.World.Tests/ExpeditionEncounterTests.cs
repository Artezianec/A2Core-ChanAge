using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// The Krao Cave, Urugugu Canyon, Fire Temple, Draupnir Cave, Vakron Sky Island and Ferocious Horn Den encounters
/// (tools/data/boss-mechanics) running on the real boss families on every difficulty.
/// </summary>
public sealed class ExpeditionEncounterTests
{
    const int BossKey = 9301, Tank = 100001, P2 = 100002, P3 = 100003;

    /// <summary>Normal-difficulty NPC id of every boss the six dungeons author (Easy is +10000, Hard +20000).</summary>
    public static TheoryData<int> Families => new()
    {
        2300104, 2300108, 2300206, 2300207, 2300310, 2300311, 2300312, 2300401, 2300403, 2300810, 2300811, 2300812, 2300908, 2300909,
        2300171, 2300371,
    };

    sealed class Fight
    {
        public NpcAi Ai { get; }
        public Dictionary<int, Target> Map { get; } = [];
        public Target Boss { get; }
        public List<(int Key, Vector3d Position)> Players { get; }
        public List<(int Key, int Damage, long At)> Hits { get; } = [];
        public List<(int Victim, int Abnormal, long Ms)> Debuffs { get; } = [];
        public long Now { get; private set; }

        public Fight(int npc, params (int Key, Vector3d Position)[] players)
        {
            Players = [.. players.Length == 0 ? [(Tank, new Vector3d(300, 0, 0))] : players];
            Boss = new Target
            {
                Key = BossKey, NpcId = npc, Name = BossPhases.Embedded.Of(npc)!.Name, Hp = 1_000_000, HpMax = 1_000_000, Level = 63,
                Position = Vector3d.Zero, WalkSpeed = 0.001, RunSpeed = 0.001, Radius = 150, SubType = "HeroMonster", Grade = 2,
            };
            Map[BossKey] = Boss;
            Ai = new NpcAi(new Random(11), GameData.Embedded)
            {
                Bosses = new BossPhases(new Dictionary<int, BossScript> { [npc] = BossPhases.Embedded.Of(npc)! }),
                BossRules = BossOptions.Default with { Engine = true },
            };
            Ai.MechanicDebuffHook = (victim, abnormal, _, ms) => { Debuffs.Add((victim, abnormal, ms)); return []; };
            Ai.Engage(Map, Boss, 0, Players[0].Key);
        }

        public EncounterRun Run => Ai.EncounterOf(Boss) ?? throw new InvalidOperationException("no engine fight");

        public void Tick(long ms, long step = 100)
        {
            for (var end = Now + ms; Now < end;)
            {
                Now += step;
                Func<int, int, List<byte[]>> hit = (key, damage) => { Hits.Add((key, damage, Now)); return []; };
                Ai.Tick(Map, Players, Now, hit);
                Ai.TickExpeditionMechanics(Now, Players, hit);
            }
        }

        public void Hp(double percent) => Boss.Hp = (int)(Boss.HpMax * percent / 100);
    }

    static int Difficulty(string name, int normal) => name switch { "Easy" => normal + 10_000, "Hard" => normal + 20_000, _ => normal };

    [Fact]
    public void The_embedded_catalog_has_no_errors()
    {
        Assert.Empty(BossEncounters.Embedded.Errors);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Every_boss_is_authored_on_all_three_difficulties(int normal)
    {
        foreach (var difficulty in new[] { "Easy", "Normal", "Hard" })
        {
            var npc = Difficulty(difficulty, normal);
            Assert.True(BossEncounters.Embedded.Authored(npc), $"{npc} is not authored");
            var encounter = BossEncounters.Embedded.Resolve(npc, BossPhases.Embedded)!;
            Assert.False(encounter.Compiled);
            Assert.Equal(difficulty, encounter.Difficulty);
        }
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Every_boss_keeps_its_generic_fury_or_phases_and_casts_its_rotation(int normal)
    {
        foreach (var difficulty in new[] { "Easy", "Normal", "Hard" })
        {
            var npc = Difficulty(difficulty, normal);
            var script = BossPhases.Embedded.Of(npc)!;
            var encounter = BossEncounters.Embedded.Resolve(npc, BossPhases.Embedded)!;
            if (script.Role == "mid") Assert.Contains(encounter.Rules, r => r.Id == "fury_50");
            Assert.NotEmpty(encounter.Rotation);
        }
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Two_minutes_of_every_fight_run_without_engine_errors_and_with_casts(int normal)
    {
        foreach (var difficulty in new[] { "Easy", "Normal", "Hard" })
        {
            var f = new Fight(Difficulty(difficulty, normal), (Tank, new Vector3d(300, 0, 0)), (P2, new Vector3d(-400, 200, 0)), (P3, new Vector3d(0, -900, 0)));
            var winds = new HashSet<int>();
            for (var step = 0; step < 130; step++)
            {
                f.Tick(1000);
                if (f.Ai.FightOf(f.Boss)?.Winding is { } w) winds.Add(w.Slam.Skill);
                f.Hp(Math.Max(2, 100 - step * 0.75));
            }
            Assert.DoesNotContain(f.Run.Log, l => l.Result.StartsWith("error", StringComparison.Ordinal));
            Assert.True(winds.Count >= 2, $"{difficulty} {normal}: only {winds.Count} distinct casts ({string.Join(",", winds)})");
        }
    }

    [Fact]
    public void Raur_raises_a_reflect_shield_at_70_percent_and_punishes_hitting_through_it()
    {
        var f = new Fight(2300206);
        f.Tick(2000);
        f.Hp(69);
        f.Tick(4500);
        Assert.True(f.Run.Shields.TryGetValue("reflect", out var shield), "no reflect shield");
        Assert.True(shield!.Pool > 0);
        f.Boss.Hp -= (int)(f.Boss.HpMax * 0.05);
        f.Tick(3000);
        Assert.DoesNotContain("reflect", f.Run.Shields.Keys);
        Assert.Contains(f.Hits, h => h.Key == Tank && h.Damage > 20_000);
    }

    [Fact]
    public void Urahums_acid_leaves_a_pool_where_the_player_stands_when_the_mark_ends()
    {
        var f = new Fight(2300207);
        var zones = new List<(long At, Vector3d Centre)>();
        for (var t = 0; t < 70_000; t += 100)
        {
            f.Tick(100);
            if (f.Run.Zones.FirstOrDefault(z => z.Id == "acid_pool") is { } zone && zones.Count == 0) zones.Add((f.Now, zone.Centres[0].Centre));
            if (f.Now == 4000) f.Players[0] = (Tank, new Vector3d(300, 0, 0));
        }
        Assert.NotEmpty(zones);
        Assert.InRange(zones[0].Centre.X, 150, 450);
    }

    [Theory]
    [InlineData("Normal", 1)]
    [InlineData("Hard", 2)]
    public void Vakrons_thorn_prison_marks_one_player_and_two_on_hard(string difficulty, int marked)
    {
        var f = new Fight(Difficulty(difficulty, 2300812), (Tank, new Vector3d(300, 0, 0)), (P2, new Vector3d(-400, 200, 0)), (P3, new Vector3d(0, -900, 0)));
        f.Tick(13_000);
        var thorns = f.Debuffs.Where(d => d.Abnormal == 16043811).ToList();
        Assert.Equal(marked, thorns.Select(d => d.Victim).Distinct().Count());
        Assert.All(thorns, d => Assert.Equal(4000, d.Ms));
        f.Tick(5000);
        Assert.Contains(f.Hits, h => h.Damage > 0 && f.Debuffs.Any(d => d.Abnormal == 12000021 && d.Victim == h.Key));
    }

    [Fact]
    public void Vakron_keeps_the_generic_add_calls_enrage_and_the_hard_frenzy_clock()
    {
        var normal = BossEncounters.Embedded.Resolve(2300812, BossPhases.Embedded)!;
        var easy = BossEncounters.Embedded.Resolve(2310812, BossPhases.Embedded)!;
        var hard = BossEncounters.Embedded.Resolve(2320812, BossPhases.Embedded)!;
        Assert.Contains(normal.Rules, r => r.Id == "enrage_20");
        Assert.DoesNotContain(easy.Rules, r => r.Id == "enrage_20" || r.Id == "frenzy");
        Assert.Equal(240_000, hard.Vars["frenzy_ms"]);
        Assert.Equal(300_000, normal.Vars["frenzy_ms"]);
        Assert.Equal(13_500, hard.Vars["filler_fury_ms"]);
        Assert.Contains(hard.Rules, r => r.Id == "thorn_prison_2");
        Assert.DoesNotContain(normal.Rules, r => r.Id == "thorn_prison_2");

        var f = new Fight(2320812);
        f.Tick(1000);
        f.Hp(69);
        f.Tick(500);
        var call = Assert.Single(f.Ai.TakeSummons());
        Assert.Equal((2320802, 4), (call.Adds.Npc, call.Adds.Count));
    }

    [Theory]
    [InlineData("Easy", 1)]
    [InlineData("Normal", 2)]
    [InlineData("Hard", 2)]
    public void Kuhapus_thunder_cry_makes_the_boss_invulnerable_and_spawns_the_client_orb_count(string difficulty, int orbs)
    {
        var f = new Fight(Difficulty(difficulty, 2300908));
        f.Tick(24_000);
        Assert.True(f.Run.Shields.TryGetValue("thunder", out var shield), "no thunder shield");
        Assert.True(shield!.Invuln);
        Assert.True(f.Boss.Immune);
        var orb = Assert.Single(f.Run.Orbs);
        Assert.Equal(orbs, orb.Orbs.Count);
    }

    [Theory]
    [InlineData("Easy", 2)]
    [InlineData("Normal", 4)]
    [InlineData("Hard", 4)]
    public void Duankas_necromancy_summons_linked_spirits_and_locks_the_boss_until_they_die(string difficulty, int spirits)
    {
        var f = new Fight(Difficulty(difficulty, 2300909));
        f.Tick(18_000);
        var request = Assert.Single(f.Ai.TakeSummons());
        Assert.Equal(spirits, request.Adds.Count);
        Assert.True(f.Run.Shields.ContainsKey("necromancy"));
        Assert.True(f.Boss.Immune);
    }

    [Fact]
    public void Tamons_colour_state_soaks_damage_for_ten_seconds()
    {
        var f = new Fight(2300811);
        f.Tick(23_000);
        Assert.True(f.Run.Shields.TryGetValue("colour", out var shield), "no colour state");
        Assert.True(shield!.Pool > 0 && !double.IsInfinity(shield.Pool));
        f.Tick(11_000);
        Assert.DoesNotContain("colour", f.Run.Shields.Keys);
    }

    [Theory]
    [InlineData("Normal", false)]
    [InlineData("Hard", true)]
    public void Kromedes_clones_come_at_75_percent_on_hard_and_50_percent_otherwise(string difficulty, bool at75)
    {
        var f = new Fight(Difficulty(difficulty, 2300371));
        f.Tick(500);
        f.Hp(74);
        f.Tick(800);
        Assert.Equal(at75, f.Run.Phase == "clones");
        f.Hp(49);
        f.Tick(800);
        Assert.Equal("clones", f.Run.Phase);
    }

    [Fact]
    public void Berks_whirlwind_pulses_and_the_rocks_start_below_half_health()
    {
        var f = new Fight(2300171);
        f.Tick(32_000);
        Assert.Contains(f.Run.Log, l => l.Rule == "whirlwind");
        Assert.Contains(f.Debuffs, d => d.Abnormal == 12000151 && d.Ms == 20_000);
        f.Hp(49);
        f.Tick(9000);
        Assert.Equal("rocks", f.Run.Phase);
        Assert.Contains(f.Run.Log, l => l.Rule == "rocks");
    }

    [Fact]
    public void The_event_player_anchor_follows_the_player_and_the_debuff_hook_gets_the_client_abnormal()
    {
        var rules = """
            { "id": "acid", "when": { "type": "fight_start" }, "do": [
                { "type": "telegraph", "at": "victim", "shape": "circle", "radius": 200, "delay_ms": 100, "decal": false, "damage": { "fixed": [7777, 7777] },
                  "on_hit": [ { "type": "debuff", "debuff": "mark", "target": "event_player", "max": 1, "duration_ms": 3000, "abnormal": 16051103,
                                "on_max": [ { "type": "delay", "ms": 1000, "do": [ { "type": "zone", "id": "pool", "at": "event_player", "shape": "circle", "radius": 100,
                                                                                    "duration_ms": 5000, "decal": false, "damage": { "fixed": [7777, 7777] } } ] } ] } ] } ] }
            """;
        var rig = new BossEngineTests.Rig(BossEngineTests.Encounter(rules));
        var seen = new List<(int Victim, int Abnormal, long Ms)>();
        rig.Ai.MechanicDebuffHook = (victim, abnormal, _, ms) => { seen.Add((victim, abnormal, ms)); return []; };
        rig.Tick(300);
        rig.Place(BossEngineTests.Tank, 900, 0);
        rig.Tick(1500);
        Assert.Equal((BossEngineTests.Tank, 16051103, 3000L), Assert.Single(seen));
        var pool = rig.Run.Zones.Single(z => z.Id == "pool");
        Assert.Equal(900, pool.Centres[0].Centre.X, 1);
    }
}
