using System.Globalization;
using System.Text.Json;

namespace Aion.World;

/// <summary>
/// Official BM HTTP catalog as shop goods rows, for SKUs the client lists in goods/v2.0/goods
/// that are missing from shops.json and platform_goods.json. Reads the served catalog
/// (tools/data/official_shop.served.json, embedded as official_shop.json).
/// Cash and Mileage prices are Quna; Kina stays Kina. Membership stays on the dedicated path.
/// </summary>
public sealed class OfficialShopGoods
{
    public const int QunaCurrencyGroupId = ShopCurrency.QunaGroup, KinaCurrencyGroupId = ShopCurrency.KinaGroup,
        CashCurrencyGroupId = ShopCurrency.CashGroup, MileageCurrencyGroupId = ShopCurrency.MileageGroup;

    readonly Dictionary<int, Entry> rows;
    readonly Dictionary<int, Meta> meta;

    sealed record Entry(ShopTables.GoodsRow Row, IReadOnlyList<(int ItemId, int Count)> Parts);
    sealed record PurchaseLimit(int Limit, int LimitTarget, int LimitPeriod);
    sealed record Meta(bool Giftable, bool AccountBound);

    OfficialShopGoods(Dictionary<int, Entry> rows, Dictionary<int, Meta> meta)
    {
        this.rows = rows;
        this.meta = meta;
    }

    static readonly Lazy<OfficialShopGoods> embedded = new(() =>
    {
        using var stream = typeof(OfficialShopGoods).Assembly.GetManifestResourceStream("official_shop.json")
            ?? throw new InvalidOperationException("Missing embedded official_shop.json");
        using var doc = JsonDocument.Parse(stream);
        var quotas = ReadQuotaLimits(doc.RootElement);
        var map = new Dictionary<int, Entry>();
        var flags = new Dictionary<int, Meta>();
        foreach (var g in EnumerateGoods(doc.RootElement))
        {
            if (!TryInt(g, "goods_id", out var goodsId)) continue;
            flags[goodsId] = ReadMeta(g);
            if (map.ContainsKey(goodsId)) continue;
            if (ToEntry(goodsId, g, quotas.GetValueOrDefault(goodsId)) is { } entry) map[goodsId] = entry;
        }
        return new OfficialShopGoods(map, flags);
    });

    static IEnumerable<JsonElement> EnumerateGoods(JsonElement root)
    {
        if (root.TryGetProperty("goods", out var goods))
            foreach (var g in goods.EnumerateArray()) yield return g;
        if (root.TryGetProperty("me_goods", out var me))
            foreach (var g in me.EnumerateArray()) yield return g;
    }

    /// <summary>Purchase limits from served catalog quotas (limit_list on goods/v2.0/me/goods/policy).</summary>
    public static bool TryPurchaseLimit(int goodsId, out int limit, out int limitTarget, out int limitPeriod)
    {
        limit = limitTarget = limitPeriod = 0;
        if (!Embedded.rows.TryGetValue(goodsId, out var entry)) return false;
        var row = entry.Row;
        if (row.Limit <= 0) return false;
        limit = row.Limit;
        limitTarget = row.LimitTarget;
        limitPeriod = row.LimitPeriod;
        return true;
    }

    static Dictionary<int, PurchaseLimit> ReadQuotaLimits(JsonElement root)
    {
        var map = new Dictionary<int, PurchaseLimit>();
        if (!root.TryGetProperty("quotas", out var quotas) || quotas.ValueKind != JsonValueKind.Array) return map;
        foreach (var quota in quotas.EnumerateArray())
        {
            if (!TryInt(quota, "goods_id", out var goodsId)) continue;
            if (ParseQuotaLimit(quota) is not { } limit) continue;
            map[goodsId] = limit;
        }
        return map;
    }

