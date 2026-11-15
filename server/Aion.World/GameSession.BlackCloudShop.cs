using Aion.Protocol.Native;

namespace Aion.World;

// Ported from zaraza87/A2Core dell-merge-2026-11 (29934bea, docs/DELL-CHANGES-2026-11.md). The live opcodes below are answered
// by default, as official (Native:ExperimentalRandomShopArcana = false turns them off); our older
// random shop on the development opcodes (GameSession.Shop.Special.cs) is left as it is.
// The Black Cloud Merchants (the client's random shop; RandomShop.cs: tables, rules, the list packet). Per character and shop
// a list of goods is rolled and kept (game.character_random_shops, migration 131) until its refresh time.
//   RandomShopInfo_RQ 0x5692 {category u8} -> _RS 0x5693 {result u16, category u8} then _NT 0x5691 (the list)   OBSERVED replies
//   RandomShopPurchase_RQ 0x5694 {category u8, goods i32} -> the bag (EIncreaseReason 48 / EDecreaseReason 67 kRandomShopPurchase),
//     AchievementCountUpdate_NT (RandomShopBuy) and _RS 0x5695 {result u16, category u8, goods i32}            OBSERVED replies
//   RandomShopGoodsListRefresh_RQ 0x5696 {category} -> Kina paid (RandomShopRefreshCost step, EDecreaseReason 68), a new list:
//     _RS 0x5697 {result u16, category u8} then _NT                                                           CANDIDATE
//   RandomShopGoodsListRefreshCheck_RQ 0x5698 {category} -> _RS 0x5699 {result u16, category u8} then _NT (renewed when due)  CANDIDATE
// Opcodes: usmap 0x37d3-0x37db + 0x1ebe (the BM shop run's offset). Request bodies from the usmap (client requests are encrypted
// in the recordings: CANDIDATE); the result is a u16 on the wire like every other reply (OBSERVED "93560000c9").
public sealed partial class GameSession
{
    public const ushort BlackCloudShopInfoNt = 0x5691, BlackCloudShopInfoRq = 0x5692, BlackCloudShopInfoRs = 0x5693, BlackCloudShopPurchaseRq = 0x5694, BlackCloudShopPurchaseRs = 0x5695,
        BlackCloudShopRefreshRq = 0x5696, BlackCloudShopRefreshRs = 0x5697, BlackCloudShopRefreshCheckRq = 0x5698, BlackCloudShopRefreshCheckRs = 0x5699;
    const byte IncreaseRandomShop = 48, DecreaseRandomShop = 67, DecreaseRandomShopRefresh = 68;
    const int RandomShopInvalidGoods = 15542, RandomShopExpiredGoodsList = 15543, RandomShopAlreadyPurchased = 15544, RandomShopNotEnoughMoney = 15545,
        RandomShopGoodsCondition = 15546, RandomShopRefreshNotEnoughMoney = 15547, RandomShopRefreshExpired = 15548;   // EResult RandomShop_*

    /// <summary>The Black Cloud Merchants' requests when the experiment is on (null: not one of them).</summary>
    async Task<List<byte[]>?> BlackCloudShopRequestAsync(byte[] plain, long now, CancellationToken ct)
    {
        if (!options.ExperimentalRandomShopArcana || plain.Length < 3 || resources is null) return null;
        return BitConverter.ToUInt16(plain, 0) switch
        {
            BlackCloudShopInfoRq => await BlackCloudShopInfoAsync(plain, now, ct),
            BlackCloudShopPurchaseRq => await BlackCloudShopPurchaseAsync(plain, now, ct),
            BlackCloudShopRefreshRq => await BlackCloudShopRefreshAsync(plain, now, ct),
            BlackCloudShopRefreshCheckRq => await BlackCloudShopRefreshCheckAsync(plain, now, ct),
            _ => null,
        };
    }   // kRandomShopPurchase / kRandomShopGoodsListRefresh

