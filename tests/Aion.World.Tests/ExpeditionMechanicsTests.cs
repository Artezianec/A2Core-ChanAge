using System.Reflection;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class ExpeditionMechanicsTests
{
    const int Tank = 100001;

    [Fact]
    public void Berk_pillar_cover_skips_slam_damage_behind_pillar()
    {
        var boss = new Target { Key = 1, Name = "KraoCave_Boss_1", NpcId = 2300171, Position = Vector3d.Zero, Level = 60, Hp = 100_000, HpMax = 100_000 };
        var pillar = ExpeditionBossMechanics.PillarAt(boss, new Vector3d(2000, 0, 0));
        var covered = new Vector3d(pillar.X + 400, pillar.Y, 0);
        var exposed = new Vector3d(3000, 0, 0);
        Assert.True(ExpeditionBossMechanics.InPillarCover(boss, pillar, covered));
        Assert.False(ExpeditionBossMechanics.InPillarCover(boss, pillar, exposed));
    }

    [Fact]
    public void Auldor_tempest_splits_damage_by_stackers()
    {
        var script = BossPhases.Embedded.Of(2300218)!;
        var phase75 = Assert.Single(script.Ordered, p => p.At == 75);
        Assert.Equal("auldor_tempest_soak", phase75.Telegraph!.Mechanic);
        var ai = new NpcAi(new Random(2), GameData.Embedded)
        {
            Bosses = new BossPhases(new Dictionary<int, BossScript> { [2300218] = script }),
            BossRules = BossOptions.Default with { Engine = false },
        };
        var boss = new Target { Key = 1, Name = "UruCan_Stage03_Boss_1", NpcId = 2300218, Position = Vector3d.Zero, Level = 60, Hp = 70_000, HpMax = 100_000, WalkSpeed = 1, RunSpeed = 1, Radius = 50 };
        var map = new Dictionary<int, Target> { [1] = boss };
        ai.Engage(map, boss, 0, Tank);
        var damaged = new List<int>();
        ai.Tick(map, [(Tank, Vector3d.Zero), (100002, new Vector3d(100, 0, 0))], 50_000, (k, _) => { damaged.Add(k); return []; });
        Assert.NotNull(ai.Expedition?.SoakMarkPlayer);
    }

    [Fact]
    public void Fire_temple_script_lists_midbosses_and_mirror_map_event()
    {
        var events = MapEventTables.Embedded.EventsOn(600022).Select(e => e.Name).ToList();
        Assert.Contains(events, n => n.Contains("Fire_Temple_Normal", StringComparison.Ordinal));
        var spawns = MapEventTables.Embedded.Dungeon(600022)!.Name;
        Assert.Equal("Fire_Temple_Normal", spawns);
        Assert.Contains(MapEventTables.Embedded.AllDungeons, d =>
            d.Value.Map == 600022 && d.Value.CubeBoss == 2300371);
        var mapEventJson = File.ReadAllText(Path.Combine(RepoRoot(), "tools/data/map-events.json"));
        Assert.Contains("Fire_Temple_Single_Mirror_1", mapEventJson, StringComparison.Ordinal);
        Assert.Contains("Fire_Temple_Single_MidBoss", mapEventJson, StringComparison.Ordinal);
    }

    static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Aion2-repo.sln")) || File.Exists(Path.Combine(dir, "tools/data/map-events.json")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }

    static void CubeKilled(GameSession session, Target target, long now) =>
        typeof(GameSession).GetMethod("CubeKilled", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, [target, now]);

    [Fact]
    public async Task Fire_temple_midboss_final_kills_arm_cube_reward()
    {
        const long Now = 1_760_700_100_000;
        var store = new FakeCharacterStore();
        FakeCharacterStore.SeedPartyDungeonItemLevel(store, 2100);
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, 60);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now + 10);
        session.EnterDungeonById(600022, Now + 20);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 100);
        Assert.NotNull(session.Copy);
        var mid1 = session.Targets.Values.FirstOrDefault(t => t.NpcId == 2300311);
        var mid2 = session.Targets.Values.FirstOrDefault(t => t.NpcId == 2300312);
        var final = session.Targets.Values.FirstOrDefault(t => t.NpcId == 2300371);
        Assert.NotNull(mid1);
        Assert.NotNull(mid2);
        Assert.NotNull(final);
        session.Teleport(600022, mid1!.Position with { X = mid1.Position.X + 120 }, Now + 150);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 150);
        CubeKilled(session, mid1, Now + 200);
        var midTick = await session.TickAsync(Now + 210);
        Assert.Contains(midTick, b => b.SequenceEqual(PacketWriter.Opcode(GameSession.MiddleBossKillNt).I32(2300311).ToArray()));
        CubeKilled(session, mid2!, Now + 220);
        await session.TickAsync(Now + 230);
        CubeKilled(session, final!, Now + 240);
        var arm = await session.TickAsync(Now + 250);
        Assert.Contains(arm, b => GameSessionTestHelpers.Op(b) == GameSession.CubeRewardStartNt);
        var cube = await session.HandleAsync(new PacketWriter().U16(GameSession.CubeRewardRq).U8(1).ToArray(), Now + 300);
        var reply = cube.FirstOrDefault(b => GameSessionTestHelpers.Op(b) == GameSession.CubeRewardRs);
        Assert.NotNull(reply);
        Assert.Equal(0, BitConverter.ToUInt16(reply!, 2));
    }

    [Fact]
    public async Task Fire_temple_entry_requires_2100_il_and_clears_to_reward()
    {
        const long Now = 1_760_700_000_000;
        var store = new FakeCharacterStore();
        FakeCharacterStore.SeedPartyDungeonItemLevel(store, 2100);
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, 60);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now + 10);
        var low = session.EnterDungeonById(600022, Now + 20);
        Assert.NotEmpty(low);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 100);
        var run = session.MapEventRuns.FirstOrDefault();
        Assert.NotNull(run);
        ((IMapEventHost)session).DungeonClear(run!);
        var tick = await session.TickAsync(Now + 500);
        Assert.True(GameSessionTestHelpers.HasOpcode(tick, GameSession.PartyDungeonClearNt));
        Assert.Equal(2100, MapEventTables.Embedded.Dungeon(600022)!.ItemLevel);
    }
}