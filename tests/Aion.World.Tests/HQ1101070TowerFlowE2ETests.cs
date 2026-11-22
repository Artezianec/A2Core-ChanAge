using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>HQ1101070 bomb towers: after Tower_1 is credited, a carrying bot must walk to the next remaining tower (not stall at the used site).</summary>
public sealed class HQ1101070TowerFlowE2ETests
{
    const long Now = 1_760_960_000_000;

    static byte[] Use(int key) => new PacketWriter().U16(0x3636).U8(1).Varint(key).I64(0).I32(0).ToArray();
    static byte[] GroundReady(int skill) => new PacketWriter().U16(GameSession.SkillGroundReadyRq).I32(skill).ToArray();

    static async Task<(GameSession session, MapEventRun run, List<(WorldObject o, int i)> towers, List<(WorldObject o, int i)> boxes)> EnterLayerAsync()
    {
        var store = new FakeCharacterStore();
        store.Quests.Add(new QuestRecord("HQ1101040", 1101040, 100, QuestLog.Rewarded, 1, new int[6]));
        store.Quests.Add(new QuestRecord("HQ1101050", 1101050, 101, QuestLog.Rewarded, 1, new int[6]));
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 10);
        await session.QuestCommandAsync("QuestAdd", "HQ1101070", default);
        var inst = InstanceTables.Embedded.Of(100002)!;
        var center = new Vector3d(inst.Entry.Center[0], inst.Entry.Center[1], inst.Entry.Center[2]);
        session.Teleport(1000, center with { Y = center.Y - 300 }, Now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20);
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(center), Now + 5000);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 5100);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_L_Starter_InstanceLayer_2_MapEvent", Now + 5200, attempts: 16);
        Assert.NotNull(run);
        var objects = WorldObjects.ObjectsOn(100002);
        var towers = objects.Select((o, i) => (o, i)).Where(x => x.o.Env == 1400824)
            .OrderBy(x => x.o.Name, StringComparer.Ordinal).ToList();
        Assert.Equal(3, towers.Count);
        var boxes = objects.Select((o, i) => (o, i)).Where(x => x.o.Env == 1400823).ToList();
        return (session, run!, towers, boxes);
    }

    [Fact]
    public async Task After_first_tower_planted_carrying_at_tower1_issues_long_walk_plant_to_next_tower()
    {
        var (session, run, towers, boxes) = await EnterLayerAsync();
        var tower0 = towers.First(t => t.o.Name.Contains("Tower_1", StringComparison.Ordinal));
        var tower1 = towers.First(t => t.o.Name.Contains("Tower_2", StringComparison.Ordinal));
        var box = boxes.OrderBy(b => Math.Abs(b.o.At.X - tower0.o.At.X)).First();
        var t = Now + 20_000;
        await MapEventScenarioHelpers.SyncPositionAsync(session, box.o.At, t);
        await session.HandleAsync(Use(WorldObjects.FirstKey + box.i), t + 100);
        await session.TickAsync(t + 100 + GameSession.CarryPickupMs);
        await MapEventScenarioHelpers.SyncPositionAsync(session, tower0.o.At, t + 1000);
        await session.HandleAsync(GroundReady(5003), t + 1100);
        await session.TickAsync(t + 2000);
        Assert.False(session.EnvObjBombTowerRemaining(tower0.i, tower0.o, t + 2000));
        Assert.True(session.EnvObjBombTowerRemaining(tower1.i, tower1.o, t + 2000));
        var nextBox = boxes.First(b => b.i != box.i);
        await MapEventScenarioHelpers.SyncPositionAsync(session, nextBox.o.At, t + 3000);
        await session.HandleAsync(Use(WorldObjects.FirstKey + nextBox.i), t + 3100);
        await session.TickAsync(t + 3100 + GameSession.CarryPickupMs);
        Assert.Equal(1196, session.CarriedGroup);
        var atTower1Site = tower0.o.At;
        await MapEventScenarioHelpers.SyncPositionAsync(session, atTower1Site, t + 4000);
        var driver = new QuestStepDriver(GameData.Embedded);
        var state = new QuestDriverState();
        var me = session.WorldPosition ?? atTower1Site;
        static double Flat2d(Vector3d a, Vector3d b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        Assert.True(Flat2d(me, atTower1Site) < 300);
        var objects = WorldObjects.ObjectsOn(100002).Select((o, i) => (o, i)).ToList();
        bool MatchTower(WorldObject o) => o.Env == 1400824;
        session.SyncMapEventBombTowerCredits(t + 4100);
        Assert.False(session.EnvObjBombTowerRemaining(tower0.i, tower0.o, t + 4100));
        var remaining = QaMapEventRunner.RemainingBombTowersForTests(session, t + 4100, me, objects, MatchTower, run);
        Assert.NotEmpty(remaining);
        var distNext = Flat2d(me, remaining[0].o.At);
        Assert.True(distNext > GameSession.BombPlantReach(remaining[0].o), $"expected long walk to {remaining[0].o.Name}, dist={distNext}");
        var slice = QaMapEventRunner.NextSlice(driver, session, run.Name, me, t + 4100, state);
        Assert.Contains("walk_plant", slice.ActionLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("skip_used_or_broken", slice.ActionLabel, StringComparison.Ordinal);
        var distToNext = Math.Sqrt(Math.Pow(tower1.o.At.X - tower0.o.At.X, 2) + Math.Pow(tower1.o.At.Y - tower0.o.At.Y, 2));
        Assert.True(distToNext > 2000, "tower spacing should require a real walk");
        Assert.Contains("dist=", slice.ActionLabel, StringComparison.Ordinal);
        if (int.TryParse(slice.ActionLabel.Split("dist=").LastOrDefault()?.Split(':').FirstOrDefault(), out var loggedDist))
            Assert.True(loggedDist > 500);
    }
}