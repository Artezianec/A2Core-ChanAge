using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class WarehouseStorageExpandTests
{
    const long Now = 1_700_000_100_000;

    static byte[] ExtendRq(ushort opcode, byte container, byte costIdx) =>
        new PacketWriter().U16(opcode).I32(0).U8(container).I64(0).U8(costIdx).ToArray();

    [Fact]
    public async Task Gold_warehouse_extension_on_8d4f_deducts_kina_and_updates_depot_tail()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var character = store.States[0].Summary.CharacterId;
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 10_000, 9101));
        await session.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now);

        var replies = await session.HandleAsync(ExtendRq(GameSession.ItemContainerSlotExtend8dRq, InventoryItem.Storage, 0), Now + 1);

        var rs = replies.First(p => GameSessionTestHelpers.Op(p) == GameSession.ItemContainerSlotExtend8dRs);
        Assert.Equal(0, BitConverter.ToInt32(rs, 2));
        Assert.Equal(InventoryItem.Storage, rs[10]);
        Assert.Equal(0, rs[19]);
        Assert.Equal(0, rs[20]);
        Assert.Equal(1, rs[21]);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == GameSession.ItemContainerSlotExtend8dNt);
        Assert.Equal(1, store.ContainerExtends[character].Storage);
        Assert.Equal(9_000, store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity));

        var depot = replies.Last(p => GameSessionTestHelpers.Op(p) == 0x5611);
        var tail = depot.Length - 6;
        Assert.Equal(1, depot[tail + 2]);
    }

    [Fact]
    public async Task Warehouse_extension_on_5848_run_matches_8d4f()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now + 10);
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 10_000, 9102));
        await session.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now + 10);

        var rs = (await session.HandleAsync(ExtendRq(GameSession.ItemContainerSlotExtendRq, InventoryItem.Storage, 0), Now + 11))
            .First(p => GameSessionTestHelpers.Op(p) == GameSession.ItemContainerSlotExtendRs);
        Assert.Equal(0, BitConverter.ToInt32(rs, 2));
        Assert.Equal(9_000, store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity));
    }

    [Fact]
    public async Task Warehouse_extension_persists_after_relogin()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now + 20);
        var character = store.States[0].Summary.CharacterId;
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 10_000, 9103));
        await session.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now + 20);
        await session.HandleAsync(ExtendRq(GameSession.ItemContainerSlotExtend8dRq, InventoryItem.Storage, 0), Now + 21);

        var session2 = await GameSessionTestHelpers.EnterWorldAsync(store, Now + 30);
        var depot = (await session2.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now + 31)).Last(p => GameSessionTestHelpers.Op(p) == 0x5611);
        var tail = depot.Length - 6;
        Assert.Equal(1, depot[tail + 2]);
    }

    [Fact]
    public async Task Stale_storage_costIdx_is_accepted_and_charges_next_tier()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now + 50);
        var character = store.States[0].Summary.CharacterId;
        store.ContainerExtends[character] = new ContainerSlotExtendCounts(0, 0, 1, 0, 0, 0);
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 10_000, 9105));

        var replies = await session.HandleAsync(ExtendRq(GameSession.ItemContainerSlotExtend8dRq, InventoryItem.Storage, 0), Now + 51);
        Assert.Equal(0, BitConverter.ToInt32(replies.First(p => GameSessionTestHelpers.Op(p) == GameSession.ItemContainerSlotExtend8dRs), 2));
        Assert.Equal(2, store.ContainerExtends[character].Storage);
        Assert.Equal(9_000, store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity));
        var depot = replies.Last(p => GameSessionTestHelpers.Op(p) == 0x5611);
        Assert.Equal(2, depot[^4]);
    }

    [Fact]
    public async Task Warehouse_extension_at_max_returns_result_4()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now + 40);
        var character = store.States[0].Summary.CharacterId;
        const int maxStorageExt = 31;
        store.ContainerExtends[character] = new ContainerSlotExtendCounts(0, 0, maxStorageExt, 0, 0, 0);
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 10_000_000, 9104));
        await session.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now + 40);

        var rs = (await session.HandleAsync(ExtendRq(GameSession.ItemContainerSlotExtend8dRq, InventoryItem.Storage, maxStorageExt), Now + 41))
            .First(p => GameSessionTestHelpers.Op(p) == GameSession.ItemContainerSlotExtend8dRs);
        Assert.Equal(4, BitConverter.ToInt32(rs, 2));
        Assert.Equal(10_000_000, store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity));
    }
}
