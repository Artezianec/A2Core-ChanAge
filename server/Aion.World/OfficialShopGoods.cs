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

    sealed record Entry(ShopTables.GoodsRow Row, IReadOnlyList<(int ItemId, int Count)> Parts);

    OfficialShopGoods(Dictionary<int, Entry> rows) => this.rows = rows;

    static readonly Lazy<OfficialShopGoods> embedded = new(() =>
    {
        using var stream = typeof(OfficialShopGoods).Assembly.GetManifestResourceStream("official_shop.json")
            ?? throw new InvalidOperationException("Missing embedded official_shop.json");
        using var doc = JsonDocument.Parse(stream);
        var map = new Dictionary<int, Entry>();
        if (!doc.RootElement.TryGetProperty("goods", out var goods)) return new OfficialShopGoods(map);
        foreach (var g in goods.EnumerateArray())
        {
            if (!TryInt(g, "goods_id", out var goodsId) || map.ContainsKey(goodsId)) continue;
            if (ToEntry(goodsId, g) is { } entry) map[goodsId] = entry;
        }
        return new OfficialShopGoods(map);
    });

    public static OfficialShopGoods Embedded => embedded.Value;

    public ShopTables.GoodsRow? ToShopGoods(int goodsId) => rows.GetValueOrDefault(goodsId)?.Row;

    public IReadOnlyList<(int ItemId, int Count)> Parts(int goodsId) =>
        rows.GetValueOrDefault(goodsId)?.Parts ?? [];

    public static ShopTables.GoodsRow? ToShopGoods(JsonElement g)
    {
        if (!TryInt(g, "goods_id", out var goodsId)) goodsId = 0;
        return ToEntry(goodsId, g)?.Row;
    }

    static Entry? ToEntry(int goodsId, JsonElement g)
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
        return new Entry(new ShopTables.GoodsRow(first.ItemId, first.Count, policy.Price, 0, id, 0, 0, 0), parts);
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
