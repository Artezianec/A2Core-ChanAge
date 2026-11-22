using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QaBombPickupTests
{
    const long T0 = 1_760_950_000_000;

    [Fact]
    public async Task Bot_at_bomb_box_range_issues_use_object_while_hostile_nearby()
    {
        var store = new FakeCharacterStore();
        store.Quests.Add(new QuestRecord("HQ1101040", 1101040, 100, QuestLog.Rewarded, 1, new int[6]));
        store.Quests.Add(new QuestRecord("HQ1101050", 1101050, 101, QuestLog.Rewarded, 1, new int[6]));
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, T0, race: 1, level: 10);
        await session.QuestCommandAsync("QuestAdd", "HQ1101070", default);
        session.Teleport(100002, new Vector3d(11060, -38630, 21418), T0 + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), T0 + 20);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_L_Starter_InstanceLayer_2_MapEvent", T0 + 500, attempts: 24);
        Assert.NotNull(run);
        var objects = WorldObjects.ObjectsOn(100002);
        var box = objects.Select((o, i) => (o, i)).First(x => x.o.Env == 1400823);
        await MapEventScenarioHelpers.SyncPositionAsync(session, box.o.At, T0 + 1000);
        var hostile = session.Targets.Values.FirstOrDefault(t => t.Hostile && !t.Dead);
        Assert.NotNull(hostile);
        var driver = new QuestStepDriver(GameData.Embedded);
        var state = new QuestDriverState();
        var slice = QaMapEventRunner.NextSlice(driver, session, run!.Name, box.o.At, T0 + 1100, state);
        Assert.Equal(QuestDriverKind.Act, slice.Kind);
        Assert.Contains("pickup_bomb", slice.ActionLabel, StringComparison.Ordinal);
        Assert.Contains(slice.Requests, p => p.Length > 2 && (p[0] | (p[1] << 8)) == 0x3636);
    }
}