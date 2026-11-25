using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Skin.dat and SkinSet.dat from tools/data/skins.json (tools/skin_data.py).</summary>
public sealed class SkinTables
{
    sealed record SkinFile(
        [property: JsonPropertyName("skins")] Dictionary<string, int> Skins,
        [property: JsonPropertyName("sets")] Dictionary<string, int[]> Sets,
        [property: JsonPropertyName("categories")] Dictionary<string, string> Categories);

    readonly SkinFile file;
    SkinTables(SkinFile file) => this.file = file;

    static readonly Lazy<SkinTables> embedded = new(() =>
    {
        using var stream = typeof(SkinTables).Assembly.GetManifestResourceStream("skins.json") ?? throw new InvalidOperationException("Missing embedded game data skins.json");
        return new SkinTables(JsonSerializer.Deserialize<SkinFile>(stream) ?? throw new InvalidOperationException("Empty skins.json"));
    });

    public static SkinTables Embedded => embedded.Value;
    /// <summary>A skin by its Skin.dat name (GetSkin items), or 0.</summary>
    public int Of(string name) => file.Skins.GetValueOrDefault(name);
    /// <summary>The skins of a set by its SkinSet.dat name (GetSkinSet items).</summary>
    public IReadOnlyList<int> Set(string name) => file.Sets.GetValueOrDefault(name) ?? [];
    /// <summary>SkinSet.dat names that include a skin id (for SkinSetFirstCollect).</summary>
    public IEnumerable<string> SetsContaining(int skinId) =>
        file.Sets.Where(p => p.Value.Contains(skinId)).Select(p => p.Key);
    public bool Known(int skinId) => file.Categories.ContainsKey(skinId.ToString(CultureInfo.InvariantCulture));
    public string Category(int skinId) => file.Categories.GetValueOrDefault(skinId.ToString(CultureInfo.InvariantCulture)) ?? "None";
    public int Count => file.Skins.Count;
}

// Skins (appearance items). The 0x8d run (catalogue index + 0x89d1): SkinInfo_NT 0x8d5b OBSERVED at the official world
// entry as `00 00 00 01 00 00` (no skins, no points, no pure-skin equips, preset 1, no preset or visual slot infos);
// SkinUpdate_NT 0x8d5c and SkinEquip_RQ/RS 0x8d5e/0x8d5f take the usmap fields (CANDIDATE layouts, result u16 as the
// neighbouring preset RS). GetSkin / GetSkinSet items add skins to the collection by Skin.dat / SkinSet.dat name.
// An equipped skin shows as the EquipBroad _skin_id of its visual slot, including slots with no item (eyewear and the
// other costume-only slots). Hide/unhide is EquipSlotVisibilityChange 0xe289/0xe28a (catalogue 0x3B8E/0x3B8F + 0xA6FB,
// the same run as PresetInfo 0xe28c): preset u8, EVisualEquipSlot u8, display bool. The flag is stored with the other
// character settings and rides on EquipBroad._display. Off-hand weapons and the shield have no visual slot.
public sealed partial class GameSession
{
    public const ushort SkinInfoNt = 0x8d5b, SkinUpdateNt = 0x8d5c, SkinEquipRq = 0x8d5e, SkinEquipRs = 0x8d5f,
        EquipSlotVisibilityRq = 0xe289, EquipSlotVisibilityRs = 0xe28a;
    static SkinTables Skins => SkinTables.Embedded;