    /// <summary>The character's lists by wire category with their stored revision (0: not stored yet).</summary>
    readonly Dictionary<int, (long Revision, RandomShopOffer Offer)> randomShops = [];
    internal IReadOnlyDictionary<int, (long Revision, RandomShopOffer Offer)> RandomShopsForChecks => randomShops;
    bool randomShopsLoaded;

    static string ShopDay(long nowMs) => ShopTables.Period(1, DateTimeOffset.FromUnixTimeMilliseconds(nowMs));

    /// <summary>The shop of a request's category byte when the character may use it (its race, the shops open), else null.</summary>
    RandomShopTables.Shop? RandomShopFor(byte[] plain, out int category, int length = 3)
    {
        category = plain.Length > 2 ? plain[2] : 0;
        if (plain.Length != length || !RandomShop.Current.Enabled) return null;
        return RandomShopTables.Embedded.ShopOf(category) is { OnSale: true } shop && shop.Race == (profileRace == 2 ? 2 : 1) ? shop : null;
    }

    async Task LoadRandomShopsAsync(CancellationToken ct)
    {
        if (randomShopsLoaded || Store is not { } store || CharacterId is not { } character) return;
        IReadOnlyList<(int Category, long Revision, string State)> rows = [];
        await Stored(async () => { rows = await store.RandomShopOffersAsync(character, ct); return []; });
        foreach (var (category, revision, state) in rows)
            if (RandomShopOffer.Parse(state) is { } offer) randomShops[category] = (revision, offer);
            else randomShops[category] = (revision, new RandomShopOffer(0, [], "", 0));   // unreadable: rolled again over the same row
        randomShopsLoaded = true;
    }

    /// <summary>Stores a list (with what it costs and grants) over the revision it was read at: "ok", "stale" or "failed".</summary>
    async Task<string> SaveRandomShopAsync(int category, RandomShopOffer offer, IReadOnlyList<(Guid Item, long Revision, int Count)> costs,
        IReadOnlyList<(int TemplateId, long Count)> goods, object? audit, CancellationToken ct)
    {
        var revision = randomShops.TryGetValue(category, out var held) ? held.Revision : 0;
        if (Store is not { } store || CharacterId is not { } character)
        {
            randomShops[category] = (revision + 1, offer);   // a development login keeps its lists in memory
            return costs.Count == 0 && goods.Count == 0 ? "ok" : "failed";
        }
        var outcome = "failed";
        await Stored(async () => { outcome = await store.RandomShopSaveAsync(character, category, revision, offer.ToJson(), costs, goods, audit, ct); return []; });
        if (outcome == "ok") randomShops[category] = (revision + 1, offer);
        else if (outcome == "stale") { randomShopsLoaded = false; randomShops.Clear(); }   // another login changed it: read again next time
        return outcome;
    }

