using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>QA ClearMapEvent driver (replaces the old map_event wait stub).</summary>
public sealed class QaMapEventRunnerTests
{
    const long Now = 1_760_010_000_000;

    [Fact]
    public void Open_objectives_treat_kill_before_enter_volume()
    {
        Assert.True(QaMapEventRunner.MapEventObjectivePriority("KillNpc")
            < QaMapEventRunner.MapEventObjectivePriority("EnterVolumePC"));
        Assert.True(QaMapEventRunner.MapEventObjectivePriority("UseEnvObj")
            < QaMapEventRunner.MapEventObjectivePriority("EnterVolumePC"));
        Assert.True(QaMapEventRunner.MapEventObjectivePriority("UseEnvObj", 100002)
            < QaMapEventRunner.MapEventObjectivePriority("KillNpc", 100002));
        Assert.True(QaMapEventRunner.MapEventObjectivePriority("KillNpc", 100002)
            > QaMapEventRunner.MapEventObjectivePriority("UseEnvObj", 100002));
    }

    [Fact]
    public async Task Qa_runner_clears_HQ1101012_World_L_Starter_HQ001()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now);
        var options = new QaBotOptions();
        if (session.Quests!.Get("HQ1101012") is null)
            await session.QuestCommandAsync("QuestAdd", "HQ1101012", default);
        for (var i = 0; i < 2 && session.Quests!.Get("HQ1101012")!.Step < 2; i++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ1101012", default);

        var driver = new QuestStepDriver(GameData.Embedded, options);
        var state = new QuestDriverState();
        var now = Now + 200;
        var cleared = false;
        for (var tick = 0; tick < 6000 && !cleared; tick++)
        {
            now += 250;
            if (tick % 3 == 0)
                _ = await session.TickAsync(now);
            var me = session.WorldPosition ?? GameSessionTestHelpers.MapStartOrDefault(1000);
            var slice = QaMapEventRunner.NextSlice(driver, session, "World_L_Starter_HQ001_MapEvent", me, now, state);
            foreach (var packet in slice.Requests)
            {
                var replies = await session.HandleAsync(packet, now);
                if (state.CombatBusyUntil > now)
                    _ = await session.TickAsync(state.CombatBusyUntil);
                foreach (var reply in replies)
                    if (reply.Length >= 6 && reply[0] == 0x21 && reply[1] == 0x36)
                        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(BitConverter.ToInt32(reply, 2)).ToArray(), now + 5);
                if (packet.Length >= 6 && packet[0] == 0x21 && packet[1] == 0x36)
                    await session.HandleAsync(new PacketWriter().U16(0x3622).I32(BitConverter.ToInt32(packet, 2)).ToArray(), now + 5);
            }
            if (slice.Kind == QuestDriverKind.Teleport && slice.TeleportMap is { } map && slice.TeleportAt is { } at)
                session.Teleport(map, at, now);
            var rec = session.Quests!.Get("HQ1101012")!;
            if (rec.Step > 2 || rec.Status >= QuestLog.Complete)
            {
                cleared = true;
                break;
            }
        }
        var final = session.Quests!.Get("HQ1101012")!;
        Assert.True(cleared || final.Step > 2 || final.Status >= QuestLog.Complete,
            $"QA map-event runner should clear HQ1101012 step 2 (step={final.Step}, status={final.Status})");
    }
}