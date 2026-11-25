using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Appearance dyeing (SkinDecorate 0x8d64/0x8d65, SkinDecoInstanceList 0x8d68/0x8d69, SkinDecoInstanceInfo_NT 0x8d6a).</summary>
public sealed partial class GameSession
{
    public const ushort SkinDecorateRq = 0x8d64, SkinDecorateRs = 0x8d65,
        SkinDecoInstanceInfoNt = 0x8d6a;
    public const int SkinDecorateFail = 15437, SkinDecorateInvalidData = 15439, SkinDecorateInternalError = 15440;

    public sealed record SkinDecoSlot(
        byte Slot, byte ColorR, byte ColorG, byte ColorB, bool ColorRemove,
        int MaterialId, int PatternId,
        byte PatternColorR, byte PatternColorG, byte PatternColorB, bool PatternColorRemove,
        int PatternColorOpacity, int PatternOpacity, int PatternSizeHorizontal, int PatternSizeVertical,
        byte PatternType, bool PatternAdditional, int PatternGlossy, int PatternMetallic);

    public sealed record SkinDecoInstance(
        byte Type, long SkinDbid, int SkinId, IReadOnlyList<SkinDecoSlot> DecoSlots, long UpdateTime, int Index);

    readonly List<SkinDecoInstance> skinDecoInstances = [];

    void LoadSkinDecoInstances(IReadOnlyList<SkinDecoInstance>? stored)
    {
        skinDecoInstances.Clear();
        if (stored is { Count: > 0 }) skinDecoInstances.AddRange(stored);
    }

    static int SkinDecoEquipSlot(int skinId)
    {
        var weapon = WeaponSkinSlot(Skins.Category(skinId));
        if (weapon > 0) return weapon;
        return Skins.Category(skinId) switch
        {
            "Torso" => 3,
            "Pants" => 4,
            "Helmet" => 1,
            "Shoulder" => 2,
            "Gloves" => 5,
            "Boots" => 6,
            "Cape" => 7,
            "Eyewear" => 23,
            "Ear" => 22,
            "Necklace" => 21,
            "Ring" => 24,
            _ => 0,
        };
    }

    static SkinDecoSlot ReadSkinDecoSlot(PacketReader r) => new(
        r.U8(), r.U8(), r.U8(), r.U8(), r.Bool(),
        r.I32(), r.I32(),
        r.U8(), r.U8(), r.U8(), r.Bool(),
        r.I32(), r.I32(), r.I32(), r.I32(),
        r.U8(), r.Bool(), r.I32(), r.I32());

    static void WriteSkinDecoSlot(PacketWriter w, SkinDecoSlot s) =>
        w.U8(s.Slot).U8(s.ColorR).U8(s.ColorG).U8(s.ColorB).Bool(s.ColorRemove)
            .I32(s.MaterialId).I32(s.PatternId)
            .U8(s.PatternColorR).U8(s.PatternColorG).U8(s.PatternColorB).Bool(s.PatternColorRemove)
            .I32(s.PatternColorOpacity).I32(s.PatternOpacity).I32(s.PatternSizeHorizontal).I32(s.PatternSizeVertical)
            .U8(s.PatternType).Bool(s.PatternAdditional).I32(s.PatternGlossy).I32(s.PatternMetallic);

    static SkinDecoInstance ReadSkinDecoInstance(PacketReader r)
    {
        var type = r.U8();
        var dbid = r.I64();
        var skinId = r.I32();
        var slotCount = (int)r.Varint();
        var slots = new List<SkinDecoSlot>(slotCount);
        for (var i = 0; i < slotCount; i++) slots.Add(ReadSkinDecoSlot(r));
        return new(type, dbid, skinId, slots, r.I64(), r.I32());
    }

    static void WriteSkinDecoInstance(PacketWriter w, SkinDecoInstance i)
    {
        w.U8(i.Type).I64(i.SkinDbid).I32(i.SkinId).Varint(i.DecoSlots.Count);
        foreach (var slot in i.DecoSlots) WriteSkinDecoSlot(w, slot);
        w.I64(i.UpdateTime).I32(i.Index);
    }

    static (List<SkinDecoInstance> Instances, Dictionary<int, long> Costs)? ParseSkinDecorate(byte[] plain)
    {
        try
        {
            var r = new PacketReader(plain) { Offset = 2 };
            var count = (int)r.Varint();
            if (count is < 1 or > 32) return null;
            var instances = new List<SkinDecoInstance>(count);
            for (var i = 0; i < count; i++) instances.Add(ReadSkinDecoInstance(r));
            var costCount = (int)r.Varint();
            var costs = new Dictionary<int, long>();
            for (var i = 0; i < costCount; i++) costs[r.U8()] = r.I64();
            return r.End ? (instances, costs) : null;
        }
        catch (ProtocolException) { return null; }
    }

