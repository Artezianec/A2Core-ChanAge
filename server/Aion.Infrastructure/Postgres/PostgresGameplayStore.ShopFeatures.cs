using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Aion.World;
using Npgsql;
using NpgsqlTypes;

namespace Aion.Infrastructure.Postgres;

public sealed partial class PostgresGameplayStore
{
    static async Task<bool> DebitMileageAsync(NpgsqlConnection c, NpgsqlTransaction t, MileageRequest spend, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand("SELECT ok FROM game.mileage_apply($1,$2,$3,$4,$5,$6,$7)", c, t);
        q.Parameters.AddWithValue(spend.AccountId);
        q.Parameters.AddWithValue(spend.RequestId);
        q.Parameters.AddWithValue(-spend.Amount);
        q.Parameters.AddWithValue(spend.Reason);
        q.Parameters.Add(new NpgsqlParameter { Value = (object?)spend.WorldId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        q.Parameters.Add(new NpgsqlParameter { Value = (object?)spend.CharacterId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        q.Parameters.Add(new NpgsqlParameter
        {
            Value = spend.Details is { } details ? details.GetRawText() : DBNull.Value,
            NpgsqlDbType = NpgsqlDbType.Jsonb,
        });
        return await q.ExecuteScalarAsync(ct) is true;
    }

    async Task<bool> GrantShopRewardAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid character, string build, int template, long count,
        IItemStatRoller? itemRoller, bool viaMail, CancellationToken ct) =>
        viaMail ? await GrantShopGoodViaMailAsync(c, t, character, build, template, count, ct)
            : await GrantShopGoodAsync(c, t, character, build, template, count, itemRoller, ct);

    static async Task<bool> GrantShopGoodViaMailAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid character, string build, int template, long count, CancellationToken ct)
    {
        _ = build;
        var id = await Scalar(c, t, "INSERT INTO game.character_posts(character_id,save_tab,post_data_id,msg_params,attachments) VALUES($1,$2,$3,$4,$5) RETURNING post_id",
            ct, character, (short)2, ShopFeatureLayer.ShopMailPostDataId, "Shop purchase|" + template, Json(new[] { new object[] { (long)template, count } }));
        return id is not null;
    }

    async Task ApplyPackageBonusesAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid account, Guid character, string? build, int goodsId,
        IItemStatRoller? itemRoller, bool viaMail, CancellationToken ct)
    {
        if (build is null) return;
        var claimed = await Scalar(c, t, "SELECT first_purchase_claimed FROM game.shop_account_packages WHERE account_id=$1 AND goods_id=$2", ct, account, goodsId);
        if (claimed is true) return;
        var rule = ShopPackageCatalog.Embedded.RuleFor(goodsId, 0);
        if (rule?.FirstPurchaseBonus is not { } bonus) return;
        foreach (var part in bonus)
        {
            if (part.Length < 2) continue;
            await GrantShopRewardAsync(c, t, character, build, part[0], part[1], itemRoller, viaMail, ct);
        }
        await Execute(c, t, """
            INSERT INTO game.shop_account_packages(account_id,goods_id,tier_unlocked,first_purchase_claimed)
            VALUES($1,$2,1,true) ON CONFLICT(account_id,goods_id) DO UPDATE SET first_purchase_claimed=true,tier_unlocked=GREATEST(game.shop_account_packages.tier_unlocked,1),updated_at=now()
            """, ct, account, goodsId);
    }

    public async Task<ShopOrderRefundResult> RefundShopOrderAsync(VerifiedAccount actor, Guid account, long orderId, int refundWindowHours, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        await using var q = new NpgsqlCommand("""
            SELECT o.order_id,o.request_id,o.status,o.ordered_at,o.price,o.currency_item,o.character_id
            FROM game.shop_orders o JOIN game.characters ch ON ch.character_id=o.character_id
            WHERE o.order_id=$1 AND o.account_id=$2 AND ch.account_id=$2
            FOR UPDATE OF o
            """, c, t);
        q.Parameters.AddWithValue(orderId);
        q.Parameters.AddWithValue(account);
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return new ShopOrderRefundResult(false, "not_found");
        var requestId = r.IsDBNull(1) ? null : r.GetString(1);
        var status = r.GetString(2);
        var orderedAt = r.GetFieldValue<DateTimeOffset>(3);
        var price = r.GetInt64(4);
        var currency = r.GetInt32(5);
        await r.CloseAsync();
        if (status == "refunded") return new ShopOrderRefundResult(false, "already_refunded");
        if (status != "pending") return new ShopOrderRefundResult(false, "not_refundable");
        if (orderedAt.AddHours(refundWindowHours) < DateTimeOffset.UtcNow) return new ShopOrderRefundResult(false, "window_expired");
        if (currency != PlatformGoodsTables.Embedded.QunaItemId || price <= 0 || string.IsNullOrWhiteSpace(requestId))
            return new ShopOrderRefundResult(false, "not_paid_quna");
        await using var refund = new NpgsqlCommand("SELECT ok,balance,entry_id,error,repeated FROM game.quna_staff_refund($1,$2,$3)", c, t);
        refund.Parameters.AddWithValue(actor.AccountId);
        refund.Parameters.AddWithValue(account);
        refund.Parameters.AddWithValue(requestId);
        var ok = false;
        await using (var rr = await refund.ExecuteReaderAsync(ct))
        {
            if (await rr.ReadAsync(ct)) ok = rr.GetBoolean(0);
        }
        if (!ok) return new ShopOrderRefundResult(false, "refund_failed");
        await Execute(c, t, "UPDATE game.shop_orders SET status='refunded',refunded_at=now(),refund_actor=$2 WHERE order_id=$1", ct, orderId, actor.AccountId);
        await Execute(c, t, "INSERT INTO audit.events(action,actor_id,details) VALUES('shop_order_refund',$1,$2)", ct, actor.AccountId,
            Json(new { orderId, account, requestId }));
        await t.CommitAsync(ct);
        return new ShopOrderRefundResult(true, QunaRefunded: price);
    }

    public async Task<bool> LogShopGiftAsync(Guid fromCharacter, Guid toCharacter, int goodsId, int quantity, string? qunaRequestId, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await Execute(c, null, "INSERT INTO game.shop_gift_log(from_character_id,to_character_id,goods_id,quantity,quna_request_id) VALUES($1,$2,$3,$4,$5)",
            ct, fromCharacter, toCharacter, goodsId, quantity, (object?)qunaRequestId ?? DBNull.Value);
        return true;
    }
}