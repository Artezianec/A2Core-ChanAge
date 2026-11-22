using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QaBotTeleportStallTests
{
    const long T0 = 1_760_400_000_000;

    [Fact]
    public async Task Teleport_slice_applies_3621_and_reaches_target_map()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), T0, level: 5, qaBot: true);
        await session.QuestCommandAsync("QuestAdd", "HQ1101021", default);
        _ = await session.QuestCommandAsync("QuestSetStep", "HQ1101021", default, stepOrder: 2);
        var dungeonAt = MapEventTables.Embedded.Points(200001).Values.FirstOrDefault(v => v.Length >= 3) is { } p
            ? new Vector3d(p[0], p[1], p[2])
            : new Vector3d(0, 0, 19000);
        await QaBotFeed.ApplyOutboundAsync(session, session.Teleport(200001, dungeonAt, T0 + 10), T0 + 10);
        Assert.Equal(200001, session.MapId);
        var driver = new QuestStepDriver(GameData.Embedded, new QaBotOptions());
        var state = new QuestDriverState();
        var slice = driver.NextSlice(session, "HQ1101021", T0 + 100, state);
        Assert.Equal(QuestDriverKind.Teleport, slice.Kind);
        Assert.NotNull(slice.TeleportMap);
        await QaBotFeed.ApplyOutboundAsync(session, session.Teleport(slice.TeleportMap!.Value, slice.TeleportAt!.Value, T0 + 100), T0 + 100);
        Assert.Equal(1000, session.MapId);
    }

    [Fact]
    public async Task Unfed_map_load_action_loop_reports_stuck()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), T0, level: 5, qaBot: true);
        await session.QuestCommandAsync("QuestAdd", "HQ1101021", default);
        _ = await session.QuestCommandAsync("QuestSetStep", "HQ1101021", default, stepOrder: 2);
        var at = new Vector3d(-20000, -30000, 19000);
        _ = session.Teleport(200001, at, T0);
        Assert.True(session.MapLoadPending);
        var brain = new QaQuestBrain(GameData.Embedded, session, 1, 45,
            new QaBotOptions { StepTimeoutMs = 600_000, PathTimeoutMs = 60_000, ActionLoopTimeoutMs = 3_000 });
        QaStuckEvent? stuck = null;
        for (var i = 0; i < 12 && stuck is null; i++)
            stuck = brain.Think(T0 + (i + 1) * 500L).Stuck;
        Assert.NotNull(stuck);
        Assert.Equal(QaStuckReason.StepTimeout, stuck!.ReasonCode);
        Assert.Contains("action loop", stuck.Detail ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Walk_in_layer_reaches_instance_map_with_full_load_path()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), T0, level: 5, qaBot: true);
        await session.QuestCommandAsync("QuestAdd", "HQ1101030", default);
        _ = await session.QuestCommandAsync("QuestSetStep", "HQ1101030", default, stepOrder: 3);
        await QaBotFeed.ApplyOutboundAsync(session, session.Teleport(1000, new Vector3d(-35000, -40000, 23000), T0), T0);
        if (session.QaInInstance)
            await QaBotFeed.ApplyThinkAsync(session, new QaThinkResult([BotRequests.LeaveInstance()], null), T0 + 20);
        var driver = new QuestStepDriver(GameData.Embedded, new QaBotOptions());
        var state = new QuestDriverState();
        QuestDriverSlice slice;
        long at = T0 + 50;
        for (var i = 0; i < 12; i++, at += 50)
        {
            slice = QaMapEventRunner.NextSlice(driver, session, "World_L_Starter_HQ002_MapEvent", session.WorldPosition!.Value, at, state);
            if (slice.Kind == QuestDriverKind.Teleport && slice.TeleportMap is { } map && slice.TeleportAt is { } pos)
            {
                await QaBotFeed.ApplyOutboundAsync(session, session.Teleport(map, pos, at), at);
                break;
            }
            if (slice.Kind == QuestDriverKind.Act && slice.Requests.Count > 0)
                await QaBotFeed.ApplyThinkAsync(session, new QaThinkResult(slice.Requests, null), at);
            if (session.MapId == 100011) break;
        }
        slice = QaMapEventRunner.NextSlice(driver, session, "World_L_Starter_HQ002_MapEvent", session.WorldPosition!.Value, at, state);
        Assert.True(session.MapId == 100011 || slice.Kind == QuestDriverKind.Teleport,
            $"walk-in should reach layer 100011 (map={session.MapId}, last={slice.ActionLabel})");
        if (session.MapId != 100011)
        {
            await QaBotFeed.ApplyOutboundAsync(session, session.Teleport(slice.TeleportMap!.Value, slice.TeleportAt!.Value, at), at);
        }
        Assert.Equal(100011, session.MapId);
        Assert.False(session.MapLoadPending);
    }

    [Fact]
    public async Task Dark_HQ001_after_layer_entry_exposes_hp_remaining_priority()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(store, T0, level: 5);
        await session.QuestCommandAsync("QuestAdd", "HQ2101030", default);
        _ = await session.QuestCommandAsync("QuestSetStep", "HQ2101030", default, stepOrder: 3);
        await MapEventScenarioHelpers.EnterInstanceLayerAsync(session, 2951078, T0 + 100);
        Assert.Equal(110002, session.MapId);
        Assert.False(session.MapLoadPending);
        Assert.Equal(0, QaMapEventRunner.MapEventObjectivePriority("HpRemaining"));
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_D_Starter_HQ001_MapEvent", T0 + 200);
        Assert.NotNull(run);
    }
}