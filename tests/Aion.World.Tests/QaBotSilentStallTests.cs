using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QaBotSilentStallTests
{
    const long T0 = 1_760_500_000_000;

    [Fact]
    public async Task Map_event_idle_action_does_not_refresh_no_progress_timer()
    {
        var session = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(new FakeCharacterStore(), T0);
        await session.QuestCommandAsync("QuestAdd", "HQ2101030", default);
        for (var i = 0; i < 2; i++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ2101030", default);
        session.Teleport(1100, new Vector3d(18694, -10822, 6843), T0);
        await MapEventScenarioHelpers.EnterInstanceLayerAsync(session, 2951078, T0 + 20);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_D_Starter_HQ001_MapEvent", T0 + 100);
        Assert.NotNull(run);
        var brain = new QaQuestBrain(GameData.Embedded, session, 2, 45,
            new QaBotOptions { StepTimeoutMs = 5_000, MapEventStallTimeoutMs = 5_000, ActionLoopTimeoutMs = 60_000 });
        brain.PinQuestFocus("HQ2101030");
        var token0 = brain.Think(T0 + 200);
        _ = token0;
        await session.TickAsync(T0 + 7_000);
        var stallEarly = brain.Think(T0 + 7_000).Stuck;
        Assert.Equal(QaStuckReason.NoProgress, stallEarly?.ReasonCode);
    }

    [Fact]
    public async Task Death_loop_on_same_step_reports_stuck()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), T0, level: 8, qaBot: true);
        await session.QuestCommandAsync("QuestAdd", "HQ1101050", default);
        var brain = new QaQuestBrain(GameData.Embedded, session, 1, 45,
            new QaBotOptions { DeathLoopThreshold = 3, DeathLoopWindowMs = 600_000 });
        brain.PinQuestFocus("HQ1101050");
        brain.NoteDeath(T0);
        brain.NoteDeath(T0 + 1000);
        brain.NoteDeath(T0 + 2000);
        var stall = brain.Think(T0 + 3000).Stuck;
        Assert.NotNull(stall);
        Assert.Equal(QaStuckReason.DeathLoop, stall!.ReasonCode);
    }
}