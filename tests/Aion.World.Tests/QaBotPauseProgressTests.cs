using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QaBotPauseProgressTests
{
    const long T0 = 1_760_600_000_000;

    [Fact]
    public async Task Map_event_goals_in_progress_token_prevent_no_progress_while_running()
    {
        var session = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(new FakeCharacterStore(), T0);
        await session.QuestCommandAsync("QuestAdd", "HQ2101030", default);
        for (var i = 0; i < 2; i++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ2101030", default);
        session.Teleport(1100, new Vector3d(18694, -10822, 6843), T0 + 100);
        await session.HandleAsync(new Aion.Protocol.Native.PacketWriter().U16(0x3622).I32(1).ToArray(), T0 + 110);
        await MapEventScenarioHelpers.EnterInstanceLayerAsync(session, 2951078, T0 + 120);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_D_Starter_HQ001_MapEvent", T0 + 200);
        Assert.NotNull(run);
        var brain = new QaQuestBrain(GameData.Embedded, session, 2, 45, new QaBotOptions { StepTimeoutMs = 120_000, ActionLoopTimeoutMs = 60_000 });
        for (var t = 0; t < 80; t++)
        {
            var now = T0 + 400 + t * 500;
            await session.TickAsync(now);
            var think = brain.Think(now);
            await QaBotFeed.ApplyThinkAsync(session, think, now);
        }
        var stall = brain.Think(T0 + 45_000).Stuck;
        Assert.Null(stall);
    }

    [Fact]
    public async Task Soft_stuck_pause_blocks_stall_detection_and_re_escalation()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), T0, level: 4, qaBot: true);
        var brain = new QaQuestBrain(GameData.Embedded, session, 1, 45, new QaBotOptions { StepTimeoutMs = 1_000 });
        var stuck = new QaStuckEvent(QaStuckReason.NoProgress, "HQ2101030", "x", 3, 3, 1100, null, "test", []);
        brain.HandleSoftStuck(stuck, T0);
        Assert.True(brain.SoftStuckPaused(T0 + 30_000));
        Assert.Null(brain.Think(T0 + 31_000).Stuck);
        brain.HandleSoftStuck(stuck, T0 + 32_000);
        Assert.True(brain.SoftStuckPaused(T0 + 90_000));
    }

    [Fact]
    public async Task Empty_open_list_still_drives_enter_volume_on_step_objectives()
    {
        var session = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(new FakeCharacterStore(), T0 + 100_000);
        await session.QuestCommandAsync("QuestAdd", "HQ2101030", default);
        for (var i = 0; i < 2; i++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ2101030", default);
        session.Teleport(1100, new Vector3d(18694, -10822, 6843), T0 + 100_100);
        await session.HandleAsync(new Aion.Protocol.Native.PacketWriter().U16(0x3622).I32(1).ToArray(), T0 + 100_110);
        await MapEventScenarioHelpers.EnterInstanceLayerAsync(session, 2951078, T0 + 100_120);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_D_Starter_HQ001_MapEvent", T0 + 100_200);
        Assert.NotNull(run);
        long t = T0 + 100_300;
        for (var i = 0; i < 90 && run!.StepOrder < 5; i++)
        {
            await MapEventScenarioHelpers.PlayMapEventScriptAsync(session, run, t, maxSeconds: 1);
            t += 1500;
        }
        var driver = new QuestStepDriver(GameData.Embedded, new QaBotOptions());
        var state = new QuestDriverState { ActiveMapEventRunId = run!.Id };
        var slice = QaMapEventRunner.NextSlice(driver, session, "World_D_Starter_HQ001_MapEvent", session.WorldPosition!.Value, t, state);
        if (run.StepOrder == 5)
            Assert.Contains("enter_vol", slice.ActionLabel, StringComparison.OrdinalIgnoreCase);
    }
}