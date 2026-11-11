using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

public readonly record struct ContainerSlotExtendView(int InventoryByItem, int InventoryByGold, int Storage, int StorageByItem, int CharacterStorageByGold, int CharacterStorageByItem)
{
    public static ContainerSlotExtendView From(ContainerSlotExtendCounts c) => new(c.InventoryByItem, c.InventoryByGold, c.Storage, c.StorageByItem, c.CharacterStorageByGold, c.CharacterStorageByItem);
    public int[] ToWireCounts() => [InventoryByItem, InventoryByGold, Storage, StorageByItem, CharacterStorageByGold, CharacterStorageByItem];
    public int BagSlotLimit => GameSession.BaseBagSlotCount + InventoryByGold * GameSession.BagSlotsPerRow + InventoryByItem * GameSession.BagSlotsPerRow;
    public int StorageSlotLimit => GameSession.BaseStorageSlotCount + Storage * GameSession.StorageSlotsPerLine + StorageByItem * GameSession.StorageSlotsPerLine;

    /// <summary>ItemContainerSlotExtend RS/NT tail counts for the requested EItemContainerType.</summary>
    public (byte ByItem, byte ByGold) WireExtendPair(byte container) => container switch
    {
        InventoryItem.Bag => ((byte)InventoryByItem, (byte)InventoryByGold),
        InventoryItem.Storage => ((byte)StorageByItem, (byte)Storage),
        InventoryItem.AccountStorage => ((byte)CharacterStorageByItem, (byte)CharacterStorageByGold),
        _ => (0, 0),
    };
}

// Bag operations and the hotbar. Opcodes are catalogue index + run base: ItemMove/Remove/Sort 0x563a/0x563c/
// 0x563e (+ 0x54ca, the 0x56 run around the OBSERVED 0x5610/0x5612/0x5646), QuickSlotSet/Reset 0x5601/0x5603
// (+ 0x54c5 below 0x5610: the live client's unanswered 0x5601 is 27 bytes = QuickSlotInfo, OBSERVED; the hotbar
// list 0x5600 is QuickSlotList_NT). Layouts are the usmap structs; result u16 as every observed RS.
public sealed partial class GameSession
{
    /// <summary>Official default bag before gold/item extensions (OBSERVED 27 slots = 3×9).</summary>
    public const int BagSlotsPerRow = 9, BaseBagSlotCount = 27;
    /// <summary>Personal warehouse (kStorage) before extensions: 25 slots = 5×5 (datamine).</summary>
    public const int StorageSlotsPerLine = 5, BaseStorageSlotCount = 25;
    static readonly long[] BagGoldExtendCosts = [500, 1_000, 2_000, 3_000, 5_000, 7_500, 10_000, 15_000, 20_000, 30_000];
    static readonly long[] StorageGoldExtendCosts =
    [
        1_000, 1_000, 1_500, 2_000, 2_500, 3_500, 5_000, 6_500, 9_000, 15_000, 20_000, 25_000, 35_000, 45_000, 60_000,
        80_000, 150_000, 150_000, 200_000, 300_000, 400_000, 550_000, 700_000, 950_000, 1_300_000, 1_800_000, 2_500_000,
        3_300_000, 4_500_000, 6_200_000, 8_400_000,
    ];
    const int MaxBagGoldExtensions = 10, MaxStorageGoldExtensions = 31;