    /// <summary>
    /// Maps BM quota limit_list rows to GameShopGoods-style limits (see tools/shop_data.py).
    /// limit_user_type 4 = account (ServerAccount), 6 = character; duration 4/5 weekly, 6 monthly, 9 lifetime.
    /// </summary>
    static PurchaseLimit? ParseQuotaLimit(JsonElement quota)
    {
        if (!quota.TryGetProperty("limit_list", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            return null;
        var entry = list[0];
        if (!TryInt(entry, "limit_quantity", out var limit) || limit <= 0) return null;
        var target = TryInt(entry, "limit_user_type", out var user) && user == 4 ? 2 : 1;
        var period = 0;
        if (TryInt(entry, "limit_duration_unit_type", out var unit))
            period = unit switch { 4 or 5 => 2, 6 => 3, _ => 0 };
        return new PurchaseLimit(limit, target, period);
    }

    public static OfficialShopGoods Embedded => embedded.Value;

    public ShopTables.GoodsRow? ToShopGoods(int goodsId) => rows.GetValueOrDefault(goodsId)?.Row;

    public IReadOnlyList<(int ItemId, int Count)> Parts(int goodsId) =>
        rows.GetValueOrDefault(goodsId)?.Parts ?? [];

    public bool IsGiftable(int goodsId) => meta.GetValueOrDefault(goodsId)?.Giftable ?? false;

    public bool AccountBoundDelivery(int goodsId) => meta.GetValueOrDefault(goodsId)?.AccountBound ?? false;

    static Meta ReadMeta(JsonElement g)
    {
        var giftable = false;
        if (g.TryGetProperty("goods_data", out var data) && data.ValueKind == JsonValueKind.String)
            giftable = (data.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(p => p.Equals("cp:1", StringComparison.Ordinal));
        var accountBound = TryInt(g, "inventory_type", out var inv) && inv == 1;
        return new Meta(giftable, accountBound);
    }

    public static ShopTables.GoodsRow? ToShopGoods(JsonElement g)
    {
        if (!TryInt(g, "goods_id", out var goodsId)) goodsId = 0;
        return ToEntry(goodsId, g, null)?.Row;
    }

    static Entry? ToEntry(int goodsId, JsonElement g, PurchaseLimit? quotaLimit)
    {
        var parts = ReadParts(g);
        if (parts.Count == 0) return null;
        if (parts.Any(p => p.ItemId == GameSession.QuaiMembershipItemId)) return null;
        if (!TryPrice(goodsId, g, out var group, out var price) || price < 0) return null;
        if (ShopCurrency.IsBlocked(group, parts.Select(p => p.ItemId), ReadCategories(g))) return null;
        var mapped = ShopCurrency.Map(goodsId, group, price, ShopCurrency.PlatformQunaPrice(goodsId));
        if (mapped is not { } policy) return null;
        var currency = policy.Group switch
        {
            ShopCurrency.QunaGroup => PlatformGoodsTables.Embedded.QunaItemId,
            ShopCurrency.KinaGroup => PlatformGoodsTables.Embedded.KinaItemId,
            _ => (int?)null,
        };
        if (currency is not { } id) return null;
        var first = parts[0];
        var lim = quotaLimit ?? new PurchaseLimit(0, 0, 0);
        return new Entry(new ShopTables.GoodsRow(first.ItemId, first.Count, policy.Price, 0, id, lim.Limit, lim.LimitTarget, lim.LimitPeriod), parts);
    }

    static List<(int ItemId, int Count)> ReadParts(JsonElement g)
    {
        var parts = new List<(int, int)>();
        if (!g.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            if (TryInt(g, "item_id", out var fallback) && fallback > 0) parts.Add((fallback, 1));
            return parts;
        }
        foreach (var it in items.EnumerateArray())
        {
            // Official item_id is a platform catalog id, not a game template. Only game_item_key is grantable.
            if (!TryInt(it, "game_item_key", out var itemId) || itemId <= 0) continue;
            var count = 1;
            if (TryInt(it, "item_quantity", out var qty) || TryInt(it, "item_count", out qty)) count = Math.Max(1, qty);
            parts.Add((itemId, count));
        }
        return parts;
    }

    static IEnumerable<string?> ReadCategories(JsonElement g)
    {
        if (!g.TryGetProperty("goods_data", out var data) || data.ValueKind != JsonValueKind.String)
            yield break;
        foreach (var part in (data.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("catl:", StringComparison.Ordinal) || part.StartsWith("catd:", StringComparison.Ordinal))
                yield return part[5..];
        }
    }

    static bool TryPrice(int goodsId, JsonElement g, out int group, out long price)
    {
        group = 0; price = 0;
        if (!g.TryGetProperty("goods_basic_price_policies", out var policies) || policies.ValueKind != JsonValueKind.Array || policies.GetArrayLength() == 0)
            return false;
        var p = policies[0];
        if (!TryInt(p, "currency_group_id", out group)) return false;
        if (p.TryGetProperty("goods_basic_price", out var raw) && raw.ValueKind == JsonValueKind.String
            && decimal.TryParse(raw.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var dec))
        {
            price = (long)dec;
            return true;
        }
        return false;
    }

    static bool TryInt(JsonElement e, string name, out int value)
    {
        value = 0;
        if (!e.TryGetProperty(name, out var p)) return false;
        if (p.ValueKind == JsonValueKind.Number) return p.TryGetInt32(out value);
        return p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}