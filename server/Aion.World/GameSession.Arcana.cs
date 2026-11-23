using System.Globalization;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Ported from zaraza87/A2Core dell-merge-2026-11 (29934bea, docs/DELL-CHANGES-2026-11.md). On by default, as official;
// GameSessionOptions.ExperimentalRandomShopArcana (Native:ExperimentalRandomShopArcana) = false turns it off.
// Arcana (the client's Arcana window: card items, EEquipSlot kArcana 41-50). Live opcodes: ArcanaEnchant_RQ/RS 0x564f/0x5650,
// ArcanaExtract 0x5651/0x5652, ArcanaExtractPoint_NT 0x5653, ArcanaCreate 0x5654/0x5655, ArcanaCreateCoolTime_NT 0x5656,
// ArcanaQuickEquip 0x5657/0x5658. NO official Arcana message was ever recorded: every body is the field list with the observed
// conventions (results u16, arrays varint count, maps as key + value pairs, structs plain) - CANDIDATE throughout.
//   Create_RQ {create id i32, main stats [i32], sub stats [i32], sub skills [i32]} -> Create_RS {result, item uid i64, create id,
//     the three selections echoed, main stats [{stat i32, value i32}], sub stats [...], sub skills [...], cooltime end i64}, then the
//     bag and CreateCoolTime_NT {end i64}. ArcanaCreate.dat: the card, the material and the Kina (base + per stat picked); one
//     creation per create_cooltime_min, kept in the setting "arcana_create". The picks are ArcanaStatFilter rows of the card's
//     category (main: kind 1, a stat name; sub: kind 2, a skill id; class None or the player's): the picked main stat takes the
//     card's main stat value, stored as the card's rolled stats with the sub skills ("arcana_sub_skills", CANDIDATE: not applied
//     in combat). The bag must have room for the card (GameSession.BagRoom.cs).
//   Enchant_RQ {card uid i64, cards fed [i64], materials map {uid i64, count i32}} -> Enchant_RS {result, uid, cards, materials,
//     level u8, refund i64}. A fed card is worth its own group's GetExp at its level; levels rise while the exp reaches NeedExp,
//     each paying its Kina. The level is upgrades.enchant; the exp left is kept in the setting "arcana_exp" {item uid: exp} (OURS:
//     the source kept it on the item). A material item (arcana.json materials: Item.dat InternalName = an Arcana_Material group)
//     is worth its group's GetExp per unit.
//   QuickEquip_RQ {list [{item key i64, EEquipSlot u8}]} -> RS {result, list}: each card into its slot (41-50), refused below the
//     card's PermitLevelMin or for the other race (items.json; the slot per category is not checked: no table).
//   Extract_RQ: refused (Arcana_Extract_InvalidData): the extract points per card are in no table we have (only GlobalSetting
//     arcana_extract_point_max), neither in the client exports nor in the sources ported.
public sealed partial class GameSession
{
    public const ushort ArcanaEnchantRq = 0x564f, ArcanaEnchantRs = 0x5650, ArcanaExtractRq = 0x5651, ArcanaExtractRs = 0x5652,
        ArcanaCreateRq = 0x5654, ArcanaCreateRs = 0x5655, ArcanaCreateCoolTimeNt = 0x5656, ArcanaQuickEquipRq = 0x5657, ArcanaQuickEquipRs = 0x5658;
    const byte IncreaseCreateArcana = 69, DecreaseCreateArcana = 83, DecreaseEnchantArcana = 81;
    public const int ArcanaSlotFirst = 41, ArcanaSlotLast = 50;
    const int ArcanaExtractInvalidData = 15939, ArcanaCreateInvalidData = 15941, ArcanaCreateCoolTime = 15942, ArcanaCreateNotExistStatType = 15943, ArcanaCreateDuplicatedStatType = 15944,
        ArcanaCreateNotEnoughCurrency = 15945, ArcanaCreateNotEnoughInventory = 15946, ArcanaCreateInternalError = 15947, ArcanaQuickEquipInvalidData = 15948,
        ArcanaQuickEquipDuplicatedEquipSlot = 15949, ArcanaEnchantInvalidData = 15950, ArcanaEnchantInternalError = 15951,
        ArcanaEnchantInvalidMaterial = 15952, ArcanaEnchantNotEnoughCost = 15953;   // EResult Arcana_*

