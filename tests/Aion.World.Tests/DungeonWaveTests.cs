using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Scripted dungeon waves (GameSession.Bosses; ported from ckrealm/A2Core c299686, AGPL-3.0). The Ludra raid's StartWave names
/// waves whose monsters are the client spawn entries "{wave}_{n}": they wait dormant, come part by part, and the wave is clear
/// (ClearWave) when every part came and died. Ferocious Horn Den's totem wave has no such spawns: a hand-made wave (OURS).
/// </summary>
public sealed class DungeonWaveTests
{
    const long Now = 1_760_200_000_000;
    const string Wave = "ReforgedAbyss_01_Wave_01";

    static async Task<GameSession> EnterAsync(int map)
    {
        // A staff character enters past the level and item-level gates; the waves do not care who entered.
        var store = new FakeCharacterStore { Progress = (60, 0) };
        var session = new GameSession(GameData.Embedded, new GameSessionOptions
        {
            World = new SharedWorld(GameData.Embedded), CharacterLevel = 60, WorldId = store.WorldId, TrainingDummy = false,
            StaffRole = (_, _) => Task.FromResult<string?>("gamemaster"),
        }, new Random(19)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        store.States[0] = store.States[0] with { Staff = true };
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await MapEventScenarioHelpers.EnterDungeonByIdAsync(session, MapEventTables.Embedded.DungeonIdOfMap(map), Now + 100);
        Assert.True(session.MapId == map, string.Join(" | ", session.MapEventNotes.TakeLast(5)));
        for (var i = 0; i < 4; i++) await session.TickAsync(Now + 200 + i * 50);
        return session;
    }

    static List<Target> Part(GameSession session, string name) =>
        session.Targets.Values.Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();

    [Fact]
    public async Task Ludra_waves_wait_dormant_then_come_part_by_part_and_clear_when_all_died()
    {
        var session = await EnterAsync(600082);
        Assert.True(session.KnownWaves.TryGetValue(Wave, out var parts));
        Assert.True(parts!.Count >= 2);
        Assert.Equal(parts, parts.OrderBy(p => int.Parse(p[(p.LastIndexOf('_') + 1)..])).ToList());
        Assert.All(parts, p => Assert.All(Part(session, p), t => Assert.True(t.Dead, $"{t.Name} is up before its StartWave")));

        var host = (IMapEventHost)session;
        Assert.True(host.WaveKnown(Wave));
        host.StartWave(Wave);
        var t = Now + 1_000;
        await session.TickAsync(t);
        Assert.NotEmpty(Part(session, parts[0]));
        Assert.All(Part(session, parts[0]), x => Assert.False(x.Dead));
        Assert.All(Part(session, parts[1]), x => Assert.True(x.Dead));
        Assert.Contains(Wave, session.RunningWaves);

        // Killing a part brings the next; the last part's death clears the wave.
        for (var i = 0; i < parts.Count; i++)
        {
            foreach (var x in Part(session, parts[i])) x.Hp = 0;
            t += 500;
            await session.TickAsync(t);
            if (i + 1 < parts.Count) Assert.All(Part(session, parts[i + 1]), x => Assert.False(x.Dead, $"{x.Name} did not come after part {i + 1}"));
        }
        t += 500;
        await session.TickAsync(t);
        Assert.Contains(Wave, session.ClearedWaves);
        Assert.True(host.WaveDone(Wave));
    }

    [Fact]
    public async Task A_wave_part_left_alive_is_followed_by_the_next_after_WavePartSeconds()
    {
        var session = await EnterAsync(600082);
        var parts = session.KnownWaves[Wave];
        ((IMapEventHost)session).StartWave(Wave);
        await session.TickAsync(Now + 1_000);
        await session.TickAsync(Now + 1_000 + GameSession.BossRules.WavePartSeconds * 1000L - 2_000);
        Assert.All(Part(session, parts[1]), x => Assert.True(x.Dead));
        await session.TickAsync(Now + 1_000 + GameSession.BossRules.WavePartSeconds * 1000L + 1_000);
        Assert.All(Part(session, parts[1]), x => Assert.False(x.Dead));
        Assert.DoesNotContain(Wave, session.ClearedWaves);
    }

    [Fact]
    public async Task EndWave_takes_its_monsters_away()
    {
        var session = await EnterAsync(600082);
        var parts = session.KnownWaves[Wave];
        var host = (IMapEventHost)session;
        host.StartWave(Wave);
        await session.TickAsync(Now + 1_000);
        host.EndWave(Wave);
        await session.TickAsync(Now + 1_500);
        Assert.All(Part(session, parts[0]), x => Assert.True(x.Dead));
        Assert.Empty(session.RunningWaves);
    }

    [Fact]
    public async Task A_wave_the_spawns_do_not_name_runs_its_hand_made_pulses_and_clears()
    {
        const string totemWave = "FierceHornCave_Wave_01";
        var session = await EnterAsync(600092);
        var wave = BossPhases.Embedded.Wave(600092, totemWave)!;
        Assert.False(session.KnownWaves.ContainsKey(totemWave));
        var host = (IMapEventHost)session;
        Assert.True(host.WaveKnown(totemWave));
        var anchor = session.Targets.Values.FirstOrDefault(x => x.Name.StartsWith(wave.Around, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(anchor);
        anchor!.Hp = anchor.HpMax;   // the totem stands while the wave runs
        anchor.CorpseRemoved = false;
        host.StartWave(totemWave);
        var t = Now + 1_000;
        await session.TickAsync(t);
        var summoned = session.Targets.Values.Where(x => BossSummons.IsSummon(x.Key) && !x.Dead).ToList();
        Assert.Equal(wave.Count, summoned.Count);
        Assert.All(summoned, x => Assert.Equal(wave.Npc, x.NpcId));

        // The totem and its adds die: the wave is clear and the adds never respawn.
        anchor.Hp = 0;
        foreach (var x in session.Targets.Values.Where(x => BossSummons.IsSummon(x.Key))) x.Hp = 0;
        t += 500;
        await session.TickAsync(t);
        Assert.Contains(totemWave, session.ClearedWaves);
        await session.TickAsync(t + GameSession.RespawnMs + 1_000);
        Assert.All(session.Targets.Values.Where(x => BossSummons.IsSummon(x.Key)), x => Assert.True(x.Dead));
    }

    [Fact]
    public async Task A_boss_phase_brings_its_adds_into_the_copy_and_they_go_with_the_boss()
    {
        var session = await EnterAsync(600072);   // Vakron Sky Island: the boss still uses the generic 70% add script (boss-phases.json, OURS)
        var map = session.WorldPlayer!.Map!;
        var boss = map.Targets.Values.First(x => x.NpcId == 2300812);
        boss.Hp = boss.HpMax;
        boss.CorpseRemoved = false;
        boss.Immune = false;
        boss.Relationship = 6;
        var t = Now + 1_000;
        await MapEventScenarioHelpers.SyncPositionAsync(session, boss.Position with { X = boss.Position.X + 200 }, t);
        map.Ai.Engage(map.Targets, boss, t, session.PlayerKey, 10);
        for (var i = 0; i < 5; i++) await session.TickAsync(t += 200);
        Assert.NotNull(map.Ai.FightOf(boss));
        boss.Hp = boss.HpMax * 6 / 10;
        var replies = new List<byte[]>();
        for (var i = 0; i < 5; i++) replies.AddRange(await session.TickAsync(t += 200));
        var adds = map.Targets.Values.Where(x => BossSummons.IsSummon(x.Key)).ToList();
        Assert.Equal(3, adds.Count);
        Assert.All(adds, a => Assert.Equal(2300802, a.NpcId));
        Assert.All(adds, a => Assert.False(a.Dead));
        Assert.Equal(adds.Select(a => a.Key).Order(), map.Ai.FightOf(boss)!.Adds.Order());

        // The boss dies: its adds vanish and never come back.
        boss.Hp = 1;
        await MapEventScenarioHelpers.CombatCastAsync(session, MapEventScenarioHelpers.BasicAttackSkill(session), boss.Key, boss.Position, t += 200);
        Assert.True(boss.Dead);
        Assert.All(adds, a => Assert.True(a.Dead && a.CorpseRemoved));
        await session.TickAsync(t + GameSession.RespawnMs + 1_000);
        Assert.All(adds, a => Assert.True(a.Dead));
    }

    [Fact]
    public async Task Waves_off_keeps_the_old_timeout()
    {
        GameSession.ConfigureBosses(new BossOptions { Waves = false });
        try
        {
            var session = await EnterAsync(600082);
            Assert.Empty(session.KnownWaves);
            Assert.False(((IMapEventHost)session).WaveKnown(Wave));
        }
        finally { GameSession.ConfigureBosses(BossOptions.Default); }
    }
}