    /// <summary>The shop's list now: the stored one while it lasts, else a new roll (stored at once so it does not change on reopening).</summary>
    async Task<RandomShopOffer?> CurrentRandomShopAsync(RandomShopTables.Shop shop, long now, CancellationToken ct)
    {
        await LoadRandomShopsAsync(ct);
        var rules = RandomShop.Current;
        var day = ShopDay(now);
        var previous = randomShops.TryGetValue(shop.Wire, out var held) ? held.Offer : null;
        if (previous is not null && now < previous.Until && previous.Goods.Count > 0)
            return previous.Day == day ? previous : previous with { Day = day, Refreshes = 0 };
        // The paid refresh count lasts the day (07:00 UTC, CANDIDATE), across the hourly lists.
        var offer = new RandomShopOffer(rules.NextRefresh(now), rules.Roll(shop, level, rng), day, previous?.Day == day ? previous.Refreshes : 0);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var outcome = await SaveRandomShopAsync(shop.Wire, offer, [], [], null, ct);
            if (outcome == "ok") return offer;
            if (outcome != "stale") return null;
            await LoadRandomShopsAsync(ct);
            if (randomShops.TryGetValue(shop.Wire, out held) && now < held.Offer.Until && held.Offer.Goods.Count > 0) return held.Offer;
        }
        return null;
    }

    byte[] RandomShopList(RandomShopTables.Shop shop, RandomShopOffer offer)
    {
        var rules = RandomShop.Current;
        var goods = offer.Goods.Select(s => RandomShopTables.Embedded.GoodsOf(s[0]) is { } g ? RandomShop.Wire(rules, g, s[1], s[2] != 0) : (RandomShop.WireGoods?)null)
            .OfType<RandomShop.WireGoods>().ToList();
        return RandomShop.InfoNt(shop.Wire, goods, offer.Until, offer.Refreshes);
    }

    static byte[] BlackCloudShopReply(ushort opcode, int result, int category) => PacketWriter.Opcode(opcode).U16(result).U8((byte)category).ToArray();

    /// <summary>0x5692 RandomShopInfo_RQ {category}: 0x5693 then the list 0x5691 (as official).</summary>
    async Task<List<byte[]>> BlackCloudShopInfoAsync(byte[] plain, long now, CancellationToken ct)
    {
        if (RandomShopFor(plain, out var category) is not { } shop) return [BlackCloudShopReply(BlackCloudShopInfoRs, RandomShopGoodsCondition, category)];
        if (await CurrentRandomShopAsync(shop, now, ct) is not { } offer) return [BlackCloudShopReply(BlackCloudShopInfoRs, RandomShopExpiredGoodsList, category)];
        return [BlackCloudShopReply(BlackCloudShopInfoRs, 0, category), RandomShopList(shop, offer)];
    }

    /// <summary>0x5698 RandomShopGoodsListRefreshCheck_RQ {category}: 0x5699, then the list (a new one when its time has come).</summary>
    async Task<List<byte[]>> BlackCloudShopRefreshCheckAsync(byte[] plain, long now, CancellationToken ct)
    {
        if (RandomShopFor(plain, out var category) is not { } shop) return [BlackCloudShopReply(BlackCloudShopRefreshCheckRs, RandomShopRefreshExpired, category)];
        if (await CurrentRandomShopAsync(shop, now, ct) is not { } offer) return [BlackCloudShopReply(BlackCloudShopRefreshCheckRs, RandomShopRefreshExpired, category)];
        return [BlackCloudShopReply(BlackCloudShopRefreshCheckRs, 0, category), RandomShopList(shop, offer)];
    }

    /// <summary>0x5694 RandomShopPurchase_RQ {category, goods}: Kina paid, the item to the bag, the slot marked bought; answered 0x5695.</summary>
    async Task<List<byte[]>> BlackCloudShopPurchaseAsync(byte[] plain, long now, CancellationToken ct)
    {
        var goodsId = plain.Length == 7 ? BitConverter.ToInt32(plain, 3) : 0;
        byte[] Reply(int result, int category) => PacketWriter.Opcode(BlackCloudShopPurchaseRs).U16(result).U8((byte)category).I32(goodsId).ToArray();
        if (RandomShopFor(plain, out var category, 7) is not { } shop || Store is null) return [Reply(RandomShopInvalidGoods, category)];
        await LoadRandomShopsAsync(ct);
        if (!randomShops.TryGetValue(category, out var held) || held.Offer.Goods.Count == 0) return [Reply(RandomShopInvalidGoods, category)];
        if (now >= held.Offer.Until)
        {
            // The list ran out while the window was open: refused, and the new list follows.
            List<byte[]> expired = [Reply(RandomShopExpiredGoodsList, category)];
            if (await CurrentRandomShopAsync(shop, now, ct) is { } renewed) expired.Add(RandomShopList(shop, renewed));
            return expired;
        }
        var offer = held.Offer;
        if (offer.Slot(goodsId) is not { } slot || RandomShopTables.Embedded.GoodsOf(goodsId) is not { } g) return [Reply(RandomShopInvalidGoods, category)];
        if (slot[2] != 0) return [Reply(RandomShopAlreadyPurchased, category)];
        if (g.LevelMin > 0 && level < g.LevelMin || g.LevelMax > 0 && level > g.LevelMax) return [Reply(RandomShopGoodsCondition, category)];
        var rules = RandomShop.Current;
        var price = rules.Price(g, slot[1]);
        var costs = price > 0 ? CostStacks(KinaItemId, price) : [];
        if (costs is null) return [Reply(RandomShopNotEnoughMoney, category)];
        if (!BagRoomFor([g.ItemId])) return [Reply(NotEnoughBagSlots, category)];   // GameSession.BagRoom.cs
        var bought = offer with { Goods = offer.Goods.Select(s => s[0] == goodsId ? new[] { s[0], s[1], 1 } : s).ToList() };
        var audit = new { shop = category, goods = g.Id, item = g.ItemId, discount = rules.Discounts[Math.Clamp(slot[1], 0, rules.Discounts.Count - 1)].Percent, kina = price };
        var outcome = await SaveRandomShopAsync(category, bought, costs, [(g.ItemId, 1)], audit, ct);
        if (outcome != "ok")
            return [Reply(outcome == "stale" ? RandomShopExpiredGoodsList : RandomShopNotEnoughMoney, category),
                .. await InventoryChangesAsync(ct, IncreaseRandomShop, DecreaseRandomShop)];
        // As official: the bag, the achievement count, then the reply.
        return [.. await InventoryChangesAsync(ct, IncreaseRandomShop, DecreaseRandomShop), .. Achieve("RandomShopBuy", 1, now: now),
            Reply(0, category)];
    }

    /// <summary>0x5696 RandomShopGoodsListRefresh_RQ {category}: the next paid step's Kina, a new list (same refresh time); 0x5697 then the list.</summary>
    async Task<List<byte[]>> BlackCloudShopRefreshAsync(byte[] plain, long now, CancellationToken ct)
    {
        if (RandomShopFor(plain, out var category) is not { } shop || Store is null) return [BlackCloudShopReply(BlackCloudShopRefreshRs, RandomShopRefreshExpired, category)];
        if (await CurrentRandomShopAsync(shop, now, ct) is not { } offer) return [BlackCloudShopReply(BlackCloudShopRefreshRs, RandomShopRefreshExpired, category)];
        var steps = RandomShopTables.Embedded.RefreshCosts(shop.RefreshGroup);
        var cost = steps.Count == 0 ? 0 : steps[Math.Min(offer.Refreshes, steps.Count - 1)];
        var costs = cost > 0 ? CostStacks(KinaItemId, cost) : [];
        if (costs is null) return [BlackCloudShopReply(BlackCloudShopRefreshRs, RandomShopRefreshNotEnoughMoney, category)];
        var renewed = new RandomShopOffer(offer.Until, RandomShop.Current.Roll(shop, level, rng), ShopDay(now), offer.Refreshes + 1);
        var outcome = await SaveRandomShopAsync(category, renewed, costs, [], new { shop = category, refresh = renewed.Refreshes, kina = cost }, ct);
        if (outcome != "ok")
            return [BlackCloudShopReply(BlackCloudShopRefreshRs, outcome == "stale" ? RandomShopRefreshExpired : RandomShopRefreshNotEnoughMoney, category),
                .. await InventoryChangesAsync(ct, IncreaseRandomShop, DecreaseRandomShopRefresh)];
        return [.. await InventoryChangesAsync(ct, IncreaseRandomShop, DecreaseRandomShopRefresh), BlackCloudShopReply(BlackCloudShopRefreshRs, 0, category), RandomShopList(shop, renewed)];
    }
}
