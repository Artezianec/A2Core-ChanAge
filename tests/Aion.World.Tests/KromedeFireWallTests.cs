using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class KromedeFireWallTests
{
    const int FireWallSkill = 1801185;
    const int BossKey = 2001, BossNpc = 2300371, Tank = 100001;

    static (NpcAi Ai, Dictionary<int, Target> Map, Target Boss) KromedeFight()
    {
        var script = BossPhases.Embedded.Of(BossNpc)!;
        var boss = new Target
        {
            Key = BossKey, NpcId = BossNpc, Name = "Fire_Temple_Single_Boss_1", Hp = 100_000, HpMax = 100_000, Level = 63,
            Position = Vector3d.Zero, WalkSpeed = 1, RunSpeed = 1, Radius = 150, SubType = "HeroMonster", Grade = 2,
        };
        var ai = new NpcAi(new Random(3), GameData.Embedded) { Bosses = new BossPhases(new Dictionary<int, BossScript> { [BossNpc] = script }) };
        return (ai, new Dictionary<int, Target> { [BossKey] = boss }, boss);
    }

    static void WindUpAndLand(NpcAi ai, Dictionary<int, Target> map, Target boss, long windStart)
    {
        var players = new List<(int Key, Vector3d Position)> { (Tank, new Vector3d(0, 0, 0)) };
        for (var t = windStart; t <= windStart + 20_000; t += 100)
            ai.Tick(map, players, t, (_, _) => []);
        Assert.Null(ai.TelegraphDueAt(boss));
        Assert.NotNull(ai.ActiveFireWall);
    }

    [Fact]
    public void Embedded_kromede_fire_wall_phases_use_grid_mechanic_at_75_and_25()
    {
        var script = BossPhases.Embedded.Of(2300371)!;
        var walls = script.Ordered.Where(p => p.Name == "fire_walls").ToList();
        Assert.Equal([75, 25], walls.Select(p => p.At).ToArray());
        Assert.All(walls, p => Assert.Equal("fire_wall_grid", p.Telegraph!.Mechanic));
        Assert.Equal(FireWallSkill, walls[0].Telegraph!.Skill);
    }

    [Fact]
    public void Player_in_wall_takes_dot_player_in_gap_does_not()
    {
        var ai = new NpcAi(new Random(3), GameData.Embedded);
        var run = KromedeFireWalls.Start(BossKey, FireWallSkill, Vector3d.Zero, 1_000, 16_500, false);
        ai.ActiveFireWall = run;
        ai.FireWallActivated(18_000);
        Assert.NotEmpty(run.Walls);
        var pitch = 2800.0;
        var gap = run.Centre with { X = run.Centre.X - 5000 + 3 * pitch + pitch / 2, Y = run.Centre.Y };
        var wall = run.Walls[0];
        var inWall = new Vector3d((wall.MinX + wall.MaxX) / 2, (wall.MinY + wall.MaxY) / 2, 0);
        Assert.False(KromedeFireWalls.InWall(run, gap));
        Assert.True(KromedeFireWalls.InWall(run, inWall));

        var damaged = new List<int>();
        ai.TickFireWall(19_500, [(Tank, inWall)], (key, _) => { damaged.Add(key); return []; });
        Assert.Contains(Tank, damaged);
        damaged.Clear();
        ai.TickFireWall(20_600, [(Tank, gap)], (key, _) => { damaged.Add(key); return []; });
        Assert.Empty(damaged);
    }

    [Fact]
    public void Fire_wall_phases_trigger_when_boss_crosses_75_and_25_percent()
    {
        var (ai, map, boss) = KromedeFight();
        ai.Engage(map, boss, 0, Tank);
        boss.Hp = 80_000;
        RunPhaseProbe(ai, map, 100, 500);
        Assert.Equal(0, ai.FireWallPhasesTriggered);
        boss.Hp = 74_000;
        RunPhaseProbe(ai, map, 600, 20_000);
        Assert.Equal(1, ai.FireWallPhasesTriggered);
        boss.Hp = 24_000;
        RunPhaseProbe(ai, map, 20_100, 40_000);
        Assert.Equal(2, ai.FireWallPhasesTriggered);
    }

    static void RunPhaseProbe(NpcAi ai, Dictionary<int, Target> map, long from, long to)
    {
        var players = new List<(int Key, Vector3d Position)> { (Tank, Vector3d.Zero) };
        for (var t = from; t <= to; t += 100)
            ai.Tick(map, players, t, (_, _) => []);
    }

    [Fact]
    public void Movement_into_wall_is_rejected_while_gap_is_free()
    {
        var (ai, map, boss) = KromedeFight();
        ai.Engage(map, boss, 0, Tank);
        boss.Hp = 70_000;
        WindUpAndLand(ai, map, boss, 100);
        var run = ai.ActiveFireWall!;
        var wall = run.Walls[0];
        var inside = new Vector3d((wall.MinX + wall.MaxX) / 2, (wall.MinY + wall.MaxY) / 2, 0);
        var outside = run.Centre;
        Assert.True(ai.FireWallBlocksMove(outside, inside, 25_000));
    }

    [Fact]
    public void Fire_temple_normal_dat_wires_final_boss_cube_and_timer()
    {
        var room = MapEventTables.Embedded.Dungeon(600022)!;
        Assert.Equal("Fire_Temple_Normal", room.Name);
        Assert.Equal("Party", room.Type);
        Assert.Equal(2100, room.ItemLevel);
        Assert.Equal(3600, room.Limit);
        Assert.Equal(2300371, room.CubeBoss);
    }

    [Fact]
    public async Task Fire_temple_clear_sends_party_dungeon_score_after_entry()
    {
        const long Now = 1_760_600_000_000;
        var store = new FakeCharacterStore();
        FakeCharacterStore.SeedExpeditionItemLevel(store);
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, 60);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now + 10);
        session.EnterDungeonById(600002, Now + 50);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 100);
        var run = session.MapEventRuns.FirstOrDefault();
        Assert.NotNull(run);
        ((IMapEventHost)session).DungeonClear(run!);
        var tick = await session.TickAsync(Now + 500);
        Assert.True(GameSessionTestHelpers.HasOpcode(tick, GameSession.PartyDungeonClearNt));
        Assert.NotNull(ExpeditionTables.Embedded.GroupForContent(600022));
    }
}