using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>The 2.0.13 GatherSourceStart_RS: raw per-turn gauges and its own turn pacing.</summary>
public sealed class GatherV213WireTests
{
    const long Now = 1_760_300_000_000;

    static (WorldObject Node, int Index, int Key) FirstGatherNode(int mapId = 1010)
    {
        var hit = WorldObjects.ObjectsOn(mapId).Select((o, i) => (o, i)).First(x => x.o.Gather is not null);
        return (hit.o, hit.i, WorldObjects.FirstKey + hit.i);
    }

    [Fact]
    public async Task V213_reply_carries_raw_per_turn_gauges_and_no_trailing_i32()
    {
        var store = new FakeCharacterStore { Progress = (55, 0) };
        var session = await GameSessionTestHelpers.EnterMap1010Async(store, Now);
        var (node, _, key) = FirstGatherNode();
        session.Teleport(1010, node.At with { X = node.At.X + 50 }, Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        session.GatherRoll = () => 0;   // a winning attempt

        var previous = WireContext.Current;
        WireContext.Current = WireVersion.V213;
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
            var target = node.Gather!.Length >= 6 && node.Gather[5] > 0 ? node.Gather[5] : 360;
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
}
