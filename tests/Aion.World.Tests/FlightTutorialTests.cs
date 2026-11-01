using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class FlightTutorialTests
{
    const long Now = 1_760_090_300_000;

    static async Task<GameSession> EnterDungeon0005Async(FakeCharacterStore store, long now)
    {
        var session = await FlightRingTests.EnterAscendedAsync(store, now);
        await session.QuestCommandAsync("QuestAdd", "HQ1101021", default);
        await session.QuestCommandAsync("QuestAdd", "AQ1602000", default);
        var dungeonId = MapEventTables.Embedded.DungeonIdOfMap(200005);
        await MapEventScenarioHelpers.EnterDungeonByIdAsync(session, dungeonId, now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 20);
        await session.TickAsync(now + 30);
        _ = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "Dungeon_0005_MapEvent", now + 40)
            ?? session.MapEventRuns.FirstOrDefault(r => r.Name == "Dungeon_0005_MapEvent");
        return session;
    }

    [Fact]
    public async Task Flight_ring_pass_sends_official_stat_refill_and_ring_counter()
    {
        var store = new FakeCharacterStore();
        var session = await EnterDungeon0005Async(store, Now);
        var ring = WorldObjects.ObjectsOn(200005).First(o => o.FlightRing);
        var ringIndex = WorldObjects.ObjectsOn(200005).ToList().IndexOf(ring);
        session.Teleport(200005, ring.At, Now + 40);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 50);
        var key = WorldObjects.FirstKey + ringIndex;
        await session.HandleAsync(new PacketWriter().U16(0x3706).ToArray(), Now + 60);
        await session.TickAsync(Now + 60 + GameSession.SprintTickMs);
        var use = await session.HandleAsync(new PacketWriter().U16(0x3636).U8(1).Varint(key).I64(0).I32(0).ToArray(), Now + 70);
        Assert.Contains(use, p => GameSessionTestHelpers.Op(p) == 0x364a);
        var stats = use.First(p => GameSessionTestHelpers.Op(p) == 0x364a);
        Assert.True(ContainsStatPair(stats, 214, 15_000));
        Assert.True(ContainsStatPair(stats, 247, 10_000));
        Assert.Contains(use, p => GameSessionTestHelpers.Op(p) == 0x8d2c
            && BitConverter.ToInt32(p, 5) == GameSession.AscensionFlightMapEventWireId);
        var reset = await session.TickAsync(Now + 3070);
        Assert.Contains(reset, p => GameSessionTestHelpers.Op(p) == 0x364a);
    }

    static bool ContainsStatPair(byte[] packet, int type, int value)
    {
        var needle = new PacketWriter().U16((ushort)type).I32(value).ToArray();
        return packet.AsSpan().IndexOf(needle) >= 0;
    }

    [Fact]
    public async Task Trigger_env_interact_fires_interact_chain_and_event_func()
    {
        var store = new FakeCharacterStore();
        var session = await EnterDungeon0005Async(store, Now + 100_000);
        var trigger = WorldObjects.TriggersOn(200005).First(t => t.Name == "Dungeon_0005_Trigger_AsharTalk_1");
        var ti = WorldObjects.TriggersOn(200005).ToList().IndexOf(trigger);
        var key = WorldObjects.FirstTriggerKey + ti;
        var use = await session.HandleAsync(new PacketWriter().U16(0x3636).U8(1).Varint(key).I64(0).I32(0).ToArray(), Now + 100_100);
        Assert.Contains(use, p => GameSessionTestHelpers.Op(p) == 0x3638);
        Assert.Contains(use, p => GameSessionTestHelpers.Op(p) == 0x363a);
        Assert.Contains(use, p => GameSessionTestHelpers.Op(p) == 0x8d6d);
        Assert.Contains(use, p => GameSessionTestHelpers.Op(p) == 0x363b);
        Assert.Contains(use, p => GameSessionTestHelpers.Op(p) == 0x8d15
            && p.AsSpan().IndexOf("Talk_Dungeon_0005_05"u8) >= 0);
    }
}
