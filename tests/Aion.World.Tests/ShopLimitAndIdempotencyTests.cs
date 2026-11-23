using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class ShopLimitAndIdempotencyTests
{
    const long Now = 1_760_100_000_000;

    [Fact]
    public void Official_catalog_goods_carry_quota_limits()
    {
        var row = OfficialShopGoods.Embedded.ToShopGoods(1218);
        Assert.NotNull(row);
        Assert.Equal(2, row!.Limit);
        Assert.Equal(1, row.LimitTarget);
        Assert.Equal(3, row.LimitPeriod);
        Assert.True(OfficialShopGoods.TryPurchaseLimit(1218, out var limit, out var target, out var period));
        Assert.Equal(2, limit);
        Assert.Equal(1, target);
        Assert.Equal(3, period);
    }

    [Fact]
    public async Task BM_quna_purchase_is_idempotent_on_immediate_retry()
    {
        const int goodsId = 1242, itemId = 534200008;
        GameSession.ConfigureShopOptions(new ShopOptions { PremiumMode = ShopPremiumMode.Quna });
        var store = new FakeCharacterStore(Guid.NewGuid());
        store.Quna.Seed(store.AccountId, 200_000);
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var pkt = new PacketWriter().U16(GameSession.NPShopPurchaseRq).I32(goodsId).I32(1).ToArray();

        var first = await session.HandleAsync(pkt, Now);
        var second = await session.HandleAsync(pkt, Now + 500);
        Assert.Equal(0, BitConverter.ToUInt16(first[0], 2));
        Assert.Equal(0, BitConverter.ToUInt16(second[0], 2));
        Assert.Single(store.Quna.Ledger, e => e.Reason == QunaWalletRules.Purchase);
        Assert.Single(store.Items, i => i.TemplateId == itemId && i.Location == "bag");
    }

    [Fact]
    public async Task BM_quna_purchase_charges_twice_when_same_goods_ten_seconds_apart()
    {
        const int goodsId = 1213;
        GameSession.ConfigureShopOptions(new ShopOptions { PremiumMode = ShopPremiumMode.Quna });
        var store = new FakeCharacterStore(Guid.NewGuid());
        store.Quna.Seed(store.AccountId, 500_000);
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var pkt = new PacketWriter().U16(GameSession.NPShopPurchaseRq).I32(goodsId).I32(1).ToArray();
        var itemsBefore = store.Items.Count;

        var first = await session.HandleAsync(pkt, Now);
        var second = await session.HandleAsync(pkt, Now + 10_000);
        Assert.Equal(0, BitConverter.ToUInt16(first[0], 2));
        Assert.Equal(0, BitConverter.ToUInt16(second[0], 2));
        Assert.Equal(2, store.Quna.Ledger.Count(e => e.Reason == QunaWalletRules.Purchase));
        Assert.Equal(itemsBefore + 2, store.Items.Count);
    }

    [Fact]
    public async Task BM_quna_purchase_with_client_serial_charges_each_serial()
    {
        const int goodsId = 1213;
        GameSession.ConfigureShopOptions(new ShopOptions { PremiumMode = ShopPremiumMode.Quna });
        var store = new FakeCharacterStore(Guid.NewGuid());
        store.Quna.Seed(store.AccountId, 500_000);
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var a = new PacketWriter().U16(GameSession.NPShopPurchaseRq).I32(goodsId).I32(1).I32(101).ToArray();
        var b = new PacketWriter().U16(GameSession.NPShopPurchaseRq).I32(goodsId).I32(1).I32(102).ToArray();

        await session.HandleAsync(a, Now);
        await session.HandleAsync(b, Now + 500);
        Assert.Equal(2, store.Quna.Ledger.Count(e => e.Reason == QunaWalletRules.Purchase));
    }

    [Fact]
    public async Task BM_quna_purchase_refuses_when_over_quota()
    {
        const int goodsId = 1218;
        GameSession.ConfigureShopOptions(new ShopOptions { PremiumMode = ShopPremiumMode.Quna });
        var store = new FakeCharacterStore(Guid.NewGuid());
        store.Quna.Seed(store.AccountId, 500_000);
        var period = ShopTables.Period(3, DateTimeOffset.FromUnixTimeMilliseconds(Now));
        store.Purchases.Add((1, goodsId, period, 2));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var buy = await session.HandleAsync(new PacketWriter().U16(GameSession.NPShopPurchaseRq).I32(goodsId).I32(1).ToArray(), Now);
        Assert.Equal(3, BitConverter.ToUInt16(buy[0], 2));
        Assert.DoesNotContain(store.Quna.Ledger, e => e.Reason == QunaWalletRules.Purchase);
    }
}