    /// <summary>The collection: skin id -> count.</summary>
    readonly Dictionary<int, int> skins = [];
    /// <summary>Equipped skins by ESkinEquipSlot.</summary>
    readonly Dictionary<int, int> skinEquips = [];
    /// <summary>The dbid the client sent for a slot (echoed on SkinEquip_RS; not stored).</summary>
    readonly Dictionary<int, long> skinEquipDbids = [];
    /// <summary>EVisualEquipSlot values whose appearance is hidden (display false). Missing means shown.</summary>
    readonly HashSet<int> skinHidden = [];
    /// <summary>ESkinEquipSlot -> EVisualEquipSlot (the weapons share MainHand; off-hand weapons and the shield have no visual slot).</summary>
    static readonly Dictionary<int, int> SkinVisualSlot = new()
    {
        [1] = 2, [2] = 3, [3] = 4, [4] = 5, [5] = 6, [6] = 7, [7] = 8, [8] = 1, [10] = 1, [11] = 1, [13] = 1, [14] = 1, [15] = 1, [17] = 1, [18] = 1,
        [20] = 11, [21] = 10, [22] = 9, [23] = 12, [24] = 13, [25] = 14, [26] = 1, [27] = 15,
    };
    /// <summary>EVisualEquipSlot -> a representative ESkinEquipSlot (SkinInfo's visual-slot map is keyed by the skin slot).</summary>
    static readonly Dictionary<int, int> VisualToSkinSlot = new()
    {
        [1] = 8, [2] = 1, [3] = 2, [4] = 3, [5] = 4, [6] = 5, [7] = 6, [8] = 7, [9] = 22, [10] = 21, [11] = 20, [12] = 23, [13] = 24, [14] = 25, [15] = 27,
    };

    public IReadOnlyDictionary<int, int> OwnedSkins => skins;
    public IReadOnlyDictionary<int, int> SkinEquips => skinEquips;

    void LoadSkins(CharacterExtras extras)
    {
        skins.Clear();
        foreach (var (id, count) in extras.Skins) skins[id] = count;
        skinEquips.Clear();
        skinEquipDbids.Clear();
        foreach (var (slot, id) in extras.SkinEquips) skinEquips[slot] = id;
    }

    /// <summary>Hidden visual slots from character settings (missing or empty means every slot is shown).</summary>
    void LoadSkinVisibility(int[]? hidden)
    {
        skinHidden.Clear();
        if (hidden is null) return;
        foreach (var slot in hidden)
            if (slot is >= 1 and <= 15) skinHidden.Add(slot);
    }

    /// <summary>0x8d5b SkinInfo_NT: the collection {skin i32, count i32} (OBSERVED: 8 bytes per skin in the official entry lists), no points, no pure-skin equips, preset 1. Equips and hidden slots are appended only when the character has them, so an empty entry stays `00 00 00 01 00 00`.</summary>
    byte[] SkinInfo()
    {
        var w = PacketWriter.Opcode(SkinInfoNt).Varint(skins.Count);
        foreach (var (id, count) in skins.OrderBy(p => p.Key)) w.I32(id).I32(count);
        w.Varint(0).Varint(0).U8(1);
        WritePresetInfos(w);
        WriteVisualInfos(w);
        return w.ToArray();
    }

    void WritePresetInfos(PacketWriter w)
    {
        if (skinEquips.Count == 0) { w.Varint(0); return; }
        w.Varint(1).U8(1).Varint(skinEquips.Count);
        foreach (var (slot, skin) in skinEquips.OrderBy(p => p.Key))
            w.U8((byte)slot).U8((byte)slot).I64(skinEquipDbids.GetValueOrDefault(slot)).I32(skin);
    }

    void WriteVisualInfos(PacketWriter w)
    {
        var hidden = skinHidden.Where(VisualToSkinSlot.ContainsKey).Order().ToList();
        if (hidden.Count == 0) { w.Varint(0); return; }
        w.Varint(1).U8(1).Varint(hidden.Count);
        foreach (var visual in hidden) WriteVisualOption(w, VisualToSkinSlot[visual], visual);
    }

    /// <summary>VisualSlotOption: skin slot, visual slot, display, default mesh, override, fx i32 (usmap; EquipBroad's observed fx is a varint and stays that way).</summary>
    void WriteVisualOption(PacketWriter w, int skinSlot, int visual) =>
        w.U8((byte)skinSlot).U8((byte)visual).Bool(!skinHidden.Contains(visual)).Bool(true).Bool(true).I32(14);

