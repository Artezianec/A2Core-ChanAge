using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>The 2.0.13 gather wire: raw per-turn gauges, attempts per node and the interaction state packets.</summary>
public sealed class GatherV213WireTests
{
    const long Now = 1_760_300_000_000;
    const int PetRide = 1002;

    static (WorldObject Node, int Index, int Key) FirstGatherNode(int mapId = 1010)
    {
        var hit = WorldObjects.ObjectsOn(mapId).Select((o, i) => (o, i)).First(x => x.o.Gather is not null);
        return (hit.o, hit.i, WorldObjects.FirstKey + hit.i);
    }

    static int TargetOf(WorldObject node) => node.Gather!.Length >= 6 && node.Gather[5] > 0 ? (int)node.Gather[5] : 360;

    static int TurnsOf(WorldObject node) => Math.Clamp((TargetOf(node) + 139) / 140, 3, 5);

    static async Task<GameSession> SessionOnNodeAsync(FakeCharacterStore store, WorldObject node, long now)
    {
        store.Progress = (55, 0);
        var session = await GameSessionTestHelpers.EnterMap1010Async(store, now);
        session.Teleport(1010, node.At with { X = node.At.X + 50 }, now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
        return session;
    }

    static WireVersion Pin(WireVersion version)
    {
        var previous = WireContext.Current;
        WireContext.Current = version;
        return previous;
    }

    [Fact]
    public async Task V213_reply_carries_raw_per_turn_gauges_and_no_trailing_i32()
    {
        var (node, _, key) = FirstGatherNode();
        var session = await SessionOnNodeAsync(new FakeCharacterStore(), node, Now);
        session.GatherRoll = () => 0;   // a winning attempt

        var previous = Pin(WireVersion.V213);
        try
        {
            var startAt = Now + 10;
            var replies = await session.HandleAsync(new PacketWriter().U16(GameSession.GatherStartRq).Varint((uint)key).ToArray(), startAt);
            var rs = replies.Single(p => GameSessionTestHelpers.Op(p) == GameSession.GatherStartRs);

            var reader = new PacketReader(rs) { Offset = 2 };
            Assert.Equal(0, reader.U16());
            Assert.Equal((uint)key, reader.Varint());
            _ = reader.I32();                       // _attempts
            _ = reader.Bool();                      // _is_success
            _ = reader.Bool();                      // _is_discovered
            _ = reader.Varint();                    // _start_skill_effect_sets
            _ = reader.Bool();                      // _basic_discovery_result
            _ = reader.Varint();                    // _discovery_skill_effect_sets
            _ = reader.I32();                       // _material_group_key
            var turnCount = (int)reader.Varint();
            var target = TargetOf(node);
            var sum = 0;
            for (var i = 0; i < turnCount; i++)
            {
                _ = reader.U8();                    // _turn_result
                _ = reader.Varint();                // _activated_skill_effect_sets
                var successBefore = reader.I32();
                var successAfter = reader.I32();
                var failBefore = reader.I32();
                var failAfter = reader.I32();
                _ = reader.Bool();                  // _additional_discovery_result
                Assert.Equal(0, successBefore);
                Assert.Equal(0, failBefore);
                Assert.Equal(0, failAfter);
                sum += successAfter;
            }
            _ = reader.Bool();                      // _mastery_grade_quest (bool, not i32)
            Assert.True(reader.End);
            Assert.Equal(target, sum);

            // The 0x8d33 completion must wait for the whole per-turn animation (~1.5 s/turn on 2.0.13).
            var race = GameSession.GatherRaceMs(turnCount);
            Assert.Equal(400 + 1500L * turnCount, race);
            var early = await session.TickAsync(startAt + race - 1);
            Assert.DoesNotContain(early, p => GameSessionTestHelpers.Op(p) == GameSession.GatherCompletedNt);
            var done = await session.TickAsync(startAt + Math.Max(race, node.InteractTime) + 100);
            Assert.Contains(done, p => GameSessionTestHelpers.Op(p) == GameSession.GatherCompletedNt && p[2] == 0);
        }
        finally { WireContext.Current = previous; }
    }

    [Fact]
    public async Task V213_three_attempt_node_survives_two_wins_and_despawns_on_the_third()
    {
        var (node, index, key) = FirstGatherNode();
        var total = node.Gather!.Length >= 7 && node.Gather[6] > 0 ? (int)node.Gather[6] : 3;
        var session = await SessionOnNodeAsync(new FakeCharacterStore(), node, Now);
        session.GatherRoll = () => 0;

        var previous = Pin(WireVersion.V213);
        try
        {
            var wait = Math.Max(GameSession.GatherRaceMs(TurnsOf(node)), node.InteractTime) + 100;
            var at = Now + 10;
            for (var attempt = 1; attempt <= total; attempt++)
            {
                var start = await session.HandleAsync(new PacketWriter().U16(GameSession.GatherStartRq).Varint((uint)key).ToArray(), at);
                Assert.Contains(start, p => GameSessionTestHelpers.Op(p) == GameSession.GatherStartRs);
                var done = await session.TickAsync(at + wait);
                Assert.Contains(done, p => GameSessionTestHelpers.Op(p) == GameSession.GatherCompletedNt && p[2] == 0);
                if (attempt < total)
                {
                    Assert.DoesNotContain(done, p => p.SequenceEqual(GamePackets.EnvObjDespawn(key)));
                    Assert.Contains(done, p => GameSessionTestHelpers.Op(p) == 0x363B);
                    Assert.Equal(total - attempt, session.GatherAttemptsLeft(index));
                }
                else
                {
                    Assert.Contains(done, p => p.SequenceEqual(GamePackets.EnvObjDespawn(key)));
                    Assert.Equal(-1, session.GatherAttemptsLeft(index));
                }
                at += wait + 500;
            }
        }
        finally { WireContext.Current = previous; }
    }

    [Fact]
    public async Task V213_lost_attempt_completes_the_interaction_before_0x8d33_and_allows_mounting()
    {
        var (node, _, key) = FirstGatherNode();
        var store = new FakeCharacterStore();
        store.RideRows.Add(new RideRecord(PetRide, 1, 0, Owned: true));
        store.DefaultRide = PetRide;
        var session = await SessionOnNodeAsync(store, node, Now);
        session.GatherRoll = () => 9999;   // a lost attempt

        var previous = Pin(WireVersion.V213);
        try
        {
            var startAt = Now + 10;
            await session.HandleAsync(new PacketWriter().U16(GameSession.GatherStartRq).Varint((uint)key).ToArray(), startAt);
            var done = await session.TickAsync(startAt + Math.Max(GameSession.GatherRaceMs(TurnsOf(node)), node.InteractTime) + 100);

            var completed = done.FindIndex(p => GameSessionTestHelpers.Op(p) == 0x363A);
            var gatherDone = done.FindIndex(p => GameSessionTestHelpers.Op(p) == GameSession.GatherCompletedNt && p[2] == 2);
            Assert.True(completed >= 0, "0x363A InteractEnvObjCompleted_NT must leave the interact state");
            Assert.True(gatherDone > completed, "0x363A must precede 0x8D33");
            Assert.Contains(done, p => GameSessionTestHelpers.Op(p) == 0x8D34);

            await session.HandleAsync(new PacketWriter().U16(RidePackets.GetOnRq).I32(PetRide).ToArray(), startAt + 3_000);
            Assert.Equal(PetRide, session.MountedRide);
        }
        finally { WireContext.Current = previous; }
    }

    [Fact]
    public async Task V213_gather_stop_leaves_the_interaction_state_before_available()
    {
        var (node, _, key) = FirstGatherNode();
        var session = await SessionOnNodeAsync(new FakeCharacterStore(), node, Now);
        session.GatherRoll = () => 0;

        var previous = Pin(WireVersion.V213);
        try
        {
            await session.HandleAsync(new PacketWriter().U16(GameSession.GatherStartRq).Varint((uint)key).ToArray(), Now + 10);
            var stop = await session.HandleAsync(new PacketWriter().U16(0x8D32).Varint((uint)key).ToArray(), Now + 20);
            var interrupted = stop.FindIndex(p => GameSessionTestHelpers.Op(p) == 0x3639);
            var available = stop.FindIndex(p => GameSessionTestHelpers.Op(p) == 0x363B);
            Assert.True(interrupted >= 0);
            Assert.True(available > interrupted);
        }
        finally { WireContext.Current = previous; }
    }

    [Fact]
    public async Task V213_starting_a_gather_dismounts_first()
    {
        var (node, _, key) = FirstGatherNode();
        var store = new FakeCharacterStore();
        store.RideRows.Add(new RideRecord(PetRide, 1, 0, Owned: true));
        store.DefaultRide = PetRide;
        var session = await SessionOnNodeAsync(store, node, Now);

        var previous = Pin(WireVersion.V213);
        try
        {
            await session.HandleAsync(new PacketWriter().U16(RidePackets.GetOnRq).I32(PetRide).ToArray(), Now + 5);
            Assert.Equal(PetRide, session.MountedRide);

            session.GatherRoll = () => 0;
            var start = await session.HandleAsync(new PacketWriter().U16(GameSession.GatherStartRq).Varint((uint)key).ToArray(), Now + 10);
            Assert.Null(session.MountedRide);
            Assert.Contains(start, p => GameSessionTestHelpers.Op(p) == RidePackets.GetOffNt);
        }
        finally { WireContext.Current = previous; }
    }
}