    public const ushort ItemMoveRq = 0x563a, ItemMoveRs = 0x563b, ItemRemoveRq = 0x563c, ItemRemoveRs = 0x563d, ItemSortRq = 0x563e, ItemSortRs = 0x563f,
        // DevPacketData 0x398A ItemContainerSlotExtend: live +0x1ebe → 0x5848 (0x56 run) or +0x53c5 → 0x8d4f (0x8d run, after SealStone 0x3989→0x8d4e).
        ItemContainerSlotExtendRq = 0x5848, ItemContainerSlotExtendRs = 0x5849, ItemContainerSlotExtendNt = 0x584a,
        ItemContainerSlotExtend8dRq = 0x8d4f, ItemContainerSlotExtend8dRs = 0x8d50, ItemContainerSlotExtend8dNt = 0x8d51,
        QuickSlotSetRq = 0x5601, QuickSlotSetRs = 0x5602, QuickSlotResetRq = 0x5603, QuickSlotResetRs = 0x5604,
        QuickSlotContextSkillResetRq = 0x5609, QuickSlotContextSkillResetRs = 0x560a;
    ContainerSlotExtendView containerSlotExtends;
    const byte DecreaseItemRemove = 13;   // EDecreaseReason kItemRemove
    const byte DecreaseBagSlotExtend = 15;   // EDecreaseReason kShopBuy (kinah spend, same as merchant purchase)
    readonly SortedDictionary<int, QuickSlotRecord> quickSlots = [];
    public IReadOnlyDictionary<int, QuickSlotRecord> QuickSlots => quickSlots;

    async Task LoadQuickSlotsAsync(INativeCharacterStore store, Guid character, CancellationToken ct)
    {
        quickSlots.Clear();
        var repairs = new List<QuickSlotRecord>();
        var saved = await store.QuickSlotsAsync(character, ct);
        var barSkills = saved.Where(s => s.Type == 0 && s.SkillId > 0).Select(s => s.SkillId)
            .Concat(Profile()?.DefaultBar().Select(d => d.SkillId) ?? []).Where(id => id > 0)
            .SelectMany(AllowedContextSkillIds).ToHashSet();
        foreach (var slot in saved)
        {
            if (slot.Type == 0 && slot.SkillId != 0 && !ContextStackValid(slot.SkillId, ContextSkillsForSlot(slot), barSkills))
            {
                var fixedSlot = slot with { ContextSkills = data.DefaultContextSkills(slot.SkillId).ToArray() };
                quickSlots[slot.Slot] = fixedSlot;
                repairs.Add(fixedSlot);
            }
            else quickSlots[slot.Slot] = slot;
        }
        foreach (var slot in repairs)
            await store.SaveQuickSlotAsync(character, slot.Slot, slot, ct);
    }

    /// <summary>At login after <see cref="learned"/> is built: drop duplicate skill ids across the persisted bar.</summary>
    void DedupeQuickSlotsAtEntry()
    {
        if (quickSlots.Count == 0) return;
        var stacks = EffectiveSkillStacks().ToDictionary(p => p.Key, p => (int[])p.Value.Clone());
        DedupeSkillStacks(stacks);
        foreach (var (slot, stack) in stacks)
        {
            if (stack[0] == 0)
            {
                if (quickSlots.ContainsKey(slot) && HasDefaultQuickSlot(slot))
                    quickSlots[slot] = new QuickSlotRecord(slot, 0, 0, 0, 0, [0, 0, 0, 0]);
                else quickSlots.Remove(slot);
                continue;
            }
            quickSlots[slot] = new QuickSlotRecord(slot, 0, 0, 0, stack[0], stack);
        }
    }

    /// <summary>The hotbar at world entry: the player's saved slots, else the class's default bar.</summary>
    /// <summary>The bar at world entry: the class's default bar with the player's saved slots over it.</summary>
    byte[] Hotbar(IEnumerable<(int Slot, int SkillId)> defaults)
    {
        var bar = new SortedDictionary<int, (int Slot, int Type, long ItemUid, int ItemId, int SkillId, IReadOnlyList<int>?, bool AutoUse)>();
        foreach (var (slot, skill) in defaults)
            bar[slot] = (slot, 0, 0, 0, skill, data.DefaultContextSkills(skill), false);
        // PCLevelQuickSlot.dat (Steam 2026-10-28 client): every class starts with its potions on the consumable bar
        // (item slots 11-18 = F1-F8, QuickSlotData bEditable); the official level-1 entry sends these item records with
        // the 8 skills (capture 20261013 #376: 14 records). Only items the player holds in the bag; saved slots win.
        foreach (var (slot, itemId) in GamePackets.LevelQuickSlotItems)
            if (storedItems.Values.Where(i => i.Location == "bag" && i.TemplateId == itemId).Select(WireKey).FirstOrDefault() is var uid and not 0)
                bar[slot] = (slot, 1, uid, itemId, 0, null, false);
        foreach (var s in quickSlots.Values)
        {
            if (IsClearedQuickSlot(s)) bar.Remove(s.Slot);
            else bar[s.Slot] = (s.Slot, s.Type, s.ItemUid, s.ItemId, s.SkillId, ContextSkillsForSlot(s), s.AutoUse);
        }
        return GamePackets.QuickSlots(bar.Values.ToList());
    }

