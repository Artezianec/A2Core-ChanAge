using Aion.Application.Authentication;
using Aion.Application.Gameplay;
using Aion.Core.Security;
using Aion.World;

namespace Aion.Host.Authentication;

/// <summary>Staff tools for the shop feature layer: order refunds and gift validation.</summary>
public static class StaffShopEndpoints
{
    public sealed record ShopRefundInput(long OrderId);
    public sealed record ShopGiftInput(string RecipientName, int GoodsId, int Quantity);

    public static void MapStaffShopEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/admin/shop").RequireRateLimiting("admin");

        admin.MapPost("/accounts/{id:guid}/orders/refund", async (Guid id, ShopRefundInput input, HttpContext ctx, IAccountAuthentication auth,
            IGameplayStore game, CancellationToken ct) =>
        {
            if (await ActorAsync(ctx, auth, game, StaffPermission.ManageQuna, ct) is not { } outcome) return Results.Unauthorized();
            if (outcome.Denied) return Results.StatusCode(403);
            if (input.OrderId <= 0) return Results.BadRequest(new { error = "invalid_order" });
            var hours = GameSession.HostShopFeatures.RefundWindowHours;
            var result = await game.RefundShopOrderAsync(outcome.Actor, id, input.OrderId, hours, ct);
            return result.Ok ? Results.Ok(new { qunaRefunded = result.QunaRefunded }) : Results.BadRequest(new { error = result.Error });
        });

        admin.MapPost("/gift", async (ShopGiftInput input, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, CancellationToken ct) =>
        {
            if (await ActorAsync(ctx, auth, game, StaffPermission.ManageQuna, ct) is not { } outcome) return Results.Unauthorized();
            if (outcome.Denied) return Results.StatusCode(403);
            if (input.Quantity < 1 || input.GoodsId < 1 || string.IsNullOrWhiteSpace(input.RecipientName))
                return Results.BadRequest(new { error = "invalid_gift" });
            if (!ShopFeatureLayer.IsGiftable(input.GoodsId)) return Results.BadRequest(new { error = "not_giftable" });
            var to = await game.CharacterByNameAsync(outcome.Actor, input.RecipientName.Trim(), ct);
            if (to is null) return Results.NotFound(new { error = "unknown_character" });
            return Results.Accepted(value: new { recipient = to, goodsId = input.GoodsId, quantity = input.Quantity,
                note = "Delivery uses in-game ShopGiftPurchase (wire 0x569A CANDIDATE) or builder commands." });
        });
    }

    sealed record Outcome(Aion.Core.Accounts.VerifiedAccount Actor, bool Denied);

    static async Task<Outcome?> ActorAsync(HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, StaffPermission permission, CancellationToken ct)
    {
        var account = await auth.AuthenticateAsync(Bearer(ctx), ct);
        if (account is null) return null;
        try { return new Outcome(account, !StaffAccess.Can((await game.AdminProfileAsync(account, ct)).Roles, permission)); }
        catch (GameplayException) { return new Outcome(account, true); }
    }

    static string Bearer(HttpContext context) =>
        context.Request.Headers.Authorization.ToString() is var value && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..] : "";
}