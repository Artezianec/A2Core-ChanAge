using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class ItemUseEffectHandlerTests
{
    const long Now = 1792000000000L;

    [Fact]
    public async Task BattlePassExp_item_consumes_and_grants_exp_to_matching_group()
    {
        const int item = 503700042;
        Assert.Equal("BattlePassExp", RewardBoxes.Embedded.Use(item)!.Effect);
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(item, 2, 901));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        const int freeExp = 810433000;
        var before = session.AchievementCount(freeExp);

        var wire = store.Items.Single(i => i.TemplateId == item).WireId;
        var replies = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(wire).ToArray(), Now + 1);
        Assert.Equal(0, replies[0][2]);
        Assert.Equal(1, store.Items.Single(i => i.TemplateId == item).Quantity);
        Assert.True(session.AchievementCount(freeExp) > before);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == GameSession.BattlePassInfoListNt);
    }

    [Fact]
    public async Task AddContentsTicket_item_consumes_and_persists_ticket()
    {
        const int item = 503700031;
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(item, 3, 902));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);

        var before = session.ContentTicketCount(60000001, Now);
        var wire = store.Items.Single(i => i.TemplateId == item).WireId;
        var replies = await session.HandleAsync(
            new PacketWriter().U16(RidePackets.ItemUseByItemIdRq).I64(wire).I32(item).I32(2).I32(0).ToArray(), Now + 1);
        var rs = replies.Single(p => GameSessionTestHelpers.Op(p) == RidePackets.ItemUseByItemIdRs);
        Assert.Equal(0, BitConverter.ToInt32(rs, 2));
        Assert.Equal(2, BitConverter.ToInt32(rs, 18));
        Assert.Equal(1, store.Items.Where(i => i.TemplateId == item).Sum(i => i.Quantity));
        Assert.Equal(Math.Min(ContentTicketTables.Embedded.Of(60000001)!.Max, before + 80), session.ContentTicketCount(60000001, Now));
    }

    [Fact]
    public async Task Not_direct_use_effect_refuses_without_unimplemented_log_reason()
    {
        const int enchantScroll = 516520001;
        Assert.Equal("EnchantScroll", RewardBoxes.Embedded.Use(enchantScroll)?.Effect);
        var store = new FakeCharacterStore();
        store.Items.Add(FakeCharacterStore.BagItem(enchantScroll, 1, 903));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        var replies = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(903).ToArray(), Now + 1);
        Assert.Equal((ushort)1, BitConverter.ToUInt16(replies[0], 2));
        Assert.Equal(1, store.Items.Single(i => i.TemplateId == enchantScroll).Quantity);
        Assert.True(RewardBoxes.Embedded.ImplementsUseEffect("EnchantScroll"));
    }
}