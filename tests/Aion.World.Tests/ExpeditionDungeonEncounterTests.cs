using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>The authored expedition encounters (DDN, Cradle of Nihility, Hall of Illusion, Azure Breath Island, Citadel of the Fallen Daeva) and the generic engine pieces they rely on.</summary>
public sealed class ExpeditionDungeonEncounterTests
{
    const int Tank = BossEngineTests.Tank, P2 = BossEngineTests.P2, P3 = BossEngineTests.P3;

    static readonly (string File, string Name, int[] Npcs, bool Final)[] Bosses =
    [
        ("ddn_nayatman", "Nayatman", [2300507, 2310507, 2300153], false),
        ("ddn_nazium", "Nazium", [2300508, 2310508, 2300154], false),
        ("ddn_durbati", "Durbati", [2300509, 2310509, 2300155], true),
        ("cradle_beltiras", "Beltiras", [2301302, 2311302, 2321302], false),
        ("cradle_balkan", "Balkan", [2301303, 2311303, 2321303], false),
        ("cradle_bakarma", "Bakarma", [2301301, 2311301, 2321301], true),
        ("hall_glassbane", "Glassbane", [2301401, 2311401, 2321401], false),
        ("hall_nasarak", "Nasarak", [2301402, 2311402, 2321402], false),
        ("hall_nathara", "Nathara", [2301403, 2311403, 2321403], true),
        ("azure_baumnute", "Baumnute", [2300189, 2300219, 2300244], false),
        ("azure_xyrepe", "Xyrepe", [2300190, 2300230, 2300245], false),
        ("azure_alturadon", "Alturadon", [2300188, 2300200, 2300243], true),
        ("citadel_turgen", "Turgen", [2301601, 2301701, 2301721], false),
        ("citadel_griosa", "Griosa", [2301602, 2301702, 2301722], false),
        ("citadel_basilus", "Basilus", [2301603, 2301703, 2301723], true),
    ];

    static string Json(string file)
    {
        using var stream = typeof(BossEncounters).Assembly.GetManifestResourceStream($"boss-mechanics/{file}.json")!;
        return new StreamReader(stream).ReadToEnd();
    }

    static BossEngineTests.Rig RigOf(string file, int npc, int hp = 100_000, string? replace = null, string? with = null)
    {
        var json = Json(file);
        if (replace is not null) json = json.Replace(replace, with ?? "");
        return new BossEngineTests.Rig(json, hp, npc: npc);
    }

    [Fact]
    public void Every_boss_has_an_authored_encounter_on_every_difficulty_and_the_catalog_has_no_errors()
    {
        var catalog = BossEncounters.Embedded;
        Assert.Empty(catalog.Errors);
        foreach (var (file, name, npcs, _) in Bosses)
            foreach (var npc in npcs)
            {
                Assert.True(catalog.Authored(npc), $"{name} {npc} has no authored encounter");
                Assert.Equal(file, catalog.Resolve(npc, BossPhases.Embedded)!.Def.Id);
            }
    }

    [Fact]
    public void Finals_enrage_on_normal_and_hard_but_not_on_exploration_and_mid_bosses_never_do()
    {
        foreach (var (_, name, npcs, final) in Bosses)
        {
            var (easy, normal, hard) = (BossEncounters.Embedded.Resolve(npcs[0], BossPhases.Embedded)!, BossEncounters.Embedded.Resolve(npcs[1], BossPhases.Embedded)!,
                BossEncounters.Embedded.Resolve(npcs[2], BossPhases.Embedded)!);
            Assert.Equal(("Easy", "Normal", "Hard"), (easy.Difficulty, normal.Difficulty, hard.Difficulty));
            Assert.Equal(final, normal.Rules.Any(r => r.Id == "frenzy"));
            Assert.Equal(final, hard.Rules.Any(r => r.Id == "frenzy"));
            Assert.DoesNotContain(easy.Rules, r => r.Id == "frenzy");
            if (final) Assert.Equal(300_000, normal.Vars["frenzy_ms"]);
            Assert.Contains(normal.Rules, r => r.Id == "fury_50");
            Assert.NotEmpty(normal.Rotation);
        }
    }

