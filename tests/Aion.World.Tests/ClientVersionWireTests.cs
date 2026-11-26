using Aion.Protocol.Native;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Wire-level checks for the per-connection 2.0.12 packet variants.</summary>
public sealed class ClientVersionWireTests
{
    static ushort Op(byte[] packet) => BitConverter.ToUInt16(packet, 0);

    [Fact]
    public void Select_and_load_use_the_2_0_12_opcodes()
    {
        var select = GamePackets.SelectCharacterWorld(1, 2, 3);
        Assert.Equal(0xA8F9, Op(select));
        var load = GamePackets.LoadCompleteWorld(7, 3, new Vector3d(1, 2, 3));
        Assert.Equal(0x4EF8, Op(load));
    }

    [Fact]
    public void Create_result_is_u16_on_modern_and_i32_on_2_0_9()
    {
        var state = WireContext.Current;
        try
        {
            WireContext.Current = WireVersion.V212;
            var modern = GamePackets.CharacterCreated("Name", 1, 2, 1, 0x1234, 0x5678, [1, 2, 3], 4, 5, 1000);
            Assert.Equal(0x361C, Op(modern));
            Assert.Equal(0, BitConverter.ToUInt16(modern, 2));
            Assert.Equal(0x1234, BitConverter.ToInt32(modern, 4));

            WireContext.Current = WireVersion.V209;
            var legacy = GamePackets.CharacterCreated("Name", 1, 2, 1, 0x1234, 0x5678, [1, 2, 3], 4, 5, 1000);
            Assert.Equal(0, BitConverter.ToInt32(legacy, 2));
        }
        finally { WireContext.Current = state; }
    }

    [Fact]
    public void MyCharAppear_uses_the_2_0_12_body_mask()
    {
        var state = WireContext.Current;
        try
        {
            WireContext.Current = WireVersion.V212;
            var packet = GamePackets.MyCharAppear(new MyCharacterAppear
            {
                Key = 1, Name = "Name", Position = new Vector3d(1, 2, 3),
            });
            Assert.Equal(0x3633, Op(packet));
            Assert.Equal(0x2AC1815Fu, BitConverter.ToUInt32(packet, 3));
        }
        finally { WireContext.Current = state; }
    }

    [Fact]
    public void Dodge_result_is_flags_first_on_modern()
    {
        var state = WireContext.Current;
        try
        {
            WireContext.Current = WireVersion.V212;
            var modern = GamePackets.DodgeResult(11000100);
            Assert.Equal(new byte[] { 0x01, 0x38, 0x00, 0x00, 0x00 }, modern[..5]);

            WireContext.Current = WireVersion.V209;
            var legacy = GamePackets.DodgeResult(11000100);
            Assert.Equal(0x01, legacy[0]);
            Assert.Equal(0x38, legacy[1]);
            Assert.Equal(0, BitConverter.ToUInt16(legacy, 2));
        }
        finally { WireContext.Current = state; }
    }
}