    byte[] SkinDecorateReply(int result, IReadOnlyDictionary<int, SkinDecoInstance> infos, IReadOnlyDictionary<int, long> costs)
    {
        var w = PacketWriter.Opcode(SkinDecorateRs).I32(result);
        w.Varint(infos.Count);
        foreach (var (slot, info) in infos) { w.U8(slot); WriteSkinDecoInstance(w, info); }
        w.Varint(costs.Count);
        foreach (var (currency, amount) in costs) w.U8(currency).I64(amount);
        w.Varint(skins.Count);
        foreach (var (skinId, count) in skins.OrderBy(p => p.Key))
            w.I32(skinId).I32(count).I32(count);
        return w.ToArray();
    }

    byte[] SkinDecoInstanceInfoNtPacket()
    {
        var w = PacketWriter.Opcode(SkinDecoInstanceInfoNt).Varint(skinDecoInstances.Count);
        foreach (var inst in skinDecoInstances) WriteSkinDecoInstance(w, inst);
        return w.ToArray();
    }

    List<byte[]> SkinDecoInstanceListReplies() =>
    [
        PacketWriter.Opcode(SkinDecoInstanceListRs).U16(0).ToArray(),
        SkinDecoInstanceInfoNtPacket(),
    ];

    async Task SaveSkinDecoInstancesAsync(CancellationToken ct) =>
        await SaveSettingAsync("skin_deco", skinDecoInstances, ct);

    async Task<List<byte[]>> SkinDecorateAsync(byte[] plain, long now, CancellationToken ct)
    {
        if (ParseSkinDecorate(plain) is not { } parsed)
            return [SkinDecorateReply(SkinDecorateInvalidData, new Dictionary<int, SkinDecoInstance>(), new Dictionary<int, long>())];
        if (Store is null || CharacterId is null)
            return [SkinDecorateReply(SkinDecorateInternalError, new Dictionary<int, SkinDecoInstance>(), new Dictionary<int, long>())];

        var (requests, expectedCost) = parsed;
        long qunaCost = 0;
        foreach (var (currency, amount) in expectedCost)
        {
            if (currency == ShopCurrency.QunaGroup) qunaCost += amount;
            else if (amount > 0) return [SkinDecorateReply(SkinDecorateInvalidData, new Dictionary<int, SkinDecoInstance>(), new Dictionary<int, long>())];
        }

        foreach (var req in requests)
        {
            if (!skins.ContainsKey(req.SkinId)) return [SkinDecorateReply(SkinDecorateInvalidData, new Dictionary<int, SkinDecoInstance>(), new Dictionary<int, long>())];
            if (SkinDecoEquipSlot(req.SkinId) == 0) return [SkinDecorateReply(SkinDecorateInvalidData, new Dictionary<int, SkinDecoInstance>(), new Dictionary<int, long>())];
        }

        var responseInfos = new Dictionary<int, SkinDecoInstance>();
        var pending = new List<(SkinDecoInstance Req, List<SkinDecoSlot> Slots, int EquipSlot)>();
        foreach (var req in requests)
        {
            var equipSlot = SkinDecoEquipSlot(req.SkinId);
            var mergedSlots = new List<SkinDecoSlot>();
            var removeAll = false;
            foreach (var slot in req.DecoSlots)
            {
                if (slot.ColorRemove) removeAll = true;
                else mergedSlots.Add(slot);
            }
            pending.Add((req, removeAll ? [] : mergedSlots, equipSlot));
        }

        var pay = await PayQunaAsync(qunaCost, new { kind = "skin_deco", quna = qunaCost }, async () =>
        {
            foreach (var (req, slots, equipSlot) in pending)
            {
                skinDecoInstances.RemoveAll(i => i.SkinId == req.SkinId && i.SkinDbid == req.SkinDbid);
                if (slots.Count == 0) continue;
                var stored = req with { DecoSlots = slots, UpdateTime = now };
                skinDecoInstances.Add(stored);
                responseInfos[equipSlot] = stored;
            }
            await SaveSkinDecoInstancesAsync(ct);
            return true;
        }, ct);
        if (pay == QunaPay.NotEnough) return [SkinDecorateReply(SkinDecorateFail, new Dictionary<int, SkinDecoInstance>(), new Dictionary<int, long>())];
        if (pay != QunaPay.Done) return [SkinDecorateReply(SkinDecorateInternalError, new Dictionary<int, SkinDecoInstance>(), new Dictionary<int, long>())];

        var replies = new List<byte[]> { SkinDecorateReply(0, responseInfos, expectedCost), SkinDecoInstanceInfoNtPacket() };
        if (qunaCost > 0)
        {
            await RefreshAccountQunaBalanceAsync(ct);
            replies.Add(QunaRefreshNow());
        }
        return replies;
    }

    /// <summary>Mail-delivered BM skin items never hit the bag; grant closet ownership directly.</summary>
    async Task<List<byte[]>> GrantMailShopSkinsAsync(IEnumerable<(int TemplateId, long Count)> rewards, CancellationToken ct)
    {
        var ids = new List<int>();
        foreach (var (templateId, _) in rewards)
        {
            if (Boxes.Use(templateId) is not { } use) continue;
            ids.AddRange(SkinRewardIds(use));
        }
        var replies = await AcquireSkinsAsync(ids.Distinct().ToList(), ct);
        if (replies.Count > 0) replies.Add(SkinInfo());
        return replies;
    }
}