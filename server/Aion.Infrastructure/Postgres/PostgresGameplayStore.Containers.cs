using Npgsql;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;

namespace Aion.Infrastructure.Postgres;

public sealed partial class PostgresGameplayStore
{
    public async Task<string> TransferContainerItemsAsync(VerifiedAccount account, Guid character, string fromLocation, string toLocation,
        IReadOnlyList<(long WireKey, long Count)> transfers, int maxDestinationSlots, CancellationToken ct = default)
    {
        if (fromLocation is not ("bag" or "storage" or "account_storage") || toLocation is not ("bag" or "storage" or "account_storage")
            || fromLocation == toLocation || transfers.Count == 0 || maxDestinationSlots < 1)
            throw new GameplayException("invalid_transfer");
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return "none";
        var accountId = account.AccountId;
        var usedDestSlots = await OccupiedSlotsAsync(c, t, character, accountId, toLocation, ct);
        foreach (var (wireKey, count) in transfers)
        {
            if (count < 1) return "failed";
            if (!await TransferOneAsync(c, t, character, accountId, wireKey, count, fromLocation, toLocation, maxDestinationSlots, usedDestSlots, ct))
                return "failed";
        }
        await t.CommitAsync(ct);
        return "ok";
    }

    static async Task<HashSet<int>> OccupiedSlotsAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid character, Guid accountId, string location, CancellationToken ct)
    {
        var slots = new HashSet<int>();
        var sql = location == "account_storage"
            ? "SELECT slot FROM game.item_instances WHERE location='account_storage' AND account_storage_owner=$1 AND slot IS NOT NULL FOR UPDATE"
            : "SELECT slot FROM game.item_instances WHERE character_id=$1 AND location=$2 AND slot IS NOT NULL FOR UPDATE";
        await using var q = location == "account_storage" ? Query(c, t, sql, accountId) : Query(c, t, sql, character, location);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) slots.Add(r.GetInt32(0));
        return slots;
    }

    static int? NextFreeSlot(HashSet<int> used, int maxSlots)
    {
        for (var s = 0; s < maxSlots; s++)
            if (!used.Contains(s)) return s;
        return null;
    }

    async Task<bool> TransferOneAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid character, Guid accountId, long wireKey, long count,
        string fromLocation, string toLocation, int maxDestSlots, HashSet<int> usedDestSlots, CancellationToken ct)
    {
        var lockSql = fromLocation == "account_storage"
            ? """
              SELECT instance_id,revision,template_id,quantity,build_id,upgrades::text,rolled_stats::text
              FROM game.item_instances
              WHERE wire_id=$1 AND location='account_storage' AND account_storage_owner=$2
                AND NOT (upgrades ? 'soul_bind') FOR UPDATE
              """
            : """
              SELECT instance_id,revision,template_id,quantity,build_id,upgrades::text,rolled_stats::text
              FROM game.item_instances
              WHERE wire_id=$1 AND character_id=$2 AND location=$3
                AND NOT (upgrades ? 'soul_bind') FOR UPDATE
              """;
        await using var q = fromLocation == "account_storage"
            ? Query(c, t, lockSql, wireKey, accountId)
            : Query(c, t, lockSql, wireKey, character, fromLocation);
        Guid instanceId;
        long revision;
        int template;
        long quantity;
        string build;
        string upgrades;
        string rolled;
        await using (var r = await q.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return false;
            instanceId = r.GetGuid(0);
            revision = r.GetInt64(1);
            template = r.GetInt32(2);
            quantity = r.GetInt64(3);
            build = r.GetString(4);
            upgrades = r.GetString(5);
            rolled = r.GetString(6);
        }
        if (quantity < count) return false;

        var movable = instanceId;
        if (count < quantity)
        {
            var splitId = Guid.NewGuid();
            if (fromLocation == "account_storage")
                await Execute(c, t, """
                    INSERT INTO game.item_instances(instance_id,character_id,build_id,template_id,quantity,location,revision,upgrades,rolled_stats,account_storage_owner)
                    VALUES($1,$2,$3,$4,$5,$6,$7,$8::jsonb,$9::jsonb,$10)
                    """, ct, splitId, character, build, template, count, fromLocation, 1L, upgrades, rolled, accountId);
            else
                await Execute(c, t, """
                    INSERT INTO game.item_instances(instance_id,character_id,build_id,template_id,quantity,location,revision,upgrades,rolled_stats)
                    VALUES($1,$2,$3,$4,$5,$6,$7,$8::jsonb,$9::jsonb)
                    """, ct, splitId, character, build, template, count, fromLocation, 1L, upgrades, rolled);
            await Execute(c, t, "UPDATE game.item_instances SET quantity=quantity-$2,revision=revision+1 WHERE instance_id=$1", ct, instanceId, count);
            movable = splitId;
            quantity = count;
        }

        if (toLocation == "bag" && upgrades == "{}" && rolled == "{}"
            && await Scalar(c, t, "SELECT game.item_stack_max($1,$2)", ct, build, template) is int maxStack and not 1)
        {
            await using var mergeQ = Query(c, t, """
                SELECT instance_id,quantity FROM game.item_instances
                WHERE character_id=$1 AND location='bag' AND build_id=$2 AND template_id=$3
                  AND upgrades='{}'::jsonb AND rolled_stats='{}'::jsonb
                  AND ($4=0 OR quantity<$4)
                ORDER BY quantity DESC LIMIT 1 FOR UPDATE
                """, character, build, template, maxStack);
            await using var mergeR = await mergeQ.ExecuteReaderAsync(ct);
            if (await mergeR.ReadAsync(ct))
            {
                var target = mergeR.GetGuid(0);
                var have = mergeR.GetInt64(1);
                var room = maxStack == 0 ? quantity : Math.Min(quantity, maxStack - have);
                if (room <= 0) { /* fall through to slot */ }
                else
                {
                    await Execute(c, t, "UPDATE game.item_instances SET quantity=quantity+$2,revision=revision+1 WHERE instance_id=$1", ct, target, room);
                    if (room == quantity)
                    {
                        await Execute(c, t, "DELETE FROM game.item_instances WHERE instance_id=$1", ct, movable);
                        return true;
                    }
                    await Execute(c, t, "UPDATE game.item_instances SET quantity=quantity-$2,revision=revision+1 WHERE instance_id=$1", ct, movable, room);
                    quantity -= room;
                    if (quantity <= 0) return true;
                }
            }
        }

        var slot = NextFreeSlot(usedDestSlots, maxDestSlots);
        if (slot is null) return false;
        usedDestSlots.Add(slot.Value);
        if (toLocation == "account_storage")
            await Execute(c, t, """
                UPDATE game.item_instances SET location=$2,slot=$3,revision=revision+1,account_storage_owner=$4
                WHERE instance_id=$1
                """, ct, movable, toLocation, slot.Value, accountId);
        else if (toLocation == "storage")
            await Execute(c, t, """
                UPDATE game.item_instances SET location=$2,slot=$3,revision=revision+1,account_storage_owner=NULL,character_id=$4
                WHERE instance_id=$1
                """, ct, movable, toLocation, slot.Value, character);
        else
            await Execute(c, t, """
                UPDATE game.item_instances SET location=$2,slot=$3,revision=revision+1,account_storage_owner=NULL
                WHERE instance_id=$1
                """, ct, movable, toLocation, slot.Value);
        _ = revision;
        return true;
    }
}