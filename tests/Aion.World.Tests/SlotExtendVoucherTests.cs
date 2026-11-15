using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Voucher and character-warehouse slot extensions (ported from Addiboii/A2Core 3149109/cff553c).</summary>
public sealed class SlotExtendVoucherTests
{
    const long Now = 1_700_000_000_000;

    static byte[] ExtendRq(byte container, long uid = 0, byte costIdx = 0) =>
        new PacketWriter().U16(GameSession.ItemContainerSlotExtend8dRq).I32(0).U8(container).I64(uid).U8(costIdx).ToArray();

    static byte[] Rs(List<byte[]> replies) => replies.First(p => GameSessionTestHelpers.Op(p) == GameSession.ItemContainerSlotExtend8dRs);

    [Fact]
    public async Task Bag_voucher_opens_one_row_and_is_used_up_without_kina()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var character = store.States[0].Summary.CharacterId;
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 50_000, 9201));
        var voucher = FakeCharacterStore.BagItem(534000001, 1, 9202);
        store.Items.Add(voucher);
        await session.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now);

        var replies = await session.HandleAsync(ExtendRq(InventoryItem.Bag, voucher.WireId), Now + 1);
        var rs = Rs(replies);
        Assert.Equal(GameSession.SlotExtendSuccess, BitConverter.ToInt32(rs, 2));
        Assert.Equal(1, rs[^2]);   // by item
        Assert.Equal(0, rs[^1]);   // by gold
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == GameSession.ItemContainerSlotExtend8dNt);
        Assert.Equal(1, store.ContainerExtends[character].InventoryByItem);
        Assert.DoesNotContain(store.Items, i => i.InstanceId == voucher.InstanceId);
        Assert.Equal(50_000, store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity));
        var depot = replies.Last(p => GameSessionTestHelpers.Op(p) == 0x5611);
        Assert.Equal(1, depot[^6]);
    }

    [Fact]
    public async Task Warehouse_voucher_does_not_extend_the_bag()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var character = store.States[0].Summary.CharacterId;
        var voucher = FakeCharacterStore.BagItem(536700001, 1, 9211);
        store.Items.Add(voucher);
        await session.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now);

        var rs = Rs(await session.HandleAsync(ExtendRq(InventoryItem.Bag, voucher.WireId), Now + 1));
        Assert.Equal(GameSession.SlotExtendInvalidItem, BitConverter.ToInt32(rs, 2));
        Assert.Contains(store.Items, i => i.InstanceId == voucher.InstanceId);
        Assert.False(store.ContainerExtends.TryGetValue(character, out var c) && c.InventoryByItem > 0);

        var ok = Rs(await session.HandleAsync(ExtendRq(InventoryItem.Storage, voucher.WireId), Now + 2));
        Assert.Equal(GameSession.SlotExtendSuccess, BitConverter.ToInt32(ok, 2));
        Assert.Equal(1, store.ContainerExtends[character].StorageByItem);
    }

    [Fact]
    public async Task Voucher_refused_at_the_container_maximum()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var character = store.States[0].Summary.CharacterId;
        store.ContainerExtends[character] = new ContainerSlotExtendCounts(GameSession.MaxBagExtensions - 10, 10, 0, 0, 0, 0);
        var voucher = FakeCharacterStore.BagItem(534000002, 1, 9221);
        store.Items.Add(voucher);
        await session.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now);

        var rs = Rs(await session.HandleAsync(ExtendRq(InventoryItem.Bag, voucher.WireId), Now + 1));
        Assert.Equal(GameSession.SlotExtendOverMax, BitConverter.ToInt32(rs, 2));
        Assert.Contains(store.Items, i => i.InstanceId == voucher.InstanceId);
    }

    [Fact]
    public async Task Character_warehouse_row_for_kina_and_by_voucher()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var character = store.States[0].Summary.CharacterId;
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 10_000, 9231));
        var voucher = FakeCharacterStore.BagItem(536700003, 1, 9232);
        store.Items.Add(voucher);
        await session.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now);

        var gold = await session.HandleAsync(ExtendRq(GameSession.CharacterStorageContainer), Now + 1);
        Assert.Equal(GameSession.SlotExtendSuccess, BitConverter.ToInt32(Rs(gold), 2));
        Assert.Equal(1, store.ContainerExtends[character].CharacterStorageByGold);
        Assert.Equal(9_000, store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity));
        var mods = gold.Where(p => GameSessionTestHelpers.Op(p) == 0x561b).ToList();
        Assert.NotEmpty(mods);
        Assert.Equal(GameSession.DecreaseExtendStorage, mods[^1][^2]);

        var byItem = Rs(await session.HandleAsync(ExtendRq(GameSession.CharacterStorageContainer, voucher.WireId), Now + 2));
        Assert.Equal(GameSession.SlotExtendSuccess, BitConverter.ToInt32(byItem, 2));
        Assert.Equal(1, store.ContainerExtends[character].CharacterStorageByItem);
        Assert.Equal(1, byItem[^2]);
        Assert.Equal(1, byItem[^1]);
    }
}