    static ArcanaTables Arcana => ArcanaTables.Embedded;
    long arcanaCreateUntil;
    Dictionary<string, int> arcanaExp = [];

    void LoadArcana(long[]? create, Dictionary<string, int>? exp)
    {
        arcanaCreateUntil = create is [var until, ..] ? until : 0;
        arcanaExp = exp ?? [];
    }

    internal long ArcanaCreateUntilForTests => arcanaCreateUntil;

    async Task<List<byte[]>?> ArcanaRequestAsync(byte[] plain, long now, CancellationToken ct)
    {
        if (!options.ExperimentalRandomShopArcana) return null;
        var opcode = Opcode(plain);
        if (opcode is not (ArcanaEnchantRq or ArcanaExtractRq or ArcanaCreateRq or ArcanaQuickEquipRq)) return null;
        try
        {
            return opcode switch
            {
                ArcanaCreateRq => await ArcanaCreateAsync(plain, now, ct),
                ArcanaEnchantRq => await ArcanaEnchantAsync(plain, ct),
                ArcanaQuickEquipRq => await ArcanaQuickEquipAsync(plain, ct),
                _ => [ArcanaExtractRefused(plain)],
            };
        }
        catch (ProtocolException) { return []; }
    }

    static List<int> ReadArcanaI32s(PacketReader r)
    {
        var n = r.Varint();
        if (n > 64) throw new ProtocolException("Too many entries");
        var list = new List<int>();
        for (var i = 0; i < n; i++) list.Add(r.I32());
        return list;
    }

    static PacketWriter ArcanaI32s(PacketWriter w, IReadOnlyList<int> list)
    {
        w.Varint(list.Count);
        foreach (var v in list) w.I32(v);
        return w;
    }

