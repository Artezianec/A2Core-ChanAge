using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    static ShopFeatureOptions hostShopFeatures = new();

    public static ShopFeatureOptions HostShopFeatures => hostShopFeatures;

    public static void ConfigureShopFeatures(ShopFeatureOptions options) => hostShopFeatures = options;

    readonly ShopFeatureOptions shopFeatures = options.ShopFeatures ?? hostShopFeatures;
    Guid? shopGiftDeliverTo;

    /// <summary>Exported / platform goods row with Mileage-wallet pricing when configured.</summary>
    ShopTables.GoodsRow? ResolveShopGoodsRow(int goodsId)
    {
        var row = ResolveExportedGoods(goodsId);
        return MileageInWallet && shopFeatures.UseMileageCurrency
            ? ShopFeatureLayer.ApplyMileageWallet(row, goodsId, shopFeatures)
            : row;
    }

    bool ShopRewardsPreferMail(IEnumerable<int> templateIds, IEnumerable<int> goodsIds) =>
        shopFeatures.MailDeliveryEnabled && (
            BagSlotCount() >= ShopBagSlotCap
            || goodsIds.Any(ShopFeatureLayer.PrefersMailDelivery)
            || templateIds.Any(_ => false));

    int BagSlotCount() => storedItems.Values.Count(i => i.Location == "bag");

    async Task<List<byte[]>> ExecuteShopPurchaseAsync(string kind, List<(int Goods, int Count)> entries, long now, CancellationToken ct,
        Func<int, byte[]> reply, INativeCharacterStore store, Guid character, Func<int, ShopTables.GoodsRow?> resolve, int? clientPurchaseSerial = null)
    {
        var orders = new List<ShopOrderLine>();
        var rewards = new List<(int, long)>();
        var prices = new Dictionary<int, long>();
        var limits = new List<(int, int, string, int, int)>();
        var goodsIds = new List<int>();
        var at = DateTimeOffset.FromUnixTimeMilliseconds(now);
        foreach (var (goodsId, count) in entries)
        {
            goodsIds.Add(goodsId);
            if (count < 1 || count > PacketSecurity.MaxShopLineCount || resolve(goodsId) is not { } g || level < g.Level) return [reply(1)];
            if (!PacketSecurity.TryMultiply(g.Bundle, count, out var bundleTotal) || !PacketSecurity.TryMultiply(g.Price, count, out var linePrice)) return [reply(1)];
            var priced = ApplyPremiumPrice(g.Currency, linePrice);
            if (priced is null) return [reply(1)];
            var parts = OfficialShopGoods.Embedded.Parts(goodsId);
            if (parts.Count > 0)
            {
                foreach (var (itemId, itemCount) in parts)
                {
                    if (!PacketSecurity.TryMultiply(itemCount, count, out var partTotal)) return [reply(1)];
                    rewards.Add((itemId, partTotal));
                }
            }
            else rewards.Add((g.ItemId, bundleTotal));
            orders.Add(new(kind, goodsId, count, priced.Value.Currency, priced.Value.Price));
            if (!PacketSecurity.TryAdd(prices.GetValueOrDefault(priced.Value.Currency), priced.Value.Price, out var sum)) return [reply(1)];
            prices[priced.Value.Currency] = sum;
            if (g.Limit > 0) limits.Add((g.LimitTarget, goodsId, ShopTables.Period(g.LimitPeriod, at), count, g.Limit));
        }
        foreach (var (itemId, _) in rewards)
        {
            if (ShopDeliverableNeedsUseData(itemId) && Boxes.Use(itemId) is null)
                return [reply(1)];
        }
        var costs = PurchaseCostsWithMileage(prices.Select(p => (p.Key, p.Value)));
        if (costs is not { } paid) return [reply(2)];
        var fingerprint = ShopPurchaseRequest.Fingerprint(store.WorldId, store.AccountId, character, kind, entries, clientPurchaseSerial);
        var purchaseId = await store.ResolveShopPurchaseRequestIdAsync(fingerprint, now, ct);
        if (orders.Count > 0) orders[0] = orders[0] with { RequestId = purchaseId };
        var viaMail = ShopRewardsPreferMail(rewards.Select(r => r.Item1), goodsIds);
        var pay = await PayMileageThenQunaAsync(paid.Mileage, paid.Quna,
            new { kind, goods = entries.Select(e => new[] { e.Goods, e.Count }).ToArray() },
            async (quna, mileage, preCtx) =>
            {
                var ctx = (preCtx ?? new ShopBuyContext(mileage)) with { DeliverViaMail = viaMail || shopGiftDeliverTo is not null,
                    QunaRequestId = quna?.RequestId ?? preCtx?.QunaRequestId, OrderGoodsId = entries.Count == 1 ? entries[0].Goods : preCtx?.OrderGoodsId,
                    GiftToCharacter = shopGiftDeliverTo ?? preCtx?.GiftToCharacter };
                shopGiftDeliverTo = null;
                return await StoredOk(() => store.ShopBuyAsync(character, paid.Costs, rewards, limits, orders, ct, quna, ctx));
            }, ct, purchaseId);
        if (pay == QunaPay.NotEnough) return [reply(2)];
        if (pay != QunaPay.Done) return [reply(limits.Count > 0 ? 3 : 1)];
        if (paid.Quna > 0)
            await EarnMileageOnQunaSpendAsync(paid.Quna, new { kind, quna = paid.Quna }, ct);
        var replies = new List<byte[]> { reply(0) };
        if (!viaMail)
            replies.AddRange(await InventoryChangesAsync(ct, IncreaseShopBuy, DecreaseShopBuy));
        else
            replies.AddRange(await MailLoadHintAsync(ct));
        replies.AddRange(await AutoUseAcquiredItemsAsync(rewards.Select(r => r.Item1), now, ct));
        if (paid.Quna > 0) replies.AddRange(QunaRefreshIfChanged());
        replies.AddRange(MileageRefreshIfChanged());
        return replies;
    }

    async Task<List<byte[]>> MailLoadHintAsync(CancellationToken ct)
    {
        if (Store is not { } store || CharacterId is not { } character) return [];
        var posts = await StoredValue(() => store.PostsAsync(character, ct), (IReadOnlyList<PostRecord>)[]);
        var latest = posts.OrderByDescending(p => p.Sent).FirstOrDefault();
        return latest is null ? [] : [WritePost(PacketWriter.Opcode(PostRecvNt), latest).ToArray()];
    }
}