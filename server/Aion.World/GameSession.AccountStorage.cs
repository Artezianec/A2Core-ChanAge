using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>NPC warehouse ItemStore / ItemTakeOut (opcode run 0x5642–0x5645; usmap 0x3784–0x3787).</summary>
public sealed partial class GameSession
{
    public const ushort ItemStoreRq = 0x5642, ItemStoreRs = 0x5643, ItemTakeOutRq = 0x5644, ItemTakeOutRs = 0x5645;

    static string? ContainerToLocation(int storeContainerType) => storeContainerType switch
    {
        InventoryItem.Storage => "storage",
        InventoryItem.AccountStorage or CharacterStorageContainer => "account_storage",
        _ => null,
    };

    static byte[] ItemStoreReply(ushort opcode, int result, int npc, IReadOnlyList<(long Key, long Count)> items, int storeContainerType)
    {
        var w = PacketWriter.Opcode(opcode).I32(result).I32(npc).Varint(items.Count);
        foreach (var (key, count) in items) w.I64(key).I64(count);
        return w.I32(storeContainerType).ToArray();
    }

    static bool TryParseStorePacket(byte[] plain, out int npc, out List<(long Key, long Count)> items, out int storeContainerType)
    {
        npc = 0;
        storeContainerType = 0;
        items = [];
        try
        {
            var r = new PacketReader(plain) { Offset = 2 };
            npc = (int)r.I32();
            items = ReadItemMap(r);
            storeContainerType = (int)r.I32();
            return true;
        }
        catch (ProtocolException)
        {
            return false;
        }
    }

    static bool CanTransferToWarehouse(ItemState row) => SoulBindCount(row) == 0;

    int DestinationSlotLimit(string location) => location switch
    {
        "storage" => containerSlotExtends.StorageSlotLimit,
        "account_storage" => containerSlotExtends.AccountStorageSlotLimit,
        "bag" => containerSlotExtends.BagSlotLimit,
        _ => 0,
    };

    async Task<List<byte[]>> ItemStoreAsync(byte[] plain, CancellationToken ct)
    {
        if (!TryParseStorePacket(plain, out var npc, out var items, out var storeContainerType)
            || ContainerToLocation(storeContainerType) is not { } toLocation
            || Store is not { } store || CharacterId is not { } character || items.Count == 0)
            return [ItemStoreReply(ItemStoreRs, 1, npc, items, storeContainerType)];

        var transfers = new List<(long WireKey, long Count)>();
        foreach (var (key, count) in items)
        {
            if (count < 1 || !storedItems.TryGetValue(key, out var row) || row.Location != "bag" || count > row.Quantity
                || !CanTransferToWarehouse(row))
                return [ItemStoreReply(ItemStoreRs, 1, npc, items, storeContainerType)];
            transfers.Add((key, count));
        }

        var outcome = "none";
        await Stored(async () =>
        {
            outcome = await store.TransferContainerItemsAsync(character, "bag", toLocation, transfers, DestinationSlotLimit(toLocation), ct);
            return outcome == "ok" ? [[]] : [];
        });
        if (outcome != "ok")
            return [ItemStoreReply(ItemStoreRs, 1, npc, items, storeContainerType), .. await InventoryChangesAsync(ct, 0, 0)];
        return
        [
            ItemStoreReply(ItemStoreRs, 0, npc, items, storeContainerType),
            .. await InventoryChangesAsync(ct, 0, 0),
            .. toLocation is "storage" or "account_storage" ? Achieve("StorageStore") : [],
        ];
    }

    async Task<List<byte[]>> ItemTakeOutAsync(byte[] plain, CancellationToken ct)
    {
        if (!TryParseStorePacket(plain, out var npc, out var items, out var storeContainerType)
            || ContainerToLocation(storeContainerType) is not { } fromLocation
            || Store is not { } store || CharacterId is not { } character || items.Count == 0)
            return [ItemStoreReply(ItemTakeOutRs, 1, npc, items, storeContainerType)];

        var transfers = new List<(long WireKey, long Count)>();
        foreach (var (key, count) in items)
        {
            if (count < 1 || !storedItems.TryGetValue(key, out var row) || row.Location != fromLocation || count > row.Quantity
                || !CanTransferToWarehouse(row))
                return [ItemStoreReply(ItemTakeOutRs, 1, npc, items, storeContainerType)];
            transfers.Add((key, count));
        }

        var outcome = "none";
        await Stored(async () =>
        {
            outcome = await store.TransferContainerItemsAsync(character, fromLocation, "bag", transfers, DestinationSlotLimit("bag"), ct);
            return outcome == "ok" ? [[]] : [];
        });
        if (outcome != "ok")
            return [ItemStoreReply(ItemTakeOutRs, 1, npc, items, storeContainerType), .. await InventoryChangesAsync(ct, 0, 0)];
        return [ItemStoreReply(ItemTakeOutRs, 0, npc, items, storeContainerType), .. await InventoryChangesAsync(ct, 0, 0)];
    }
}