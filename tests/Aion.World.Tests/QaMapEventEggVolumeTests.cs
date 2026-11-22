using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QaMapEventEggVolumeTests
{
    const long Now = 1_760_400_000_000;

    static async Task<(GameSession Session, MapEventRun Run)> Hq1101030SkyIslandAsync(long now)
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), now, level: 5, qaBot: true);
        await session.QuestCommandAsync("QuestAdd", "HQ1101030", default);
        _ = await session.QuestCommandAsync("QuestSetStep", "HQ1101030", default, stepOrder: 3);
        await QaBotFeed.ApplyOutboundAsync(session, session.Teleport(1000, new Vector3d(-35000, -40000, 23000), now), now);
        if (session.QaInInstance)
            await QaBotFeed.ApplyThinkAsync(session, new QaThinkResult([BotRequests.LeaveInstance()], null), now + 20);
        var driver = new QuestStepDriver(GameData.Embedded, new QaBotOptions());
        var state = new QuestDriverState();
        long at = now + 50;
        for (var i = 0; i < 12; i++, at += 50)
        {
            var slice = QaMapEventRunner.NextSlice(driver, session, "World_L_Starter_HQ002_MapEvent", session.WorldPosition!.Value, at, state);
            if (slice.Kind == QuestDriverKind.Teleport && slice.TeleportMap is { } map && slice.TeleportAt is { } pos)
            {
                await QaBotFeed.ApplyOutboundAsync(session, session.Teleport(map, pos, at), at);
                break;
            }
            if (slice.Kind == QuestDriverKind.Act && slice.Requests.Count > 0)
                await QaBotFeed.ApplyThinkAsync(session, new QaThinkResult(slice.Requests, null), at);
            if (session.MapId == 100011) break;
        }
        if (session.MapId != 100011)
        {
            var slice = QaMapEventRunner.NextSlice(driver, session, "World_L_Starter_HQ002_MapEvent", session.WorldPosition!.Value, at, state);
            if (slice.TeleportMap is { } map && slice.TeleportAt is { } pos)
                await QaBotFeed.ApplyOutboundAsync(session, session.Teleport(map, pos, at), at);
        }
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), at + 5);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_L_Starter_HQ002_MapEvent", at + 60)
                  ?? throw new InvalidOperationException("HQ002 sky island map event did not start");
        return (session, run);
    }

    static async Task AdvanceRunToStepAsync(GameSession session, MapEventRun run, int stepOrder, long t)
    {
        for (var i = 0; i < 400 && run.StepOrder < stepOrder; i++)
        {
            await MapEventScenarioHelpers.PlayMapEventScriptAsync(session, run, t, maxSeconds: 1);
            t += 1200;
        }
    }

    [Fact]
    public async Task Phoenix_eggs_step_9_uses_three_distinct_objects()
    {
        var (session, run) = await Hq1101030SkyIslandAsync(Now);
        var t = Now + 8000;
        await AdvanceRunToStepAsync(session, run, 9, t);
        Assert.Equal(9, run.StepOrder);

        var driver = new QuestStepDriver(GameData.Embedded, new QaBotOptions());
        var state = new QuestDriverState();
        var eggObjective = run.Open.FirstOrDefault(x => x.Objective.Type == "UseEnvObj");
        Assert.NotEqual(default, eggObjective);
        var goalIdx = eggObjective.Index;
        var distinctKeys = new HashSet<int>();
        for (var attempt = 0; attempt < 150 && goalIdx < run.Goals.Length && run.Goals[goalIdx] < 3; attempt++)
        {
            t += 700;
            var me = session.WorldPosition ?? WorldObjects.ObjectsOn(100011)[0].At;
            var slice = QaMapEventRunner.NextSlice(driver, session, "World_L_Starter_HQ002_MapEvent", me, t, state);
            foreach (var packet in slice.Requests)
                await session.HandleAsync(packet, t);
            if (slice.ActionLabel == "map_event:use_env" && state.PendingEnvKey is { } pk)
                distinctKeys.Add(pk);
            if (slice.ActionLabel is "map_event:env_channel" or "map_event:use_env")
                t = Math.Max(t, state.PendingEnvUntil + 50);
            await session.TickAsync(t + 200);
        }

        Assert.True(distinctKeys.Count >= 3,
            $"expected three distinct env keys (got {distinctKeys.Count}: {string.Join(',', distinctKeys)})");
        var eggsDone = run.StepOrder > 9
            || (goalIdx < run.Goals.Length && run.Goals[goalIdx] >= eggObjective.Objective.Count);
        Assert.True(eggsDone,
            $"egg step should finish (step={run.StepOrder}, goals=[{string.Join(',', run.Goals)}])");
    }

    [Fact]
    public async Task UseEnvObj_skips_already_consumed_egg()
    {
        var (session, run) = await Hq1101030SkyIslandAsync(Now + 100_000);
        var t = Now + 108_000;
        await AdvanceRunToStepAsync(session, run, 9, t);
        var eggs = WorldObjects.ObjectsOn(100011).Select((o, idx) => (o, idx))
            .Where(x => x.o.Name.Contains("PhoenixEgg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.o.At.X)
            .ToList();
        Assert.True(eggs.Count >= 2);
        var (egg, idx) = eggs[0];
        var key = WorldObjects.FirstKey + idx;
        await MapEventScenarioHelpers.SyncPositionAsync(session, egg.At, t);
        await session.HandleAsync(BotRequests.UseObject(key, egg.Env), t + 10);
        var channelMs = egg.InteractTime > 0 ? egg.InteractTime : 1500;
        await session.TickAsync(t + channelMs + 600);
        Assert.False(session.EnvObjAvailableForUse(idx, egg, t + channelMs + 700));

        var driver = new QuestStepDriver(GameData.Embedded, new QaBotOptions());
        var state = new QuestDriverState();
        var slice = QaMapEventRunner.NextSlice(driver, session, "World_L_Starter_HQ002_MapEvent", session.WorldPosition!.Value, t + 5000, state);
        Assert.NotEqual("map_event:env_channel", slice.ActionLabel);
        if (slice.ActionLabel == "map_event:use_env")
            Assert.NotEqual(key, state.PendingEnvKey);
    }

    [Fact]
    public async Task Dark_HQ001_fresh_driver_state_credits_volume_not_hand_triggers()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(store, Now + 200_000);
        await session.QuestCommandAsync("QuestAdd", "HQ2101030", default);
        for (var i = 0; i < 2; i++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ2101030", default);
        session.Teleport(1100, new Vector3d(18694, -10822, 6843), Now + 200_100);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 200_110);
        await MapEventScenarioHelpers.EnterInstanceLayerAsync(session, 2951078, Now + 200_120);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_D_Starter_HQ001_MapEvent", Now + 200_200);
        Assert.NotNull(run);
        var t = Now + 200_300;
        for (var i = 0; i < 120 && run!.StepOrder < 5; i++)
        {
            await MapEventScenarioHelpers.PlayMapEventScriptAsync(session, run, t, maxSeconds: 1);
            t += 1500;
        }
        Assert.True(run!.StepOrder >= 5, $"expected step>=5 (step={run.StepOrder})");

        var driver = new QuestStepDriver(GameData.Embedded, new QaBotOptions());
        var freshState = new QuestDriverState();
        var slice = QaMapEventRunner.NextSlice(driver, session, "World_D_Starter_HQ001_MapEvent", session.WorldPosition!.Value, t, freshState);
        Assert.DoesNotContain("hq001_dark:trigger", slice.ActionLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("script_idle", slice.ActionLabel, StringComparison.Ordinal);

        if (run.StepOrder == 5 && run.Open.Any(x => x.Objective.Type == "EnterVolumePC"))
        {
            var triggerName = run.Open.First(x => x.Objective.Type == "EnterVolumePC").Objective.Value(0);
            var trigger = MapEventScenarioHelpers.TriggerOrSubzonePosition(110002, triggerName);
            await MapEventScenarioHelpers.SyncPositionAsync(session, trigger, t + 50);
            slice = QaMapEventRunner.NextSlice(driver, session, "World_D_Starter_HQ001_MapEvent", session.WorldPosition!.Value, t + 100, freshState);
            Assert.Contains("enter_vol", slice.ActionLabel, StringComparison.Ordinal);
            await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(trigger), t + 110);
            for (var w = 0; w < 10; w++)
                await session.TickAsync(t + 200 + w * 250);
        }
        else if (run.StepOrder >= 6)
        {
            Assert.Contains("hp_remaining", slice.ActionLabel, StringComparison.OrdinalIgnoreCase);
        }

        t += 8000;
        for (var i = 0; i < 80 && run.StepOrder < 6; i++)
        {
            await MapEventScenarioHelpers.PlayMapEventScriptAsync(session, run, t, maxSeconds: 1, liveAskNpc: true);
            t += 2000;
        }
        if (run.StepOrder == 6 && run.Open.Any(x => x.Objective.Type == "HpRemaining"))
        {
            var elvida = session.Targets!.Values.FirstOrDefault(n => n.NpcId == 2950867);
            Assert.NotNull(elvida);
            elvida!.Hp = Math.Max(1, (int)(elvida.HpMax * 0.35));
            freshState = new QuestDriverState();
            slice = QaMapEventRunner.NextSlice(driver, session, "World_D_Starter_HQ001_MapEvent", session.WorldPosition!.Value, t + 100, freshState);
            Assert.Contains("hp_remaining", slice.ActionLabel, StringComparison.Ordinal);
        }
        Assert.True(run.StepOrder >= 6, $"expected step 6 after volume (step={run.StepOrder})");
    }
}