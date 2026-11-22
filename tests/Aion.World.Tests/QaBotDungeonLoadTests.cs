using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QaBotDungeonLoadTests
{
    const long Now = 1_760_500_000_000;

    [Fact]
    public async Task Enter_dungeon_packets_complete_map_load_via_outbound_feed()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now, level: 5, qaBot: true);
        await session.QuestCommandAsync("QuestAdd", "HQ1101021", default);
        await session.QuestCommandAsync("QuestAdd", "AQ1602000", default);
        var dungeonId = MapEventTables.Embedded.DungeonIdOfMap(200005);
        Assert.True(dungeonId > 0);
        Assert.Equal(1000, session.MapId);
        var packets = session.EnterDungeonById(dungeonId, Now);
        Assert.NotEmpty(packets);
        Assert.All(packets, p => Assert.True(QaBotFeed.IsQaOutboundPacket(p) || GameSessionTestHelpers.Op(p) is 0x3647 or 0x3621,
            "EnterDungeon should emit map-move outbound packets"));
        await QaBotFeed.ApplyOutboundAsync(session, packets, Now);
        Assert.Equal(200005, session.MapId);
        Assert.False(session.MapLoadPending);
    }

    [Fact]
    public async Task Qa_map_event_enter_dungeon_think_moves_off_field_map()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now + 10_000, level: 5, qaBot: true);
        await session.QuestCommandAsync("QuestAdd", "HQ1101021", default);
        await session.QuestCommandAsync("QuestAdd", "AQ1602000", default);
        var move = session.Teleport(1000, new Vector3d(-35000, -40000, 23000), Now + 10_050);
        await QaBotFeed.ApplyOutboundAsync(session, move, Now + 10_055);
        var driver = new QuestStepDriver(GameData.Embedded, new QaBotOptions());
        var state = new QuestDriverState { ActiveMapEvent = "Dungeon_0005_MapEvent" };
        var slice = QaMapEventRunner.NextSlice(driver, session, "Dungeon_0005_MapEvent", session.WorldPosition!.Value, Now + 10_100, state);
        Assert.Contains("enter_dungeon", slice.ActionLabel, StringComparison.Ordinal);
        var think = new QaThinkResult(slice.Requests, null) { Outbound = QaBotFeed.PartitionPackets(slice.Requests).Outbound };
        await QaBotFeed.ApplyThinkAsync(session, think, Now + 10_150);
        Assert.Equal(200005, session.MapId);
        Assert.False(session.MapLoadPending);
    }

    [Fact]
    public async Task Dungeon_L_0016_boss_kill_uses_npc_id_not_spawner_label()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now + 50_000, level: 5, qaBot: true);
        await session.QuestCommandAsync("QuestAdd", "HQ1101020", default);
        var entered = await MapEventScenarioHelpers.PlayDungeonL0016Async(session, Now + 55_000);
        Assert.True(entered, "dungeon L0016 scenario should advance");
        var run = session.MapEventRuns.FirstOrDefault(r => r.Name == "Dungeon_L_0016_MapEvent");
        if (run is { } r && r.StepOrder >= 13)
        {
            var driver = new QuestStepDriver(GameData.Embedded, new QaBotOptions());
            var state = new QuestDriverState();
            var slice = QaMapEventRunner.NextSlice(driver, session, "Dungeon_L_0016_MapEvent", session.WorldPosition!.Value, Now + 120_000, state);
            if (slice.ActionLabel.StartsWith("map_event:kill:", StringComparison.Ordinal))
                Assert.DoesNotContain("Spawn_Boss", slice.ActionLabel, StringComparison.Ordinal);
            if (slice.ActionLabel.StartsWith("map_event:clear:", StringComparison.Ordinal))
                Assert.Fail($"should not opportunistically clear spawner proxy ({slice.ActionLabel})");
        }
    }
}