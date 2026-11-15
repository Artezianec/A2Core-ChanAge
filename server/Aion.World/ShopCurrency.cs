using System.Globalization;

namespace Aion.World;

/// <summary>
/// Private-server price policy for the BM shop: Cash and Mileage are charged as Quna,
/// Kina and Quna stay, and Quna top-up SKUs are not sold.
/// </summary>
public static class ShopCurrency
{
    public const int CashGroup = 1, QunaGroup = 145, MileageGroup = 158, KinaGroup = 159, Group261 = 261;
    public const int MileageMultiplier = 1;

    static readonly HashSet<string> BlockedCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Daily_RechargeQuna", "D_Daily_RechargeQuna",
    };

    public static bool IsBlockedCategory(string? category) =>
        !string.IsNullOrEmpty(category) && BlockedCategories.Contains(category);

    public static bool IsBlocked(int? currencyGroup, IEnumerable<int> itemIds, IEnumerable<string?> categories)
    {
        if (currencyGroup == Group261) return true;
        if (itemIds.Any(id => id == PlatformGoodsTables.Embedded.QunaItemId)) return true;
        return categories.Any(IsBlockedCategory);
    }

    /// <summary>
    /// Maps a catalog price to a bag currency group. A platform_goods numeric price for Cash,
    /// GameCash or Mileage wins (membership 100024 is Cash 25000 there).
    /// </summary>
    public static (int Group, long Price)? Map(int goodsId, int? group, long price, long? platformPrice)
    {
        if (platformPrice is > 0) return (QunaGroup, platformPrice.Value);
        if (group is null) return null;
        return group switch
        {
            CashGroup => (QunaGroup, price),
            MileageGroup => (QunaGroup, price * MileageMultiplier),
            QunaGroup or KinaGroup => (group.Value, price),
            _ => null,
        };
    }

    public static long? PlatformQunaPrice(int goodsId)
    {
        if (PlatformGoodsTables.Embedded.Goods(goodsId) is not { Price: > 0 } row) return null;
        return row.Currency is "GameCash" or "Cash" or "Mileage" ? row.Price : null;
    }

    public static int? CurrencyItemId(string? currency, int itemId = 0, string? category = null)
    {
        if (itemId == GameSession.QuaiMembershipItemId) return null;
        if (itemId == PlatformGoodsTables.Embedded.QunaItemId || IsBlockedCategory(category)) return null;
        return currency switch
        {
            "GameCash" or "Cash" or "Mileage" => PlatformGoodsTables.Embedded.QunaItemId,
            "GoldCombined" => PlatformGoodsTables.Embedded.KinaItemId,
            _ => null,
        };
    }
}
