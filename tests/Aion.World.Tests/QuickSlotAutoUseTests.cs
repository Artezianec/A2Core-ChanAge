using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QuickSlotAutoUseTests
{
    const long Now = 4_000_000;
    const int Food = 510715007;
    const int BarrierScroll = 531060005;
    const byte HotbarSlot = 16;

    static byte[] QuickSlotItemSet(int slot, long uid, int itemId, bool autoUse) =>
        new PacketWriter().U16(GameSession.QuickSlotSetRq).U8(0).U8(slot).U8(1).I64(uid).I32(itemId).I32(0).U8(0).I32(0).U8(autoUse ? 1 : 0).ToArray();

    [Fact]
    public async Task QuickSlotSet_persists_auto_use_toggle()
    {
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(Food, 5, 90));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        var rs = await session.HandleAsync(QuickSlotItemSet(HotbarSlot, 90, Food, autoUse: true), Now);
        Assert.Equal(GameSession.QuickSlotSetRs, GameSessionTestHelpers.Op(rs[0]));
        Assert.Equal(0, rs[0][2]);
        Assert.True(store.QuickSlots.TryGetValue(HotbarSlot, out var saved));
        Assert.True(saved.AutoUse);
        Assert.True(session.QuickSlots[HotbarSlot].AutoUse);
    }

    [Fact]
    public async Task Server_tick_auto_uses_when_buff_is_missing()
    {
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(Food, 2, 91));
        store.QuickSlots[HotbarSlot] = new QuickSlotRecord(HotbarSlot, 1, 91, Food, 0, null, true);
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        var hpBefore = session.Resource!.HpMax;
        var tick = await session.TickAsync(Now + 200);
        Assert.True(GameSessionTestHelpers.HasOpcode(tick, RidePackets.ItemUseRs));
        Assert.True(session.Resource.HpMax > hpBefore);
        Assert.Equal(1, store.Items.Single(i => i.TemplateId == Food).Quantity);
    }

    [Fact]
    public async Task Server_tick_skips_auto_use_while_buff_is_active()
    {
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(Food, 2, 92));
        store.QuickSlots[HotbarSlot] = new QuickSlotRecord(HotbarSlot, 1, 92, Food, 0, null, true);
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        await session.TickAsync(Now + 200);
        var qtyAfterFirst = store.Items.Single(i => i.TemplateId == Food).Quantity;
        await session.TickAsync(Now + 400);
        Assert.Equal(qtyAfterFirst, store.Items.Single(i => i.TemplateId == Food).Quantity);
    }

    [Fact]
    public async Task ItemUseByItemId_accepts_client_auto_use()
    {
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(BarrierScroll, 1, 93));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        var rq = new PacketWriter().U16(RidePackets.ItemUseByItemIdRq).I64(93).I32(BarrierScroll).I32(1).ToArray();
        var rs = await session.HandleAsync(rq, Now + 500);
        Assert.Equal(RidePackets.ItemUseByItemIdRs, GameSessionTestHelpers.Op(rs[0]));
        Assert.Equal(0, BitConverter.ToInt32(rs[0], 2));
        Assert.Contains(22120011, session.ActiveBuffs);
        Assert.DoesNotContain(store.Items, i => i.TemplateId == BarrierScroll && i.Quantity > 0);
    }
}
