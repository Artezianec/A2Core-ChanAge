using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Trade server / broker (0x3B69–0x3B77, TradeServerInfo 0xe263). Listings persist in Postgres (051+) or FakeCharacterStore in tests.</summary>
public sealed partial class GameSession
{
    public const ushort TradeSellRq = 0xE264, TradeSellRs = 0xE265, TradeCancelSaleRq = 0xE266, TradeCancelSaleRs = 0xE267,
        TradePurchaseRq = 0xE26C, TradePurchaseRs = 0xE26D, TradeSoldNt = 0xE26E, TradeDeliveryCompleteRq = 0xE26F,
        TradeDeliveryCompleteRs = 0xE270, TradeGetSummaryRq = 0xE271, TradeGetSummaryRs = 0xE272;   // RQ CANDIDATE (RS-1)

    static TradeTables TradeConfig => TradeTables.Embedded;

    async Task<List<byte[]>> TradeRequestAsync(byte[] plain, long now, CancellationToken ct)
    {
        var op = Opcode(plain);
        return op switch
        {
            TradeSellRq when plain.Length >= 40 => await TradeSellAsync(plain, now, ct),
            TradeCancelSaleRq when plain.Length >= 20 => await TradeCancelAsync(plain, ct),
            TradePurchaseRq when plain.Length >= 20 => await TradePurchaseAsync(plain, ct),
            TradeDeliveryCompleteRq when plain.Length >= 11 => await TradeDeliveryCompleteAsync(plain, ct),
            TradeGetSummaryRq when plain.Length >= 4 => await TradeSummaryAsync(plain, ct),
            _ => [],
        };
    }

    static long ParseSaleId(string text) => long.TryParse(text, out var id) ? id : 0;

    async Task<List<byte[]>> TradeSellAsync(byte[] plain, long nowMs, CancellationToken ct)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var itemKey = (long)r.U64();
        var count = (long)r.U64();
        var moneyType = r.U8();          // ECurrencyCategory (the sender stores 0 = Kina)
        var price = (long)r.U64();
        _ = r.I64(); _ = r.I64();        // fee rate/min from the client; the server uses trade.json
        var tradeServer = r.U16();       // int32 narrowed on the wire
        byte[] Reply(int result, TradeListingRecord? listing) =>
            PacketWriter.Opcode(TradeSellRs).U16(result).String(listing?.SaleId.ToString() ?? "0").I64(itemKey).I64(count)
                .U8(moneyType).I64(price).I64(listing?.ListedAtMs ?? nowMs).I64(listing?.ExpiresAtMs ?? nowMs)
                .U16(tradeServer).ToArray();
        if (Store is not { } store || CharacterId is not { } character || !PacketSecurity.IsValidStackCount(count) || price < 1 || price > PacketSecurity.MaxTradeUnitPrice
            || !PacketSecurity.TryMultiply(price, count, out _))
            return [Reply(1, null)];
        var (result, listing) = await StoredValue(() => store.TradeListItemAsync(character, itemKey, count, price, tradeServer, nowMs, ct), ("failed", null));
        if (listing is null) return [Reply(result == "ok" ? 0 : 1, null), .. await InventoryChangesAsync(ct, 0, 0)];
        return [Reply(0, listing), .. await InventoryChangesAsync(ct, 0, 0)];
    }

    async Task<List<byte[]>> TradeCancelAsync(byte[] plain, CancellationToken ct)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var saleId = ParseSaleId(r.String());
        var template = r.I32();
        var count = (long)r.U64();
        var tradeServer = r.U16();       // int32 narrowed on the wire
        byte[] Reply(int result) => PacketWriter.Opcode(TradeCancelSaleRs).U16(result).String(saleId.ToString()).I64(0).I32(template).I64(count).U16(tradeServer).ToArray();
        if (Store is not { } store || CharacterId is not { } character || !PacketSecurity.IsValidStackCount(count)) return [Reply(1)];
        var (result, _) = await StoredValue(() => store.TradeCancelListingAsync(character, saleId, template, count, tradeServer, ct), ("failed", null));
        return [Reply(result == "ok" ? 0 : 1), .. result == "ok" ? await InventoryChangesAsync(ct, 0, 0) : []];
    }

    async Task<List<byte[]>> TradePurchaseAsync(byte[] plain, CancellationToken ct)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var saleId = ParseSaleId(r.String());
        var template = r.I32();
        var count = (long)r.U64();
        var tradeServer = r.U16();       // int32 narrowed on the wire
        byte[] Reply(int result) => PacketWriter.Opcode(TradePurchaseRs).U16(result).String(saleId.ToString()).I32(template).I64(count).U16(tradeServer).ToArray();
        if (Store is not { } store || CharacterId is not { } character || !PacketSecurity.IsValidStackCount(count)) return [Reply(1)];
        var (result, deliveryId) = await StoredValue(() => store.TradePurchaseAsync(character, saleId, template, count, tradeServer, ct), ("failed", 0L));
        var replies = new List<byte[]> { Reply(result == "ok" ? 0 : 1) };
        if (result == "ok") replies.AddRange(await InventoryChangesAsync(ct, 0, 0));
        if (deliveryId > 0) replies.Add(PacketWriter.Opcode(TradeSoldNt).String(saleId.ToString()).I32(template).I64(count).U16(tradeServer).ToArray());
        return replies;
    }

    async Task<List<byte[]>> TradeDeliveryCompleteAsync(byte[] plain, CancellationToken ct)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var deliveryId = ParseSaleId(r.String());
        r.I32();
        var tradeServer = r.U16();       // int32 narrowed on the wire
        byte[] Reply(int result) => PacketWriter.Opcode(TradeDeliveryCompleteRs).U16(result).String(deliveryId.ToString()).U16(tradeServer).ToArray();
        if (Store is not { } store || CharacterId is not { } character) return [Reply(1)];
        var ok = await StoredValue(() => store.TradeCompleteDeliveryAsync(character, deliveryId, tradeServer, ct), false);
        return [Reply(ok ? 0 : 1), .. ok ? await InventoryChangesAsync(ct, 0, 0) : []];
    }

    async Task<List<byte[]>> TradeSummaryAsync(byte[] plain, CancellationToken ct)
    {
        var tradeServer = BitConverter.ToUInt16(plain, 2);   // i16 on the wire
        if (Store is not { } store || CharacterId is not { } character)
            return [PacketWriter.Opcode(TradeGetSummaryRs).U16(1).I32(0).I32(0).Varint(0).I32(0).I32(0).Varint(0).U16(tradeServer).ToArray()];
        var summary = await StoredValue(() => store.TradeSummaryAsync(character, tradeServer, ct), new TradeSummaryRecord(0, 0, 0));
        return [PacketWriter.Opcode(TradeGetSummaryRs).U16(0).I32(summary.ActiveSales).I32(summary.ActiveSales).Varint(0)
            .I32(summary.PendingEarningsKina).I32(summary.PendingEarningsKina).Varint(0).U16(tradeServer).ToArray()];
    }
}
