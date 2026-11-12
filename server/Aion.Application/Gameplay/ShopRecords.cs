namespace Aion.Application.Gameplay;

/// <summary>One line in game.shop_orders (migration 093): a purchase within a ShopBuy transaction.</summary>
public sealed record ShopOrderLine(string Kind, int GoodsId, int Quantity, int CurrencyItemId, long Price);

/// <summary>Persisted Special Quai Membership for an account on a world (game.account_memberships).</summary>
public sealed record StoredMembership(DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool Revoked);
