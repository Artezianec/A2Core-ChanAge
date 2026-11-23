namespace Aion.Application.Gameplay;

/// <summary>One line in game.shop_orders (migration 093): a purchase within a ShopBuy transaction.</summary>
/// <param name="RequestId">When set on the first line, ties the purchase to a Quna request for idempotent retries (migration 137).</param>
public sealed record ShopOrderLine(string Kind, int GoodsId, int Quantity, int CurrencyItemId, long Price, string? RequestId = null);

/// <summary>Optional shop feature-layer context for a single ShopBuy (migration 141).</summary>
public sealed record ShopBuyContext(
    MileageRequest? MileageSpend = null,
    bool DeliverViaMail = false,
    string? QunaRequestId = null,
    long? OrderGoodsId = null,
    Guid? GiftToCharacter = null);

/// <summary>Staff refund target for an unopened shop order.</summary>
public sealed record ShopOrderRefundResult(bool Ok, string? Error = null, long QunaRefunded = 0);

/// <summary>Persisted Special Quai Membership for an account on a world (game.account_memberships).</summary>
public sealed record StoredMembership(DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool Revoked);
