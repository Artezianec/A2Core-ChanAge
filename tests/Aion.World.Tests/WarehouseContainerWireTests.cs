using Aion.Protocol.Native;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// 2.0.12 swapped the warehouse container ids: 15 is the per-character warehouse and 2/3 its account
/// alias, while 2.0.9 keeps 2 for the character warehouse and 3/15 for the account one. Also pins the
/// soul-bind attune reply layout (0x5632).
/// </summary>
public sealed class WarehouseContainerWireTests
{
    [Fact]
    public void Container_ids_follow_the_client_generation()
    {
        var state = WireContext.Current;
        try
        {
            WireContext.Current = WireVersion.V209;
            Assert.Equal("storage", GameSession.ContainerToLocation(InventoryItem.Storage));
            Assert.Equal("account_storage", GameSession.ContainerToLocation(InventoryItem.AccountStorage));
            Assert.Equal("account_storage", GameSession.ContainerToLocation(GameSession.CharacterStorageContainer));
            Assert.Equal(InventoryItem.Storage, GameSession.ContainerOf("storage"));
            Assert.Equal(InventoryItem.AccountStorage, GameSession.ContainerOf("account_storage"));

            WireContext.Current = WireVersion.V212;
            Assert.Equal("storage", GameSession.ContainerToLocation(GameSession.CharacterStorageContainer));
            Assert.Equal("account_storage", GameSession.ContainerToLocation(InventoryItem.Storage));
            Assert.Equal("account_storage", GameSession.ContainerToLocation(InventoryItem.AccountStorage));
            Assert.Equal(GameSession.CharacterStorageContainer, GameSession.ContainerOf("storage"));
            Assert.Equal(InventoryItem.Storage, GameSession.ContainerOf("account_storage"));
        }
        finally { WireContext.Current = state; }
    }

    [Fact]
    public void Soul_bind_attune_reply_uses_the_client_layout()
    {
        var packet = GamePackets.SoulBindAttune(0x116000005f825, 318, 0, true, (194, 20), (0, 0));
        Assert.Equal(0x5632, BitConverter.ToUInt16(packet, 0));
        Assert.Equal(0, BitConverter.ToUInt16(packet, 2));         // result
        Assert.Equal(0x116000005f825, BitConverter.ToInt64(packet, 4));
        Assert.Equal(318, BitConverter.ToUInt16(packet, 12));      // select stat
        Assert.Equal(0, BitConverter.ToInt32(packet, 14));         // select skill
        Assert.Equal(1, packet[18]);                               // success bit
        Assert.Equal(194, BitConverter.ToUInt16(packet, 19));      // new stat type
        Assert.Equal(20, BitConverter.ToInt32(packet, 21));        // new stat value
        Assert.Equal(0, BitConverter.ToInt32(packet, 25));         // new skill id
        Assert.Equal(0, packet[29]);                               // new skill level
        Assert.Equal(30, packet.Length);
    }

    [Fact]
    public async Task Soul_bind_attune_request_is_answered_even_when_it_fails()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), 0);
        var packet = new PacketWriter().U16(0x5631).I64(123).U16(318).I32(0).I64(456).ToArray();
        Assert.Equal(24, packet.Length);
        var replies = await session.HandleAsync(packet, 1);
        Assert.NotEmpty(replies);
        Assert.Equal(0x5632, GameSessionTestHelpers.Op(replies[0]));
        Assert.Equal(1, BitConverter.ToUInt16(replies[0], 2));     // unknown item -> result 1, not a hang
    }
}
