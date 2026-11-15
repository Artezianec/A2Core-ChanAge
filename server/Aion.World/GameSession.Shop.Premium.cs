namespace Aion.World;

public sealed partial class GameSession
{
    static ShopOptions hostDefaultShopOptions = new();

    internal static ShopOptions HostDefaultShopOptions => hostDefaultShopOptions;

    public static void ConfigureShopOptions(ShopOptions options) => hostDefaultShopOptions = options;

    static bool IsPremiumCurrency(int currencyItemId)
    {
        if (currencyItemId == PlatformGoodsTables.Embedded.QunaItemId) return true;
        return currencyItemId is 930100013 or 930100014 or 930100015 or 930100021;
    }

    (int Currency, long Price)? ApplyPremiumPrice(int currency, long price)
    {
        if (!IsPremiumCurrency(currency)) return (currency, price);
        return shopOptions.PremiumMode switch
        {
            ShopPremiumMode.Disabled => null,
            ShopPremiumMode.Free => (currency, 0),
            ShopPremiumMode.Kina => (KinaItemId, price),
            _ => (PlatformGoodsTables.Embedded.QunaItemId, price),
        };
    }

    static ShopTables.GoodsRow? ResolveExportedGoods(int platformOrGoodsId)
    {
        var gameId = PlatformGoodsTables.Embedded.ResolveGameGoodsId(platformOrGoodsId) ?? platformOrGoodsId;
        // Quna / Kina BM SKUs are omitted from shops.json at export; fall back to the platform catalog.
        return Shops.RowByGoodsId(gameId)
            ?? PlatformGoodsTables.Embedded.ToShopGoods(platformOrGoodsId)
            ?? OfficialShopGoods.Embedded.ToShopGoods(platformOrGoodsId)
            ?? OfficialShopGoods.Embedded.ToShopGoods(gameId);
    }
}