    static byte[] ArcanaExtractRefused(byte[] plain)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var type = r.Remaining > 0 ? r.U8() : (byte)0;
        return PacketWriter.Opcode(ArcanaExtractRs).U16(ArcanaExtractInvalidData).U8(type).Varint(0).Varint(0).I32(0).ToArray();
    }

    async Task<List<byte[]>> ArcanaCreateAsync(byte[] plain, long now, CancellationToken ct)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var createId = r.I32();
        var mains = ReadArcanaI32s(r);
        var subs = ReadArcanaI32s(r);
        var skills = ReadArcanaI32s(r);
        var until = arcanaCreateUntil;
        byte[] Reply(int result, long uid, ArcanaTables.ArcanaItem? card, long end)
        {
            var w = PacketWriter.Opcode(ArcanaCreateRs).U16(result).I64(uid).I32(createId);
            ArcanaI32s(ArcanaI32s(ArcanaI32s(w, mains), subs), skills);
            var stats = card?.MainStats ?? [];
            w.Varint(stats.Count);
            foreach (var (stat, value) in stats) w.I32(ItemStatWire.Embedded.WireOf(stat)).I32(value);
            return w.Varint(0).Varint(0).I64(end).ToArray();
        }
        if (Arcana.CreateOf(createId) is not { } row || Arcana.ArcanaOf(row.Item) is not { } card) return [Reply(ArcanaCreateInvalidData, 0, null, until)];
        if (mains.Count > row.MainMax || subs.Count > row.SubMax || mains.Distinct().Count() != mains.Count || subs.Distinct().Count() != subs.Count)
            return [Reply(ArcanaCreateDuplicatedStatType, 0, null, until)];
        var picked = new List<string>();
        foreach (var id in mains)
            if (Arcana.Filter(row.Category, 1, id) is { } f && (f.Class == "None" || f.Class == className)) picked.Add(f.Stat);
            else return [Reply(ArcanaCreateNotExistStatType, 0, null, until)];
        var subSkills = new List<int>();
        foreach (var id in subs)
            if (Arcana.Filter(row.Category, 2, id) is { } f && (f.Class == "None" || f.Class == className) && int.TryParse(f.Stat, out var skill)) subSkills.Add(skill);
            else return [Reply(ArcanaCreateNotExistStatType, 0, null, until)];
        if (until > now) return [Reply(ArcanaCreateCoolTime, 0, null, until)];
        if (Store is not { } store || CharacterId is not { } character) return [Reply(ArcanaCreateInternalError, 0, null, until)];
        var material = row.MaterialCount + row.MaterialPerMain * mains.Count + row.MaterialPerSub * subs.Count;
        var kina = row.Kina + row.KinaPerMain * mains.Count + row.KinaPerSub * subs.Count;
        var costs = new List<(Guid, long, int)>();
        if (material > 0) { if (CostStacks(row.Material, material) is { } m) costs.AddRange(m); else return [Reply(ArcanaCreateNotEnoughCurrency, 0, null, until)]; }
        if (kina > 0) { if (CostStacks(KinaItemId, kina) is { } k) costs.AddRange(k); else return [Reply(ArcanaCreateNotEnoughCurrency, 0, null, until)]; }
        if (!BagRoomFor([row.Item])) return [Reply(ArcanaCreateNotEnoughInventory, 0, null, until)];   // GameSession.BagRoom.cs
        var before = storedItems.Values.Where(i => i.TemplateId == row.Item).Select(i => i.InstanceId).ToHashSet();
        var made = await Stored(async () => await store.ShopBuyAsync(character, costs, [(row.Item, 1)], [], ct) ? [[]] : []);
        if (made.Count == 0) return [Reply(ArcanaCreateInternalError, 0, null, until)];
        var end = now + Arcana.CreateCooltimeMinutes * 60_000L;
        arcanaCreateUntil = end;
        await SaveSettingAsync("arcana_create", new[] { end }, ct);
        var changes = await InventoryChangesAsync(ct, IncreaseCreateArcana, DecreaseCreateArcana);
        var made1 = storedItems.Values.FirstOrDefault(i => i.TemplateId == row.Item && !before.Contains(i.InstanceId));
        var uid = made1?.WireId ?? 0;
        var stats = ArcanaCreatedStats(card, picked);
        if (made1 is not null && (picked.Count > 0 || subSkills.Count > 0))
        {
            var rolled = ArcanaRolled(stats, subSkills);
            var set = await Stored(async () => await store.SetRolledStatsAsync(character, made1.InstanceId, made1.Revision, rolled, ct) ? [[]] : []);
            if (set.Count > 0) storedItems[WireKey(made1)] = made1 with { RolledStats = rolled, Revision = made1.Revision + 1 };
        }
        Note($"arcana: created {row.Item} (creation {createId}, {mains.Count} main / {subs.Count} sub picks, {material} material, {kina} Kina)");
        var materialGroup = Arcana.MaterialGroup(row.Material);
        var arcanaKeys = new[] { card.Category, card.EnchantGroup, materialGroup ?? "" }.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToArray();
        return [Reply(0, uid, card with { MainStats = stats }, end), .. changes, .. Emit(new AchievementEvent("ArcanaCreate", Keys: arcanaKeys.Length > 0 ? arcanaKeys : null)),
            PacketWriter.Opcode(ArcanaCreateCoolTimeNt).I64(end).ToArray()];
    }

    /// <summary>The created card's main stats: the picked stats at the card's own main stat value (none picked: the card's own).</summary>
    internal static IReadOnlyList<(string Stat, int Value)> ArcanaCreatedStats(ArcanaTables.ArcanaItem card, IReadOnlyList<string> picked)
    {
        if (picked.Count == 0) return card.MainStats;
        var value = card.MainStats.Count > 0 ? card.MainStats[0].Value : 0;
        return picked.Select(p => (p, value)).ToList();
    }

    static System.Text.Json.JsonElement ArcanaRolled(IReadOnlyList<(string Stat, int Value)> stats, IReadOnlyList<int> subSkills)
    {
        var o = new Dictionary<string, object>();
        foreach (var (stat, value) in stats) o[stat] = value;
        if (subSkills.Count > 0) o["arcana_sub_skills"] = subSkills;
        return System.Text.Json.JsonSerializer.SerializeToElement(o);
    }

    int ArcanaExp(ItemState item) => arcanaExp.GetValueOrDefault(item.InstanceId.ToString("N", CultureInfo.InvariantCulture));

    async Task<List<byte[]>> ArcanaEnchantAsync(byte[] plain, CancellationToken ct)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var target = r.I64();
        var n = r.Varint();
        if (n > 64) return [];
        var fed = new List<long>();
        for (var i = 0; i < n; i++) fed.Add(r.I64());
        var m = r.Varint();
        if (m > 64) return [];
        var materials = new List<(long Key, int Count)>();
        for (var i = 0; i < m; i++) materials.Add((r.I64(), r.I32()));
        byte[] Reply(int result, int level)
        {
            var w = PacketWriter.Opcode(ArcanaEnchantRs).U16(result).I64(target).Varint(fed.Count);
            foreach (var k in fed) w.I64(k);
            w.Varint(materials.Count);
            foreach (var (k, c) in materials) w.I64(k).I32(c);
            return w.U8((byte)level).I64(0).ToArray();
        }
        if (Store is not { } store || CharacterId is not { } character || !storedItems.TryGetValue(target, out var row) || row.Location is not ("bag" or "equipment")
            || Arcana.ArcanaOf(row.TemplateId) is not { } card) return [Reply(ArcanaEnchantInvalidData, 0)];
        var level = EnchantOf(row);
        var exp = (long)ArcanaExp(row);
        var costs = new List<(Guid, long, int)>();
        var eaten = new List<Guid>();
        foreach (var key in fed.Distinct())
        {
            if (key == target || !storedItems.TryGetValue(key, out var food) || food.Location != "bag" || Arcana.ArcanaOf(food.TemplateId) is not { } foodCard)
                return [Reply(ArcanaEnchantInvalidMaterial, level)];
            exp += Arcana.Step(foodCard.EnchantGroup, EnchantOf(food))?.GetExp ?? 0;
            costs.Add((food.InstanceId, food.Revision, (int)food.Quantity));
            eaten.Add(food.InstanceId);
        }
        foreach (var (key, count) in materials)
        {
            if (count < 1 || !storedItems.TryGetValue(key, out var stack) || stack.Location != "bag" || stack.Quantity < count
                || Arcana.MaterialGroup(stack.TemplateId) is not { } group || Arcana.Step(group, 0) is not { } worth)
                return [Reply(ArcanaEnchantInvalidMaterial, level)];
            exp += (long)worth.GetExp * count;
            costs.Add((stack.InstanceId, stack.Revision, count));
        }
        if (costs.Count == 0) return [Reply(ArcanaEnchantInvalidMaterial, level)];
        var after = level;
        long kina = 0;
        while (Arcana.Step(card.EnchantGroup, after) is { NeedExp: > 0 } step && exp >= step.NeedExp && after < Arcana.MaxLevel(card.EnchantGroup))
        {
            exp -= step.NeedExp;
            kina += step.Kina;
            after++;
        }
        if (kina > 0) { if (CostStacks(KinaItemId, kina, row.InstanceId) is { } k) costs.AddRange(k); else return [Reply(ArcanaEnchantNotEnoughCost, level)]; }
        var done = await Stored(async () => await store.EnchantItemAsync(character, row.InstanceId, row.Revision, costs, after, EnchantOf(row, "enchant_bonus"), ct) ? [[]] : []);
        if (done.Count == 0) return [Reply(ArcanaEnchantInternalError, level)];
        foreach (var id in eaten) arcanaExp.Remove(id.ToString("N", CultureInfo.InvariantCulture));
        arcanaExp[row.InstanceId.ToString("N", CultureInfo.InvariantCulture)] = (int)Math.Min(exp, int.MaxValue);
        await SaveSettingAsync("arcana_exp", arcanaExp, ct);
        Note($"arcana: {row.TemplateId} enchant {level} -> {after} (exp {exp} left, {kina} Kina)");
        var replies = new List<byte[]> { Reply(0, after) };
        replies.AddRange(await InventoryChangesAsync(ct, 0, DecreaseEnchantArcana));
        if (row.Location == "equipment" && after != level) replies.AddRange(RefreshStats());
        return replies;
    }

    /// <summary>The equip rules of a card (items.json): not below its PermitLevelMin, not the other race's.</summary>
    bool ArcanaEquipRefused(int templateId) =>
        data.Item(templateId) is not { } info || info.PermitLevel > level || info.Race != 0 && info.Race != (profileRace == 2 ? 2 : 1);

    async Task<List<byte[]>> ArcanaQuickEquipAsync(byte[] plain, CancellationToken ct)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var n = r.Varint();
        if (n > 10) return [];
        var list = new List<(long Key, byte Slot)>();
        for (var i = 0; i < n; i++) list.Add((r.I64(), r.U8()));
        byte[] Reply(int result)
        {
            var w = PacketWriter.Opcode(ArcanaQuickEquipRs).U16(result).Varint(list.Count);
            foreach (var (k, s) in list) w.I64(k).U8(s);
            return w.ToArray();
        }
        if (Store is not { } store || CharacterId is not { } character || list.Count == 0) return [Reply(ArcanaQuickEquipInvalidData)];
        if (list.Select(e => e.Slot).Distinct().Count() != list.Count || list.Select(e => e.Key).Distinct().Count() != list.Count)
            return [Reply(ArcanaQuickEquipDuplicatedEquipSlot)];
        foreach (var (key, slot) in list)
            if (slot is < ArcanaSlotFirst or > ArcanaSlotLast || !storedItems.TryGetValue(key, out var item) || Arcana.ArcanaOf(item.TemplateId) is null
                || ArcanaEquipRefused(item.TemplateId))
                return [Reply(ArcanaQuickEquipInvalidData)];
        var changes = new List<byte[]>();
        foreach (var (key, slot) in list)
        {
            var item = storedItems[key];
            if (item.Location == "equipment" && item.Slot == slot) continue;
            // The card already in that slot goes back to the bag first.
            if (storedItems.Values.FirstOrDefault(i => i.Location == "equipment" && i.Slot == slot) is { } worn)
            {
                await Stored(async () => await store.MoveItemAsync(character, worn.InstanceId, worn.Revision, null, ct) ? [[]] : []);
                changes.AddRange(await InventoryChangesAsync(ct));
            }
            var fresh = storedItems.Values.First(i => i.InstanceId == item.InstanceId);
            var moved = await Stored(async () => await store.MoveItemAsync(character, fresh.InstanceId, fresh.Revision, slot, ct) ? [[]] : []);
            changes.AddRange(await InventoryChangesAsync(ct));
            if (moved.Count == 0) return [Reply(ArcanaQuickEquipInvalidData), .. changes];
        }
        Note($"arcana: quick equip {string.Join(", ", list.Select(e => $"{e.Key}@{e.Slot}"))}");
        return [Reply(0), .. changes, .. RefreshStats()];
    }
}