    /// <summary>A stored slot with nothing in it: how a slot of the default bar stays empty after the player emptied it (OURS).</summary>
    static bool IsClearedQuickSlot(QuickSlotRecord slot) => slot.SkillId == 0 && slot.ItemId == 0 && slot.ItemUid == 0;

    bool HasDefaultQuickSlot(int number) =>
        Profile()?.DefaultBar().Any(s => s.Slot == number) == true || GamePackets.LevelQuickSlotItems.Any(i => i.Slot == number);

    /// <summary>
    /// 0x563a ItemMove_RQ {npc key i32, EItemContainerType u8, item key i64, slot pos i32}: a bag or warehouse item to a
    /// slot of the bag (1) or the warehouse (2, kStorage: the Storage NPCs' depot; kept per character, OURS — the
    /// official one is account-wide). Equipped items go through ItemEquip.
    /// </summary>
    async Task<List<byte[]>> MoveItemInBagAsync(byte[] plain, CancellationToken ct)
    {
        int npc = BitConverter.ToInt32(plain, 2), slot = BitConverter.ToInt32(plain, 15);
        byte container = plain[6];
        var key = BitConverter.ToInt64(plain, 7);
        byte[] Reply(int result) => PacketWriter.Opcode(ItemMoveRs).U16(result).I32(npc).U8(container).I64(key).I32(slot).ToArray();
        var target = container switch { InventoryItem.Bag => "bag", InventoryItem.Storage => "storage", InventoryItem.AccountStorage => "account_storage", _ => null };
        if (target is null || Store is not { } store || CharacterId is not { } character || !storedItems.TryGetValue(key, out var row) || row.Location is not ("bag" or "storage" or "account_storage") || slot < 0)
            return [Reply(1)];
        var moved = await Stored(async () => (target == "bag" && row.Location == "bag"
            ? await store.MoveBagItemAsync(character, row.InstanceId, row.Revision, slot, ct)
            : await store.MoveItemToContainerAsync(character, row.InstanceId, row.Revision, target, slot, ct)) ? [[]] : []);
        if (moved.Count == 0) return [Reply(1)];
        return [Reply(0), .. await InventoryChangesAsync(ct, 0, 0), .. target == "storage" && row.Location == "bag" ? Achieve("StorageStore") : []];
    }

