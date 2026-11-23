namespace Aion.World;

/// <summary>Catalog helpers for the shop feature layer (Mileage tabs, giftable SKUs, account-bound delivery).</summary>
public static class ShopFeatureLayer
{
    /// <summary>Sentinel currency item id: purchases debit the Mileage wallet, not a bag stack.</summary>
    public const int MileageChargeItemId = 535300158;

    public const int ShopMailPostDataId = 107;

    public static bool IsMileageCategory(string? category) =>
        !string.IsNullOrEmpty(category) && (category.Contains("Mileage_Usable", StringComparison.OrdinalIgnoreCase)
                                            || category.Contains("Mileage_Special", StringComparison.OrdinalIgnoreCase));

    public static string? CategoryOf(int goodsId) =>
        PlatformGoodsTables.Embedded.Goods(goodsId)?.CategoryId;

    /// <summary>goods_data flag cp:1 (giftable to another character) when present in official served catalog.</summary>
    public static bool IsGiftable(int goodsId) =>
        OfficialShopGoods.Embedded.IsGiftable(goodsId);

    /// <summary>inventory_type 1 in the platform catalog: account-bound delivery via mail when the bag is full.</summary>
    public static bool PrefersMailDelivery(int goodsId) =>
        OfficialShopGoods.Embedded.AccountBoundDelivery(goodsId);

    public static long MileageListPrice(int goodsId) =>
        PlatformGoodsTables.Embedded.Goods(goodsId)?.Price ?? 0;

    /// <summary>When enabled, remap a mileage-tab SKU to charge the Mileage wallet at its catalog Mileage price.</summary>
    public static ShopTables.GoodsRow? ApplyMileageWallet(ShopTables.GoodsRow? row, int goodsId, ShopFeatureOptions features)
    {
        if (row is null || !features.UseMileageCurrency) return row;
        var category = CategoryOf(goodsId);
        if (!IsMileageCategory(category)) return row;
        var price = MileageListPrice(goodsId);
        if (price <= 0) return row;
        return row with { Currency = MileageChargeItemId, Price = price };
    }
}