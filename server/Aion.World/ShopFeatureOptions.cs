namespace Aion.World;

/// <summary>BM shop feature-layer toggles (docs/SHOP_FEATURES.md); configured on the host via Aion:Shop:*.</summary>
public sealed class ShopFeatureOptions
{
    /// <summary>Charge Mileage_Usable / Mileage_Special SKUs from the account Mileage wallet instead of converting to Quna.</summary>
    public bool UseMileageCurrency { get; init; } = true;

    /// <summary>Fraction of paid Quna spends credited as Mileage (OURS default 10%; no official capture found).</summary>
    public int MileageEarnPercent { get; init; } = 10;

  /// <summary>When the bag is full or delivery is account-bound, send shop rewards via mail instead of failing.</summary>
    public bool MailDeliveryEnabled { get; init; } = true;

    /// <summary>Staff may refund unopened shop orders within this many hours (paid Quna only).</summary>
    public int RefundWindowHours { get; init; } = 168;

    public bool GiftingEnabled { get; init; } = true;

    public bool PackageBonusesEnabled { get; init; } = true;
}