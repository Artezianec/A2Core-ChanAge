using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QaBotActionLoopTests
{
    const long T0 = 1_760_300_000_000;

    [Fact]
    public async Task Static_seek_entry_stalls_within_action_loop_timeout()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), T0, level: 5, qaBot: true);
        await session.QuestCommandAsync("QuestAdd", "HQ1101021", default);
        _ = await session.QuestCommandAsync("QuestSetStep", "HQ1101021", default, stepOrder: 2);
        _ = session.Teleport(200001, new Vector3d(-20000, -30000, 19000), T0);
        Assert.True(session.MapLoadPending);
        var brain = new QaQuestBrain(GameData.Embedded, session, 1, 45,
            new QaBotOptions { StepTimeoutMs = 600_000, PathTimeoutMs = 600_000, ActionLoopTimeoutMs = 3_000 });
        QaStuckEvent? stuck = null;
        for (var i = 0; i < 20 && stuck is null; i++)
            stuck = brain.Think(T0 + (i + 1) * 500L).Stuck;
        Assert.NotNull(stuck);
        Assert.Contains("action loop", stuck!.Detail ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Long_walk_with_movement_three_minutes_no_action_loop()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), T0, level: 4, qaBot: true);
        await session.QuestCommandAsync("QuestAdd", "HQ1101018", default);
        _ = await session.QuestCommandAsync("QuestSetStep", "HQ1101018", default, stepOrder: 4);
        var brain = new QaQuestBrain(GameData.Embedded, session, 1, 45,
            new QaBotOptions { StepTimeoutMs = 600_000, PathTimeoutMs = 300_000, ActionLoopTimeoutMs = 60_000 });
        session.Teleport(1000, new Vector3d(-20800, -44400, 1000), T0 + 50);
        for (var tick = 0; tick < 360; tick++)
        {
            var now = T0 + 100 + tick * 500L;
            await session.TickAsync(now);
            if (session.WorldPosition is { } p)
                session.Teleport(session.MapId!.Value, new Vector3d(p.X + 80, p.Y + 30, p.Z), now);
            var think = brain.Think(now);
            if (think.Stuck is { } stuck)
                Assert.DoesNotContain("action loop", stuck.Detail ?? "", StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Map_event_escort_with_movement_three_minutes_no_action_loop()
    {
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(new FakeCharacterStore(), T0, race: 2, level: 3);
        await session.QuestCommandAsync("QuestAdd", "HQ2101030", default);
        for (var i = 0; i < 2; i++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ2101030", default);
        session.Teleport(1100, new Vector3d(18694, -10822, 6843), T0 + 100);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), T0 + 110);
        await MapEventScenarioHelpers.EnterInstanceLayerAsync(session, 2951078, T0 + 120);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), T0 + 130);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_D_Starter_HQ001_MapEvent", T0 + 200);
        Assert.NotNull(run);
        var brain = new QaQuestBrain(GameData.Embedded, session, 2, 45,
            new QaBotOptions { StepTimeoutMs = 600_000, PathTimeoutMs = 300_000, ActionLoopTimeoutMs = 60_000 });
        for (var tick = 0; tick < 360; tick++)
        {
            var now = T0 + 300 + tick * 500L;
            await session.TickAsync(now);
            if (session.WorldPosition is { } p)
            {
                var angle = tick * 0.12;
                session.Teleport(session.MapId!.Value, new Vector3d(p.X + 70 * Math.Cos(angle), p.Y + 70 * Math.Sin(angle), p.Z), now);
            }
            var think = brain.Think(now);
            if (think.Stuck is { } stuck)
                Assert.DoesNotContain("action loop", stuck.Detail ?? "", StringComparison.Ordinal);
        }
    }
}