    /// <summary>0x8d5c SkinUpdate_NT: the changed counts {skin i32 -> count i32}, no points.</summary>
    byte[] SkinUpdate(IReadOnlyCollection<int> ids)
    {
        var w = PacketWriter.Opcode(SkinUpdateNt).Varint(ids.Count);
        foreach (var id in ids) w.I32(id).I32(skins.GetValueOrDefault(id));
        return w.Varint(0).ToArray();
    }

    /// <summary>ESkinEquipSlot of the weapon the character is holding (0 when none, or the type has no costume slot).</summary>
    static int WeaponSkinSlot(string? category) => category switch
    {
        "Greatsword" => 10,
        "Sword" or "Longsword" => 8,
        "Dagger" or "Shortsword" => 11,
        "Bow" => 13,
        "Staff" => 14,
        "Mace" => 15,
        "Magicbook" or "Spellbook" => 17,
        "Orb" => 18,
        "Gauntlet" or "Gun" => 26,
        _ => 0,
    };

    /// <summary>
    /// The skin shown in a visual slot (0 none). Main-hand shows the skin for the weapon in hand, so a leftover
    /// costume for another weapon type cannot keep the model after that weapon's skin is taken off.
    /// </summary>
    int SkinIn(int visualSlot)
    {
        var matches = skinEquips.Where(p => SkinVisualSlot.GetValueOrDefault(p.Key) == visualSlot).OrderBy(p => p.Key).ToList();
        if (matches.Count == 0) return 0;
        if (visualSlot == 1 && WeaponSkinSlot(data.WeaponType(equippedWeapon)) is var prefer and > 0)
            return skinEquips.GetValueOrDefault(prefer);
        return matches[0].Value;
    }

    /// <summary>The visual equips with their skins: the 15 EVisualEquipSlot records (others' appearance) or the worn ones plus costume-only and hidden slots (the own).</summary>
    IReadOnlyList<EquipBroad> VisualEquips(bool all)
    {
        var items = VisualItems();
        IEnumerable<int> slots = all
            ? Enumerable.Range(1, 15)
            : items.Keys.Union(Enumerable.Range(1, 15).Where(s => SkinIn(s) != 0 || skinHidden.Contains(s))).Order();
        return slots.Select(slot =>
        {
            var item = items.GetValueOrDefault(slot);
            return new EquipBroad(item, VisualSlot: (byte)slot, SkinId: SkinIn(slot), Display: !skinHidden.Contains(slot), Godstones: item == 0 ? [] : [0, 0, 0]);
        }).ToList();
    }

    static IReadOnlyList<int> SkinRewardIds(ItemUse use) =>
        use.Effect switch
        {
            "GetSkinSet" => Skins.Set(use.Values[0]),
            "GetSkin" => Skins.Of(use.Values[0]) is var id and > 0 ? [id] : [],
            _ => [],
        };

    /// <summary>Weapon-skin reward boxes: class pick row when the client sends select 0 (quick use).</summary>
    byte WeaponSkinBoxSelectIndex(byte requested, string group)
    {
        if (group is not "G_BM_Box_Package_WP_002_b" and not "G_BM_Box_Package_WP_004_b" || requested != 0)
            return requested;
        return WeaponSkinBoxSelectForHeldWeapon();
    }

    /// <summary>ESkin-equip order in G_BM_Box_Package_WP_002/004 select tables (GS..ST).</summary>
    static byte WeaponSkinBoxSelectForHeldWeapon(GameData data, int equippedWeapon) => data.WeaponType(equippedWeapon) switch
    {
        "Greatsword" => 0,
        "Sword" or "Longsword" => 1,
        "Dagger" or "Shortsword" => 2,
        "Bow" => 3,
        "Magicbook" or "Spellbook" => 4,
        "Orb" => 5,
        "Mace" => 6,
        "Staff" => 7,
        _ => 0,
    };

    byte WeaponSkinBoxSelectForHeldWeapon() => WeaponSkinBoxSelectForHeldWeapon(data, equippedWeapon ?? 0);

