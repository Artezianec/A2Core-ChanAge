using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

[Collection("PlaytestDebug")]
public sealed class InventoryExtendDiagnosticsTests
{
    const long Now = 1_700_000_000_000;

    [Fact]
    public async Task Extend_request_with_trailing_bytes_is_still_handled()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var character = store.States[0].Summary.CharacterId;
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 124_500, 9001));
        var rq = new PacketWriter().U16(GameSession.ItemContainerSlotExtendRq).I32(0).U8(InventoryItem.Bag).I64(0).U8(0).U8(0).U8(0).ToArray();
        var replies = await session.HandleAsync(rq, Now + 1);
        var rs = replies.First(p => GameSessionTestHelpers.Op(p) == GameSession.ItemContainerSlotExtendRs);
        Assert.Equal(0, BitConverter.ToInt32(rs, 2));
        Assert.Equal(1, store.ContainerExtends[character].InventoryByGold);
    }

    [Fact]
    public async Task Unhandled_packet_and_extend_rejects_are_logged()
    {
        var capture = new CaptureSink();
        PlaytestDebug.Sink = capture;
        try
        {
            var store = new FakeCharacterStore();
            var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
            await session.HandleAsync(new PacketWriter().U16(0x7ffe).I32(0x11223344).ToArray(), Now + 1);
            var unhandled = Assert.Single(capture.Logged, a => a.Kind == "unhandled_packet");
            Assert.Equal("0x7ffe", unhandled.Fields["opcode"]);
            Assert.Equal(6, unhandled.Fields["length"]);
            Assert.Equal("FE7F44332211", unhandled.Fields["hex"]);

            // Item path (uid != 0) is not implemented: the reject is logged with its reason.
            var rq = new PacketWriter().U16(GameSession.ItemContainerSlotExtendRq).I32(0).U8(InventoryItem.Bag).I64(42).U8(0).ToArray();
            await session.HandleAsync(rq, Now + 2);
            var reject = Assert.Single(capture.Logged, a => a.Kind == "container_slot_extend");
            Assert.StartsWith("item_path_unsupported", (string)reject.Fields["reason"]!);
        }
        finally
        {
            PlaytestDebug.Sink = null;
        }
    }

    sealed class CaptureSink : IPlaytestDebugSink
    {
        public bool Packets => false;
        public bool Actions => true;
        public bool Entities => false;
        public List<(string Kind, Dictionary<string, object?> Fields)> Logged { get; } = [];
        public void Packet(in PlaytestPacketRecord record) { }
        public void Action(string kind, IReadOnlyDictionary<string, object?> fields) => Logged.Add((kind, new Dictionary<string, object?>(fields)));
        public void EntitySpawn(int entityKey, int datId, string? name) { }
    }
}