    [Fact]
    public void Existing_slams_are_kept_in_the_authored_rotations()
    {
        var script = BossPhases.Embedded;
        foreach (var (file, name, npcs, _) in Bosses)
        {
            var legacy = script.Of(npcs[1]);
            var slam = legacy?.Rotation.FirstOrDefault()?.Skill ?? legacy?.Ordered.Select(p => p.Telegraph).FirstOrDefault(t => t is not null)?.Skill;
            if (slam is null) continue;
            var resolved = BossEncounters.Embedded.Resolve(npcs[1], script)!;
            Assert.Contains(resolved.Rotation, c => c.Skill == slam);
        }
    }

    // ---- engine additions ---------------------------------------------------------------------------------------------

    [Fact]
    public void Hp_ratio_damage_is_a_share_of_the_victims_max_hp()
    {
        var rig = new BossEngineTests.Rig(BossEngineTests.Encounter("""
            { "id": "r", "when": { "type": "fight_start" }, "do": [{ "type": "telegraph", "at": "victim", "shape": "circle", "radius": 500, "delay_ms": 0, "decal": false,
              "damage": { "hp_ratio": 2500 } }] }
            """));
        rig.Ai.PlayerHpOf = _ => (20_000, 20_000);
        rig.Tick(300);
        Assert.Contains(rig.Hits, h => h.Key == Tank && h.Damage == 5000);
    }

    [Fact]
    public void An_inverted_shape_at_several_offsets_only_hits_players_outside_every_copy()
    {
        var rig = new BossEngineTests.Rig(BossEngineTests.Encounter("""
            { "id": "rocks", "when": { "type": "fight_start" }, "do": [{ "type": "telegraph", "at": "boss", "offsets": [[300, 0], [-500, 0]], "shape": "circle", "radius": 400,
              "invert": true, "delay_ms": 0, "decal": false, "damage": { "fixed": [7777, 7777] } }] }
            """));
        rig.Place(P2, 300, 900);
        rig.Place(P3, -500, 100);
        rig.Tick(300);
        Assert.Equal([P2], rig.Marked());
    }

    [Fact]
    public void A_soak_over_several_spots_succeeds_when_a_player_stands_in_any_of_them()
    {
        var rig = new BossEngineTests.Rig(BossEngineTests.Encounter("""
            { "id": "pillars", "when": { "type": "timer", "after_ms": 200 }, "do": [{ "type": "soak", "id": "bait", "at": "boss", "offsets": [[-4000, 0], [0, 4000]], "radius": 500,
              "delay_ms": 500, "decal": false, "on_success": [{ "type": "counter", "counter": "ok", "add": 1 }], "on_fail": [{ "type": "counter", "counter": "fail", "add": 1 }] }] },
            { "id": "again", "when": { "type": "timer", "after_ms": 2000 }, "do": [{ "type": "soak", "id": "bait2", "at": "boss", "offsets": [[-4000, 0], [0, 4000]], "radius": 500,
              "delay_ms": 500, "decal": false, "on_success": [{ "type": "counter", "counter": "ok", "add": 1 }], "on_fail": [{ "type": "counter", "counter": "fail", "add": 1 }] }] }
            """));
        rig.Tick(1000);
        Assert.Equal(1, rig.Run.Counters["fail"]);
        rig.Place(P2, 0, 4100);
        rig.Tick(2000);
        Assert.Equal(1, rig.Run.Counters["ok"]);
    }

    [Fact]
    public void A_debuff_with_an_abnormal_reaches_the_mechanic_debuff_hook()
    {
        var seen = new List<(int Key, int Abnormal, long Ms)>();
        var rig = new BossEngineTests.Rig(BossEngineTests.Encounter("""
            { "id": "d", "when": { "type": "fight_start" }, "do": [{ "type": "debuff", "debuff": "fear", "target": "each_player", "duration_ms": 3000, "abnormal": 16043307 }] }
            """));
        rig.Ai.MechanicDebuffHook = (key, abnormal, _, ms) => { seen.Add((key, abnormal, ms)); return []; };
        rig.Tick(300);
        Assert.Contains((Tank, 16043307, 3000L), seen);
    }

