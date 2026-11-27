using Aion.Protocol.Native;
using Xunit;

namespace Aion.World.Tests;

/// <summary>The 2.0.12 dispatch table differs from the pinned 2.0.9 one; the edge translator must round-trip it.</summary>
public sealed class ProtocolTranslationTests
{
    static ushort Op(byte[] body) => BitConverter.ToUInt16(body, 0);

    [Theory]
    [InlineData(0x3B69, 0xE264)]   // trade sell
    [InlineData(0x3B77, 0xE272)]   // trade summary
    [InlineData(0x3AC1, 0x9700)]   // dungeon room list
    [InlineData(0x3AF5, 0x9734)]   // dungeon retry fail
    [InlineData(0xE2A8, 0xE30F)]   // matching state
    [InlineData(0xE2C5, 0xE32C)]   // arena record
    [InlineData(0x933C, 0x9400)]   // duel call-out
    [InlineData(0x934A, 0x940E)]   // duel cancel
    [InlineData(0x3776, 0x5634)]   // godstone update
    [InlineData(0x377B, 0x5639)]   // godstone combine
    public void Renumbered_families_round_trip(ushort v209, ushort v212)
    {
        var body = new byte[] { (byte)v209, (byte)(v209 >> 8), 0xAA };
        var modern = ProtocolTranslation.ToClient(body);
        Assert.Equal(v212, Op(modern));
        Assert.Equal(v209, Op(ProtocolTranslation.ToServer(modern)));
    }

    /// <summary>
    /// 2.0.13 uses the old numbers for movement/skills/projectiles, so these families are not
    /// rewritten at the frame edge: the writer emits the modern number and dispatch matches it.
    /// </summary>
    [Theory]
    [InlineData(0x3813, 0x5718)]   // party loot begin
    [InlineData(0x3830, 0x5735)]   // party loot result / LockOnTarget_RQ
    [InlineData(0x372C, 0x4300)]   // airspace battle
    [InlineData(0x373A, 0x430E)]   // airspace ranking
    [InlineData(0x385B, 0x6302)]   // abyss rift progress
    [InlineData(0x385F, 0x6306)]   // abyss rift phase start
    public void Moved_families_are_written_and_dispatched_by_their_modern_number(ushort v209, ushort v212)
    {
        var previous = WireContext.Current;
        WireContext.Current = WireVersion.V213;
        try
        {
            var legacy = new byte[] { (byte)v209, (byte)(v209 >> 8), 0xAA };
            Assert.Equal(v209, Op(ProtocolTranslation.ToClient(legacy)));
            Assert.Equal(v212, ProtocolTranslation.ForModern(v209));
            var modern = new byte[] { (byte)v212, (byte)(v212 >> 8), 0xAA };
            Assert.Equal(v212, Op(ProtocolTranslation.ToServer(modern)));
        }
        finally { WireContext.Current = previous; }
    }

    [Theory]
    [InlineData(0x3BD1, 0xE333)]   // refresh currency
    [InlineData(0x36EE, 0x3845)]   // skill customizing RQ
    [InlineData(0x36EF, 0x3846)]   // skill customizing RS
    [InlineData(0x930D, 0xE21D)]   // exit instance layer RQ
    [InlineData(0x930E, 0xE21E)]   // exit instance layer RS
    [InlineData(0x9327, 0x931F)]   // party leave NT
    [InlineData(0x3BA9, 0xE30B)]   // monolith list
    [InlineData(0x3C08, 0xFFAD)]   // monolith achievement sync
    [InlineData(0x3A0C, 0x9100)]   // field boss tab
    [InlineData(0x3A0D, 0x9101)]   // field boss reply
    public void Single_moves_round_trip(ushort v209, ushort v212)
    {
        var body = new byte[] { (byte)v209, (byte)(v209 >> 8) };
        var modern = ProtocolTranslation.ToClient(body);
        Assert.Equal(v212, Op(modern));
        Assert.Equal(v209, Op(ProtocolTranslation.ToServer(modern)));
    }

    [Theory]
    [InlineData(0x382A)]   // abnormal add
    [InlineData(0x382C)]   // abnormal remove
    public void Abnormal_packets_inside_the_loot_range_are_not_rewritten(ushort opcode)
    {
        var body = new byte[] { (byte)opcode, (byte)(opcode >> 8) };
        Assert.Same(body, ProtocolTranslation.ToClient(body));
        Assert.Same(body, ProtocolTranslation.ToServer(body));
    }

    [Fact]
    public void Unknown_opcodes_are_left_untouched()
    {
        var body = new byte[] { 0x13, 0x36 };
        Assert.Same(body, ProtocolTranslation.ToClient(body));
        Assert.Same(body, ProtocolTranslation.ToServer(body));
    }
}
