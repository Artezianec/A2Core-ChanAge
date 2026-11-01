using Aion.Protocol.Native;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// 0xe005 live wire (OBSERVED stagger boss 2026-11-01): drain always ends with trailer 0x02;
/// break is a separate 8-byte body <c>00 03 02</c> without max/energy.
/// </summary>
public sealed class StaggerGroggyWireFormatTests
{
    [Theory]
    [InlineData(28000, 100, 100, "05e0e0da010301640000006400000002")]
    [InlineData(28000, 100, 41, "05e0e0da010301640000002900000002")]
    [InlineData(28000, 100, 0, "05e0e0da010301640000000000000002")]
    [InlineData(2001, 50, 50, "05e0d10f0301320000003200000002")]
    [InlineData(898702, 50, 43, "05e08eed360301320000002b00000002")]
    public void Groggy_drain_matches_observed_masked_layout(int key, int max, int energy, string goldenHex)
    {
        var pkt = GamePackets.UpdateGroggyInfo(key, max, energy);
        Assert.Equal(goldenHex, Convert.ToHexString(pkt).ToLowerInvariant());
    }

    [Theory]
    [InlineData(28000, "05e0e0da01000302")]
    [InlineData(898702, "05e08eed36000302")]
    public void Groggy_break_matches_observed_compact_body(int key, string goldenHex)
    {
        var pkt = GamePackets.UpdateGroggyInfoBreak(key);
        Assert.Equal(goldenHex, Convert.ToHexString(pkt).ToLowerInvariant());
    }

    [Fact]
    public void Groggy_packet_is_not_usmap_full_struct_length()
    {
        var pkt = GamePackets.UpdateGroggyInfo(28000, 100, 100);
        Assert.Equal(0xe005, (ushort)(pkt[0] | (pkt[1] << 8)));
        Assert.Equal(16, pkt.Length);
        Assert.NotEqual(45, pkt.Length);
    }
}
