using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class MapEventInstanceReentryTests
{
    const long Now = 1_761_000_000_000;

    [Fact]
    public async Task Teleport_off_layer_stashes_run_and_reenter_restores_same_run_id()
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
        var runId = run!.Id;
        var goalsBefore = run.Goals.ToArray();
        var field = MapEventTables.Embedded.Volume(100002)!.Base;
        var fieldAt = new Vector3d(inst.Entry.Center[0], inst.Entry.Center[1] - 800, inst.Entry.Center[2]);
        var t = Now + 30_000;
        var leave = session.Teleport(field, fieldAt, t);
        await session.HandleAsync(leave[^1], t + 50);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t + 100);
        Assert.Equal(field, session.MapId);
        var dungeonId = MapEventTables.Embedded.DungeonIdOfMap(100002);
        var prefer = session.PreferredSoloDungeonCopy(100002);
        Assert.NotEqual(0, prefer);
        var reenter = session.EnterDungeonById(dungeonId, t + 500);
        Assert.NotEmpty(reenter);
        await session.HandleAsync(reenter[^1], t + 600);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t + 700);
        Assert.Equal(100002, session.MapId);
        var restored = session.MapEventRuns.FirstOrDefault(r => r.Name == run.Name);
        Assert.NotNull(restored);
        Assert.Equal(goalsBefore, restored!.Goals);
    }

    [Fact]
    public async Task Second_EnterDungeon_while_in_copy_does_not_issue_map_reload()
    {
        var store = new FakeCharacterStore();
        store.Quests.Add(new QuestRecord("HQ1101040", 1101040, 100, QuestLog.Rewarded, 1, new int[6]));
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 10);
        await session.QuestCommandAsync("QuestAdd", "HQ1101070", default);
        var dungeonId = MapEventTables.Embedded.DungeonIdOfMap(100002);
        var first = session.EnterDungeonById(dungeonId, Now + 1000);
        Assert.NotEmpty(first);
        Assert.Contains(first, p => GameSessionTestHelpers.Op(p) == 0x3621);
        await session.HandleAsync(first[^1], Now + 1100);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 1200);
        Assert.Equal(100002, session.MapId);
        var again = session.EnterDungeonById(dungeonId, Now + 5000);
        Assert.DoesNotContain(again, p => GameSessionTestHelpers.Op(p) == 0x3621);
    }
}