    // ---- dungeon encounters -------------------------------------------------------------------------------------------

    [Fact]
    public void Alturadon_wipes_the_party_when_ten_breath_orbs_are_left_standing()
    {
        var rig = RigOf("azure_alturadon", 2300200);
        rig.Place(P2, 5000, 5000);
        rig.Tick(37_000);
        Assert.Equal(10, rig.Run.Counters["blue_breath"]);
        Assert.Contains(rig.Hits, h => h.Damage == NpcAi.LethalDamage);
    }

    [Fact]
    public void Alturadon_orbs_that_players_take_never_become_stacks()
    {
        var rig = RigOf("azure_alturadon", 2300200);
        rig.Tick(20_500);
        var spots = new (double X, double Y)[] { (0, 3500), (-2057, 2832), (-3329, 1082), (-3329, -1082), (-2057, -2832), (0, -3500), (2057, -2832), (3329, -1082), (3329, 1082), (2057, 2832) };
        foreach (var (x, y) in spots)
        {
            rig.Place(P2, x, y);
            rig.Tick(200);
        }
        rig.Tick(16_000);
        Assert.False(rig.Run.Counters.TryGetValue("blue_breath", out var stacks) && stacks > 0);
    }

    [Fact]
    public void Baumnute_veil_strikes_only_players_beyond_eight_metres()
    {
        var rig = RigOf("azure_baumnute", 2300219);
        rig.Place(P2, 5000, 0);
        rig.Tick(30_000);
        Assert.Contains(rig.Hits, h => h.Key == P2 && h.At > 20_000);
        Assert.Equal(800, rig.Run.Encounter.Vars["veil_radius"]);
    }

    [Fact]
    public void Basilus_gold_circles_left_unfilled_wipe_the_party_after_two_explosions()
    {
        var rig = RigOf("citadel_basilus", 2301703);
        rig.Place(P2, 6000, 6000);
        rig.Tick(60_000);
        Assert.True(rig.Run.Counters["gold_fail"] >= 2);
        Assert.Contains(rig.Hits, h => h.Damage == NpcAi.LethalDamage);
    }

    [Fact]
    public void Cradle_bakarma_line_share_kills_players_outside_the_lane()
    {
        var rig = RigOf("cradle_bakarma", 2311301);
        rig.Place(P2, 300, 200);
        rig.Place(P3, 5000, 5000);
        rig.Tick(30_000);
        Assert.Contains(rig.Hits, h => h.Key == P3 && h.Damage == NpcAi.LethalDamage);
        Assert.DoesNotContain(rig.Hits, h => h.Key == P2 && h.Damage == NpcAi.LethalDamage);
    }

    [Fact]
    public void Durbati_swamp_spares_players_on_the_rocks_and_hits_the_rest()
    {
        var rig = RigOf("ddn_durbati", 2310509);
        rig.Place(P2, -3586, 1369);
        rig.Place(P3, 1500, 1500);
        rig.Tick(55_000);
        var swamp = rig.Hits.Where(h => h.At > 49_000 && h.At < 51_000).ToList();
        Assert.Contains(swamp, h => h.Key == P3);
        Assert.Contains(swamp, h => h.Key == Tank);
        Assert.DoesNotContain(swamp, h => h.Key == P2);
    }

    [Fact]
    public void Nathara_split_illusion_summons_two_copies_and_wipes_when_they_live()
    {
        var rig = RigOf("hall_nathara", 2311403);
        rig.Hp(55);
        rig.Tick(40_000);
        Assert.True(rig.Logged("summon"));
        Assert.Contains(rig.Hits, h => h.Damage == NpcAi.LethalDamage);
    }
}
