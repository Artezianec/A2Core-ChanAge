using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class InventoryCubeExpandTests
{
    const long Now = 1_700_000_000_000;

    [Fact]
    public async Task Gold_bag_extension_deducts_kina_and_replies_success()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var character = store.States[0].Summary.CharacterId;
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 124_500, 9001));
        await session.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now);

        var rq = new PacketWriter().U16(GameSession.ItemContainerSlotExtendRq).I32(0).U8(InventoryItem.Bag).I64(0).U8(0).ToArray();
        var replies = await session.HandleAsync(rq, Now + 1);

        var rs = replies.First(p => GameSessionTestHelpers.Op(p) == GameSession.ItemContainerSlotExtendRs);
        Assert.Equal(0, BitConverter.ToInt32(rs, 2));
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == GameSession.ItemContainerSlotExtendNt);
        Assert.Equal(1, store.ContainerExtends[character].InventoryByGold);
        Assert.Equal(119_500, store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity));
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x561b);

        var depot = replies.Last(p => GameSessionTestHelpers.Op(p) == 0x5611);
        Assert.Equal(1, BitConverter.ToInt32(depot, depot.Length - 20));
    }

    [Fact]
    public async Task Live_8d4f_run_same_as_5848_gold_extension()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 124_500, 9003));
        await session.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now);

        var rq = new PacketWriter().U16(GameSession.ItemContainerSlotExtend8dRq).I32(0).U8(InventoryItem.Bag).I64(0).U8(0).ToArray();
        var replies = await session.HandleAsync(rq, Now + 1);

        var rs = replies.First(p => GameSessionTestHelpers.Op(p) == GameSession.ItemContainerSlotExtend8dRs);
        Assert.Equal(0, BitConverter.ToInt32(rs, 2));
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == GameSession.ItemContainerSlotExtend8dNt);
        Assert.Equal(119_500, store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity));
    }

    [Fact]
    public async Task Extension_refuses_when_kina_insufficient()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 1000, 9002));
        await session.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now);

        var rq = new PacketWriter().U16(GameSession.ItemContainerSlotExtendRq).I32(0).U8(InventoryItem.Bag).I64(0).U8(0).ToArray();
        var rs = (await session.HandleAsync(rq, Now + 1)).First(p => GameSessionTestHelpers.Op(p) == GameSession.ItemContainerSlotExtendRs);
        Assert.Equal(3, BitConverter.ToInt32(rs, 2));
        Assert.Equal(1000, store.Items.Where(i => i.TemplateId == GameSession.KinaItemId).Sum(i => i.Quantity));
    }
}