    /// <summary>0x563c ItemRemove_RQ {map item key -> count (bag), map (temporary bag)}: discarded, answered with the same maps.</summary>
    async Task<List<byte[]>> RemoveItemsAsync(byte[] plain, CancellationToken ct)
    {
        var bag = new List<(long Key, long Count)>();
        var temporary = new List<(long Key, long Count)>();
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            foreach (var list in new[] { bag, temporary })
            {
                var count = reader.Varint();
                for (var i = 0; i < count && i < 128; i++) list.Add(((long)reader.U64(), (long)reader.U64()));
            }
        }
        catch (ProtocolException) { return []; }
        byte[] Reply(int result)
        {
            var w = PacketWriter.Opcode(ItemRemoveRs).U16(result);
            foreach (var list in new[] { bag, temporary })
            {
                w.Varint(list.Count);
                foreach (var (key, count) in list) w.I64(key).I64(count);
            }
            return w.ToArray();
        }
        if (Store is not { } store || CharacterId is not { } character || temporary.Count > 0 || bag.Count == 0) return [Reply(1)];
        foreach (var (key, count) in bag)
        {
            if (!storedItems.TryGetValue(key, out var row) || row.Location != "bag" || count < 1 || count > row.Quantity) return [Reply(1)];
            var removed = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, (int)count, ct) ? [[]] : []);
            if (removed.Count == 0) return [Reply(1), .. await InventoryChangesAsync(ct, 0, DecreaseItemRemove)];
        }
        return [Reply(0), .. await InventoryChangesAsync(ct, 0, DecreaseItemRemove)];
    }

    /// <summary>0x563e ItemSort_RQ {npc key i32, EItemContainerType u8}: the bag ordered by item, answered 0x563f.</summary>
    /// <summary>ItemContainerSlotExtend_RQ (0x5848 or 0x8d4f): npc i32, container u8, item uid i64, cost idx u8 (gold path uses uid 0).</summary>
    async Task<List<byte[]>> ExtendContainerSlotsAsync(byte[] plain, CancellationToken ct)
    {
        var rq = Opcode(plain);
        if (plain.Length < 16 || rq is not (ItemContainerSlotExtendRq or ItemContainerSlotExtend8dRq))
        {
            LogContainerSlotExtend(plain.Length > 6 ? plain[6] : (byte)0, -1, -1, -1, $"bad_request op=0x{rq:x4} len={plain.Length}");
            return [];
        }
        var rsOpcode = (ushort)(rq + 1);
        var ntOpcode = (ushort)(rq + 2);
        var npc = BitConverter.ToInt32(plain, 2);
        var container = plain[6];
        var itemUid = BitConverter.ToInt64(plain, 7);
        var costIdx = plain[15];
        byte[] Rs(int result, ContainerSlotExtendView counts)
        {
            var (byItem, byGold) = counts.WireExtendPair(container);
            return PacketWriter.Opcode(rsOpcode).I32(result).I32(npc).U8(container).I64(itemUid).U8(costIdx).U8(byItem).U8(byGold).ToArray();
        }
        byte[] Nt(ContainerSlotExtendView counts)
        {
            var (byItem, byGold) = counts.WireExtendPair(container);
            return PacketWriter.Opcode(ntOpcode).U8(container).I64(itemUid).U8(byItem).U8(byGold).ToArray();
        }
        var cachedExt = container == InventoryItem.Bag ? containerSlotExtends.InventoryByGold : containerSlotExtends.Storage;
        if (Store is not { } store || CharacterId is not { } character)
        {
            LogContainerSlotExtend(container, costIdx, 1, cachedExt, "no_store");
            return [Rs(1, containerSlotExtends)];
        }
        if (itemUid != 0)
        {
            LogContainerSlotExtend(container, costIdx, 1, cachedExt, $"item_path_unsupported uid={itemUid} op=0x{rq:x4} len={plain.Length}");
            return [Rs(1, containerSlotExtends)];
        }
        if (container is not (InventoryItem.Bag or InventoryItem.Storage))
        {
            LogContainerSlotExtend(container, costIdx, 1, cachedExt, "container_unsupported");
            return [Rs(1, containerSlotExtends)];
        }
        containerSlotExtends = ContainerSlotExtendView.From(await store.ContainerSlotExtendsAsync(character, ct));
        var maxExt = container == InventoryItem.Bag ? MaxBagGoldExtensions : MaxStorageGoldExtensions;
        var currentExt = container == InventoryItem.Bag ? containerSlotExtends.InventoryByGold : containerSlotExtends.Storage;
        if (currentExt >= maxExt)
        {
            LogContainerSlotExtend(container, costIdx, 4, currentExt, "max_reached");
            return [Rs(4, containerSlotExtends), .. InventoryDepotSyncPackets(), .. await InventoryChangesAsync(ct, 0, 0)];
        }
        // The live client keeps sending costIdx=0 (the price row it shows) after an extension, so its index is advisory:
        // the server always charges the next tier (= current extension count) and accepts the request.
        var tier = currentExt;
        var reason = costIdx == tier ? "ok" : $"ok client_cost_idx={costIdx} tier={tier}";
        var outcome = ("none", ContainerSlotExtendCounts.Zero);
        await Stored(async () =>
        {
            outcome = container == InventoryItem.Bag
                ? await store.ExtendInventoryByGoldAsync(character, tier, ct)
                : await store.ExtendStorageByGoldAsync(character, tier, ct);
            return outcome.Item1 == "ok" ? [[]] : [];
        });
        if (outcome.Item1 != "ok")
        {
            var fail = outcome.Item1 switch { "kina" => 3, "count" => 4, _ => 1 };
            // "none" (store refused / not owned / row missing) carries zero counts: keep the cached ones so the bag does not shrink.
            if (outcome.Item1 != "none") containerSlotExtends = ContainerSlotExtendView.From(outcome.Item2);
            LogContainerSlotExtend(container, costIdx, fail, container == InventoryItem.Bag ? containerSlotExtends.InventoryByGold : containerSlotExtends.Storage, $"store_{outcome.Item1}");
            return [Rs(fail, containerSlotExtends), .. await InventoryChangesAsync(ct, 0, 0)];
        }
        containerSlotExtends = ContainerSlotExtendView.From(outcome.Item2);
        var newExt = container == InventoryItem.Bag ? containerSlotExtends.InventoryByGold : containerSlotExtends.Storage;
        LogContainerSlotExtend(container, costIdx, 0, newExt, reason);
        return
        [
            Rs(0, containerSlotExtends),
            Nt(containerSlotExtends),
            .. await InventoryChangesAsync(ct, 0, DecreaseBagSlotExtend),
            .. InventoryDepotSyncPackets(),
        ];
    }

    async Task<List<byte[]>> SortBagAsync(byte[] plain, CancellationToken ct)
    {
        var npc = BitConverter.ToInt32(plain, 2);
        var container = plain[6];
        byte[] Reply(int result) => PacketWriter.Opcode(ItemSortRs).U16(result).I32(npc).U8(container).ToArray();
        var location = container switch { InventoryItem.Bag => "bag", InventoryItem.Storage => "storage", InventoryItem.AccountStorage => "account_storage", _ => null };
        if (location is null || Store is not { } store || CharacterId is not { } character) return [Reply(1)];
        var sorted = await Stored(async () => await store.SortContainerAsync(character, location, ct) ? [[]] : []);
        return sorted.Count == 0 ? [Reply(1)] : [Reply(0), .. await InventoryChangesAsync(ct, 0, 0)];
    }

    /// <summary>
    /// 0x5601 QuickSlotSet_RQ {QuickSlotInfo: slot u8, EQuickSlotType u8, item uid i64, item id i32, skill id i32,
    /// context skills (varint + i32), macro u8, social action i32, toggle u8}: stored, echoed in 0x5602.
    /// </summary>
    async Task<List<byte[]>> SetQuickSlotAsync(byte[] plain, CancellationToken ct)
    {
        if (!TryParseQuickSlotInfo(plain, out var number, out var type, out var uid, out var itemId, out var skillId, out var context, out var autoUse))
            return [];
        byte[] Reply(int result) => PacketWriter.Opcode(QuickSlotSetRs).U16(result).Raw(plain.AsSpan(2)).ToArray();
        if (Store is not { } store || CharacterId is not { } character || type is < 0 or > 4) return [Reply(1)];
        if (type == 1 && uid != 0 && (!storedItems.TryGetValue(uid, out var itemRow) || itemRow.Location != "bag" || itemRow.TemplateId != itemId))
            return [Reply(1)];
        // A stigma needs to be learned and a free stigma slot (docs/STIGMAS.md; stigmas on only).
        if (StigmaSlotCheck(number, type, skillId) is { } stigmaResult)
        {
            if (stigmaResult != 0) return [Reply(stigmaResult)];
        }
        else if (type == 0 && skillId != 0 && !learned.Contains(skillId)) return [Reply(1)];
        context ??= type == 0 && skillId != 0 ? data.DefaultContextSkills(skillId).ToArray() : null;
        var slot = new QuickSlotRecord(number, type, uid, itemId, skillId, context, autoUse);
        var saved = await Stored(async () => await store.SaveQuickSlotAsync(character, number, slot, ct) ? [[]] : []);
        if (saved.Count == 0) return [Reply(1)];
        quickSlots[number] = slot;
        return [Reply(0)];
    }

    /// <summary>QuickSlotInfo body after opcode: mask, slot, type, item uid/id, skill id, optional context skills, macro, social, toggle.</summary>
    static bool TryParseQuickSlotInfo(byte[] plain, out int number, out int type, out long uid, out int itemId, out int skillId, out int[]? context, out bool autoUse)
    {
        number = type = itemId = skillId = 0;
        uid = 0;
        context = null;
        autoUse = false;
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            var mask = reader.U8();
            number = reader.U8(); type = reader.U8(); uid = (long)reader.U64(); itemId = (int)reader.U32(); skillId = (int)reader.U32();
            if ((mask & 1) != 0 && type == 0 && skillId != 0)
            {
                var count = reader.U8();
                var ids = new int[4];
                for (var i = 0; i < count && i < 4; i++) ids[i] = (int)reader.U32();
                context = ids;
            }
            _ = reader.U8();
            _ = reader.U32();
            autoUse = reader.U8() != 0;
            return true;
        }
        catch (ProtocolException) { return false; }
    }

    /// <summary>0x5603 QuickSlotReset_RQ {slot u8}: cleared, answered 0x5604 {result, slot}.</summary>
    async Task<List<byte[]>> ResetQuickSlotAsync(byte number, CancellationToken ct)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(QuickSlotResetRs).U16(result).U8(number).ToArray();
        if (Store is not { } store || CharacterId is not { } character) return [Reply(1)];
        // A default-bar slot is emptied with a stored empty record; deleting the row would bring the default back at the next login.
        var tombstone = HasDefaultQuickSlot(number) ? new QuickSlotRecord(number, 0, 0, 0, 0, [0, 0, 0, 0]) : null;
        var saved = await Stored(async () => await store.SaveQuickSlotAsync(character, number, tombstone, ct) ? [[]] : []);
        if (saved.Count == 0) return [Reply(1)];
        if (tombstone is null) quickSlots.Remove(number);
        else quickSlots[number] = tombstone;
        return [Reply(0)];
    }

    /// <summary>
    /// 0x5609 QuickSlotContextSkillReset_RQ (no body; the Skill Order window's reset button): every skill slot's stack goes
    /// back to its class default, saved, answered 0x560a {result u16} and the bar sent again as 0x5600 (CANDIDATE: no official reply sample).
    /// </summary>
    async Task<List<byte[]>> ResetQuickSlotContextAsync(CancellationToken ct)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(QuickSlotContextSkillResetRs).U16(result).ToArray();
        if (Store is not { } store || CharacterId is not { } character || Profile() is not { } profile) return [Reply(1)];
        var changed = quickSlots.Values
            .Where(s => s.Type == 0 && s.SkillId != 0)
            .Select(s => s with { ContextSkills = data.DefaultContextSkills(s.SkillId).ToArray() })
            .Where(s => !s.ContextSkills!.SequenceEqual(ContextSkillsForSlot(quickSlots[s.Slot])))
            .ToList();
        var saved = await Stored(async () =>
        {
            foreach (var slot in changed)
                if (!await store.SaveQuickSlotAsync(character, slot.Slot, slot, ct)) return [];
            return [[]];
        });
        if (saved.Count == 0) return [Reply(1)];
        foreach (var slot in changed) quickSlots[slot.Slot] = slot;
        return [Reply(0), Hotbar(profile.DefaultBar())];
    }

    /// <summary>
    /// Hotbar auto-use (QuickSlotInfo._toggle_yn): when the item buff is missing or expired and the cooldown is ready,
    /// consume one stack and send the normal item-use replies (in or out of combat). Also honors SetAutoUseItem ids.
    /// </summary>
    async Task<List<byte[]>> AutoQuickSlotItemsAsync(long now, CancellationToken ct)
    {
        if (resources is null || dead || Store is null || itemCast is not null) return [];
        foreach (var slot in quickSlots.Values.OrderBy(s => s.Slot))
        {
            if (slot.Type != 1 || !slot.AutoUse || slot.ItemId == 0) continue;
            if (!ItemAutoUseReady(slot.ItemId, now, out var stack)) continue;
            var used = await UseItemAsync(WireKey(stack!), now, ct);
            if (used.Count == 0 || used[0].Length >= 3 && used[0][2] != 0) continue;
            LogQuickSlotAutoUse(slot.ItemId, slot.Slot, "server");
            return used;
        }
        foreach (var itemId in autoUseItems.OrderBy(i => i))
        {
            if (quickSlots.Values.Any(s => s.Type == 1 && s.AutoUse && s.ItemId == itemId)) continue;
            if (!ItemAutoUseReady(itemId, now, out var stack)) continue;
            var used = await UseItemAsync(WireKey(stack!), now, ct);
            if (used.Count == 0 || used[0].Length >= 3 && used[0][2] != 0) continue;
            LogQuickSlotAutoUse(itemId, -1, "server_setting");
            return used;
        }
        return [];
    }

    bool ItemAutoUseReady(int itemId, long now, out ItemState? stack)
    {
        stack = ResolveBagStack(itemId, 0);
        if (stack is null || Boxes.Use(itemId) is not { Effect: "Skill" }) return false;
        var cooldown = Boxes.Cooldown(itemId);
        if (cooldown.Group != "None" && now < itemCooldowns.GetValueOrDefault(cooldown.Group)) return false;
        return NeedsItemBuffRefresh(itemId, now);
    }

    ItemState? ResolveBagStack(int itemId, long preferredUid)
    {
        if (preferredUid != 0 && storedItems.TryGetValue(preferredUid, out var preferred) && preferred.Location == "bag"
            && preferred.TemplateId == itemId && preferred.Quantity > 0)
            return preferred;
        return storedItems.Values.Where(i => i.Location == "bag" && i.TemplateId == itemId && i.Quantity > 0)
            .OrderBy(i => i.Quantity).FirstOrDefault();
    }

    /// <summary>True when a skill consumable's Abnormal_True buffs are all absent or expired.</summary>
    bool NeedsItemBuffRefresh(int itemId, long now)
    {
        if (Boxes.Use(itemId) is not { Effect: "Skill", Values: [var skillText, ..] } || !int.TryParse(skillText, out var skillId))
            return false;
        var abnormalIds = Boxes.SkillEffects(skillId).Where(e => e.Type == "Abnormal_True").Select(e => e.Value(2)).Where(id => id > 0).ToList();
        if (abnormalIds.Count == 0) return false;
        return !abnormalIds.Any(id => buffs.Any(b => b.AbnormalId == id && b.ExpireAt > now));
    }

    /// <summary>Party members on the same map within this distance share a kill's experience (OURS).</summary>
    public const double PartyExpRange = 10_000;

    /// <summary>The killer's party members nearby (not the killer).</summary>
    List<WorldPlayer> NearbyPartyMembers()
    {
        if (worldPlayer?.Map is not { } map || position is not { } at || Social?.PartyOf(worldPlayer) is not { } party) return [];
        return party.Others(worldPlayer).Where(m => m.Map == map && !m.Session.Dead
            && (m.Position.X - at.X) * (m.Position.X - at.X) + (m.Position.Y - at.Y) * (m.Position.Y - at.Y) <= PartyExpRange * PartyExpRange).ToList();
    }

    /// <summary>Another member's share of a kill: applied on that player's own loop (its store, its messages).</summary>
    public void ShareExperience(long gained, CancellationToken ct = default) =>
        _ = InvokeAsync(async now => (true, gained > 0 ? await GainExperienceAsync(experience + gained, gained, ExpReasonNpc, ct) : []));
}
