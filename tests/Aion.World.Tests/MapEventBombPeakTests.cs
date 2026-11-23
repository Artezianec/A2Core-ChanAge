using System.Reflection;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class MapEventBombPeakTests
{
    [Fact]
    public async Task Tower_peak_tracks_env_spend_not_goal_credit_alone()
    {
        const long now = 1_761_050_000_000;
        var store = new FakeCharacterStore();
        store.Quests.Add(new QuestRecord("HQ1101040", 1101040, 100, QuestLog.Rewarded, 1, new int[6]));
        store.Quests.Add(new QuestRecord("HQ1101050", 1101050, 101, QuestLog.Rewarded, 1, new int[6]));
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, now, race: 1, level: 10);
        await session.QuestCommandAsync("QuestAdd", "HQ1101070", default);
        var inst = InstanceTables.Embedded.Of(100002)!;
        var center = new Vector3d(inst.Entry.Center[0], inst.Entry.Center[1], inst.Entry.Center[2]);
        session.Teleport(1000, center with { Y = center.Y - 300 }, now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 20);
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(center), now + 5000);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 5100);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_L_Starter_InstanceLayer_2_MapEvent", now + 5200, attempts: 16);
        Assert.NotNull(run);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var peakField = typeof(GameSession).GetField("bombTowerPeakSpent", flags)!;
        var record = typeof(GameSession).GetMethod("RecordBombTowerPeak", flags)!;
        record.Invoke(session, [0, 2]);
        Assert.Equal(0, peakField.GetValue(session));
        record.Invoke(session, [2, 2]);
        Assert.Equal(2, peakField.GetValue(session));
        var bump = typeof(GameSession).GetMethod("BumpBombTowerProgress", flags)!;
        bump.Invoke(session, [run, 3, 3]);
        Assert.True(run!.BombProbeDue);
    }
}