    /// <summary>
    /// Box rewards are rolled and shown in OpenRewardBox_RS even when catalog.grant_item skips a template; apply closet
    /// skins from the rolled GetSkin rows when auto-use did not persist them.
    /// </summary>
    async Task<List<byte[]>> EnsureBoxSkinGrantsAsync(IReadOnlyList<(int TemplateId, long Count)> rewards, List<byte[]> autoUse, long now, CancellationToken ct)
    {
        if (autoUse.Any(p => Opcode(p) == SkinUpdateNt)) return [];
        var ids = rewards.SelectMany(r => Boxes.Use(r.Item1) is { } use ? SkinRewardIds(use) : []).Distinct().ToList();
        if (ids.Count == 0) return [];
        var replies = await AcquireSkinsAsync(ids, ct);
        if (replies.Count == 0) return [];
        await ConsumeLeftoverSkinRewardItemsAsync(rewards.Select(r => r.Item1), now, ct);
        return replies;
    }

    async Task ConsumeLeftoverSkinRewardItemsAsync(IEnumerable<int> templateIds, long now, CancellationToken ct)
    {
        if (Store is not { } store || CharacterId is not { } character) return;
        foreach (var tid in templateIds)
        {
            if (Boxes.Use(tid) is not { Effect: "GetSkin" or "GetSkinSet" }) continue;
            foreach (var key in storedItems.Where(s => s.Value.TemplateId == tid && s.Value.Location == "bag").Select(s => s.Key).ToList())
            {
                var row = storedItems[key];
                await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, 1, ct) ? [[]] : []);
            }
        }
        await InventoryChangesAsync(ct, 0, DecreaseItemUse);
    }

    /// <summary>Skins added to the collection (an item, a command): stored, SkinUpdate_NT 0x8d5c. Empty when none is known.</summary>
    public async Task<List<byte[]>> AcquireSkinsAsync(IReadOnlyList<int> ids, CancellationToken ct)
    {
        var known = ids.Where(Skins.Known).Distinct().ToList();
        if (known.Count == 0) return [];
        if (Store is { } store && CharacterId is { } character)
        {
            var saved = await Stored(async () => await store.GrantSkinsAsync(character, known, ct) ? [[]] : []);
            if (saved.Count == 0) return [];
        }
        var prior = new HashSet<int>(skins.Keys);
        foreach (var id in known) skins[id] = skins.GetValueOrDefault(id) + 1;
        var achieve = new List<byte[]>();
        if (prior.Count == 0) achieve.AddRange(Emit(new AchievementEvent("SkinFirstCollectAny", SetValue: 1)));
        foreach (var id in known.Where(i => !prior.Contains(i)))
        foreach (var setName in Skins.SetsContaining(id))
            achieve.AddRange(Emit(new AchievementEvent("SkinSetFirstCollect", Keys: [setName])));
        return [SkinUpdate(known), .. achieve];
    }

    /// <summary>A GetSkin [skin name] or GetSkinSet [set name] item: used up, the skins acquired.</summary>
    async Task<List<byte[]>?> UseSkinItemAsync(INativeCharacterStore store, Guid character, ItemState row, ItemUse use, bool reply, CancellationToken ct)
    {
        var ids = use.Effect == "GetSkinSet" ? Skins.Set(use.Values[0]) : Skins.Of(use.Values[0]) is var one and > 0 ? [one] : [];
        if (ids.Count == 0) return null;
        var used = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, 1, ct) ? [[]] : []);
        if (used.Count == 0) return null;
        var replies = Used(row, reply);
        replies.AddRange(await InventoryChangesAsync(ct, 0, DecreaseItemUse));
        replies.AddRange(await AcquireSkinsAsync(ids, ct));
        return replies;
    }

    /// <summary>
    /// 0x8d5e SkinEquip_RQ: {ESkinEquipSlot u8 -> SkinEquipInfo {slot u8, skin dbid i64, skin i32}}; skin 0 clears the slot.
    /// 0x8d5f {result u16, the map echoing the request dbid, visual slot options for slots that have one}. The changed
    /// visual slots go to the wearer and those nearby (ItemEquipChange), including a costume on an empty slot.
    /// </summary>
    async Task<List<byte[]>> SkinEquipAsync(byte[] plain, CancellationToken ct)
    {
        var changes = new Dictionary<int, int>();
        var dbids = new Dictionary<int, long>();
        try
        {
            var r = new PacketReader(plain) { Offset = 2 };
            var count = (int)r.Varint();
            for (var i = 0; i < count && i < 32; i++)
            {
                int slot = r.U8();
                r.U8();
                dbids[slot] = r.I64();
                changes[slot] = r.I32();
            }
        }
        catch (ProtocolException) { return []; }
        byte[] Reply(int result)
        {
            var w = PacketWriter.Opcode(SkinEquipRs).U16(result).Varint(changes.Count);
            foreach (var (slot, skin) in changes) w.U8((byte)slot).U8((byte)slot).I64(dbids.GetValueOrDefault(slot)).I32(skin);
            if (result != 0) return w.Varint(0).ToArray();
            var visuals = changes.Keys.Where(SkinVisualSlot.ContainsKey).ToList();
            w.Varint(visuals.Count);
            foreach (var slot in visuals) WriteVisualOption(w, slot, SkinVisualSlot[slot]);
            return w.ToArray();
        }
        if (changes.Count == 0 || changes.Any(c => c.Key is < 1 or > 27 || c.Value != 0 && !skins.ContainsKey(c.Value))) return [Reply(1)];
        var before = VisualEquips(true);
        if (Store is { } store && CharacterId is { } character)
        {
            var saved = await Stored(async () => await store.SetSkinEquipsAsync(character, changes, ct) ? [[]] : []);
            if (saved.Count == 0) return [Reply(1)];
        }
        foreach (var (slot, skin) in changes)
        {
            if (skin == 0) { skinEquips.Remove(slot); skinEquipDbids.Remove(slot); }
            else { skinEquips[slot] = skin; skinEquipDbids[slot] = dbids.GetValueOrDefault(slot); }
        }
        var after = VisualEquips(true);
        var replies = new List<byte[]> { Reply(0) };
        var changed = before.Zip(after).Where(p => p.First.SkinId != p.Second.SkinId || p.First.Display != p.Second.Display)
            .Select(p => (p.First.VisualSlot, p.First, p.Second)).ToList();
        if (changed.Count > 0)
        {
            var shown = PlayerPackets.ItemEquipChange(playerKey, EquipItemLevel(), changed);
            worldPlayer?.Map?.Broadcast(worldPlayer, [shown]);
            replies.Add(shown);
        }
        return replies;
    }

    /// <summary>
    /// 0xe289 EquipSlotVisibilityChange_RQ {preset u8, EVisualEquipSlot u8, display bool}: the wardrobe's hide button.
    /// 0xe28a {result u16, the same three fields}. A change is stored and broadcast as EquipBroad._display.
    /// </summary>
    async Task<List<byte[]>> SkinVisibilityAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 5) return [];
        var preset = plain[2];
        var visual = plain[3];
        bool display;
        try { display = new PacketReader(plain) { Offset = 4 }.Bool(); }
        catch (ProtocolException) { return []; }
        byte[] Reply(int result) => PacketWriter.Opcode(EquipSlotVisibilityRs).U16(result).U8(preset).U8(visual).Bool(display).ToArray();
        if (visual is < 1 or > 15) return [Reply(1)];
        if (skinHidden.Contains(visual) == !display) return [Reply(0)];
        var before = VisualEquips(true);
        if (display) skinHidden.Remove(visual);
        else skinHidden.Add(visual);
        await SaveSettingAsync("skin_hidden", skinHidden.Order().ToArray(), ct);
        var after = VisualEquips(true);
        var replies = new List<byte[]> { Reply(0) };
        var changed = before.Zip(after).Where(p => p.First.Display != p.Second.Display || p.First.SkinId != p.Second.SkinId)
            .Select(p => (p.First.VisualSlot, p.First, p.Second)).ToList();
        if (changed.Count > 0)
        {
            var shown = PlayerPackets.ItemEquipChange(playerKey, EquipItemLevel(), changed);
            worldPlayer?.Map?.Broadcast(worldPlayer, [shown]);
            replies.Add(shown);
        }
        return replies;
    }
}
