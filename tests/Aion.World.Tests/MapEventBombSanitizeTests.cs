using System.Reflection;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class MapEventBombSanitizeTests
{
    const long Now = 1_761_050_000_000;
    const string Event = "World_L_Starter_InstanceLayer_2_MapEvent";

    static void ForceStage(MapEventRun run, int stageIndex, int stepIndex, int[] goals, int maxStage, bool everCredited, int runPeak)
    {
        var type = typeof(MapEventRun);
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        type.GetProperty("Goals", flags)!.GetSetMethod(true)!.Invoke(run, [goals]);
        type.GetProperty("StageIndex", flags)!.GetSetMethod(true)!.Invoke(run, [stageIndex]);
        type.GetProperty("StepIndex", flags)!.GetSetMethod(true)!.Invoke(run, [stepIndex]);
        type.GetField("stepObjectivesDone", flags)!.SetValue(run, true);
        run.MaxStageOrderReached = maxStage;
        run.EverTowerCredited = everCredited;
        run.BombTowerPeakEver = runPeak;
    }

    static async Task<(GameSession session, MapEventRun run)> EnterBrigadeLayerAsync()
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
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, Event, Now + 5200, attempts: 16);
        Assert.NotNull(run);
        return (session, run!);
    }

    [Fact]
    public async Task Sanitize_live_resend_does_not_rewind_legit_stage3_after_towers()
    {
        var (session, run) = await EnterBrigadeLayerAsync();
        Assert.NotNull(run);
        ForceStage(run!, stageIndex: 2, stepIndex: 0, goals: [0], maxStage: 3, everCredited: true, runPeak: 3);
        Assert.Equal(3, run!.StageOrder);
        session.SanitizeBombMapEventRuns(Now + 7000, "live_resend");
        Assert.Equal(3, run.StageOrder);
        Assert.True(run.EverTowerCredited);
    }
}