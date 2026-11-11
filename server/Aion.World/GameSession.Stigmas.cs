using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Stigmas, the global way (docs/STIGMAS.md, tools/data/stigmas.json). Ported from ckrealm/A2Core (friends-server 8416c5a, dbc178b;
// AGPL-3.0) onto our skill-point code. Each class has 13 stigma skills (SkillAcquireData Stigma rows). They are not learned by
// level: from level 22 a character buys one with Stigma Shards (stigma level 1) and raises it to 20 (1/2/4/8 shards a level by
// band), and a stigma casts only from a quick slot (4 stigma slots open at 22, 27, 32 and 37 on global, CONFIRMED). Shards come
// from levelling (CANDIDATE totals x Aion:Stigmas:ShardRate) plus stored bonus shards (shops, Stigma Shard items, staff grants).
// Only the bought stigma levels are stored (game.character_stigmas); bonus shards are game.character_stigma_bonus.
// Reset refunds every spent shard and costs Aion:Stigmas:ResetKinah Kinah (default 100,000; 0 is free).
//
// Messages: CANDIDATE, no official stigma purchase has been captured. The shapes are the usmap's and the skill-point ones we
// OBSERVED (GameSession.SkillLevels.cs): SkillLevelUp_RQ 0x383f {skill} for a stigma learns it (0x3840, AddSkillList 0x5101
// reason SkillPointLevelUp) or raises it (0x3840, SkillLevelUpdate 0x3813), then SkillPointUpdate 0x5105 with ESkillPointType
// StigmaShard (2). SkillLevelDown_RQ 0x3841 takes one stigma level back; SkillLevelReset_RQ 0x3843 {ESkillAcquireType Stigma (2),
// skill or 0} forgets it (RemoveSkillList, opcode CANDIDATE 0x5102 next to AddSkillList 0x5101) and clears its quick slots.
// Everything here is behind Aion:Stigmas:Enabled (off by default); off, the skill-point messages behave exactly as before.
public sealed partial class GameSession
{
    const byte SkillPointTypeStigmaShard = 2;   // ESkillPointType
    const byte SkillAcquireTypeStigma = 2;      // ESkillAcquireType
    const int QuickSlotTypeStigma = 4;          // EQuickSlotType
    /// <summary>RemoveSkillList_NT {ActionProtocol key, skill ids}: CANDIDATE opcode (AddSkillList 0x5101 + 1; never observed).</summary>
    public const ushort RemoveSkillListNt = 0x5102;
    // EResult (usmap) for refused stigma requests.
    public const int SkillAcquireInvalidLevel = 6186, SkillAcquireInvalidClass = 6187, QuickSlotMismatchSlotType = 6485, QuickSlotStigmaMaxCount = 6492;
    /// <summary>A store save failed (same generic 1 as a skill-point save failure). Not an official stigma EResult.</summary>
    public const int SkillAcquirePersistFailed = 1;

    static StigmaOptions hostStigmaOptions = StigmaOptions.Default;

    /// <summary>The host's Aion:Stigmas (Program.cs); a session's <see cref="GameSessionOptions.Stigmas"/> wins.</summary>
    public static void ConfigureStigmas(StigmaOptions stigmaOptions) => hostStigmaOptions = stigmaOptions.Validated();

    /// <summary>Learned stigmas and their stigma levels.</summary>
    readonly Dictionary<int, int> stigmas = [];
    /// <summary>Bonus Stigma Shard points from items, shops and staff grants (not multiplied by ShardRate).</summary>
    int stigmaBonusShards;

    /// <summary>Stigma Shard (Bound), Item.dat Charge_StigmaShardPoint_1_A_s_01a_b: AutoUseWhenAcquire GetStigmaShard +1.</summary>
    public const int StigmaShardPointItemId = 531900010;

    StigmaOptions StigmaRules => options.Stigmas ?? hostStigmaOptions;
    StigmaTable StigmaData => data.Stigmas;

    /// <summary>Stigmas are on and this character's class has some.</summary>
    public bool StigmasOn => StigmaRules.Enabled && StigmaData.ClassStigmas(className).Count > 0;

    public IReadOnlyDictionary<int, int> Stigmas => stigmas;

    /// <summary>A stigma skill of this character's class (stigmas on).</summary>
    bool IsOwnStigma(int skillId) => StigmaRules.Enabled && StigmaData.IsStigmaOf(className, skillId);

    /// <summary>The stigma level of a learned stigma (stigmas on), else null.</summary>
    int? StigmaLevelOf(int skillId) => StigmasOn && stigmas.TryGetValue(skillId, out var lvl) ? lvl : null;

    /// <summary>Shards from levelling only (x ShardRate), not including bonus shards.</summary>
    public int StigmaShardsFromLevel => (int)Math.Floor(StigmaData.ShardsAt(level) * StigmaRules.ShardRate + 1e-9);

    /// <summary>Bonus shards from items, shops and staff grants.</summary>
    public int StigmaShardsBonus => Math.Max(0, stigmaBonusShards);

    /// <summary>The shards this character has had in all: levelling grants x ShardRate, plus stored bonus shards.</summary>
    public int StigmaShardsTotal => StigmaShardsFromLevel + StigmaShardsBonus;

    public int StigmaShardsSpent => stigmas.Sum(p => StigmaData.Spent(p.Value));

    public int StigmaShardsLeft => Math.Max(0, StigmaShardsTotal - StigmaShardsSpent);

    /// <summary>The stigma slots open at this level (Aion:Stigmas:Slots of them at most).</summary>
    public int StigmaSlotsOpen => StigmasOn ? StigmaData.SlotsAt(level, StigmaRules.Slots) : 0;

    /// <summary>The learned stigmas on a quick slot now.</summary>
    public IReadOnlyList<int> EquippedStigmas =>
        quickSlots.Values.Select(s => s.SkillId).Where(stigmas.ContainsKey).Distinct().ToList();

    /// <summary>Whether a stigma may be cast: learned and on a slot (unless Aion:Stigmas:RequireSlot is off).</summary>
    internal bool StigmaCastable(int skillId) =>
        StigmasOn && stigmas.ContainsKey(skillId) && (!StigmaRules.RequireSlot || EquippedStigmas.Contains(skillId));

    /// <summary>The learned stigmas for the skill list (skill, stigma level).</summary>
    IEnumerable<(int SkillId, int Level)> StigmaEntries() =>
        StigmasOn ? stigmas.OrderBy(p => p.Key).Select(p => (p.Key, Math.Clamp(p.Value, 1, 255))) : [];

    /// <summary>The leveled kit with the learned stigmas after it (0x5100 and its resyncs).</summary>
    List<(int SkillId, int Level)> WithStigmas(IReadOnlyList<(int SkillId, int Level)> kit)
    {
        if (!StigmasOn || stigmas.Count == 0) return kit as List<(int SkillId, int Level)> ?? kit.ToList();
        var have = kit.Select(k => k.SkillId).ToHashSet();
        return [.. kit, .. StigmaEntries().Where(s => !have.Contains(s.SkillId))];
    }

    void LoadStigmas(IReadOnlyList<StigmaState> stored)
    {
        stigmas.Clear();
        foreach (var s in stored)
            if (s.Level >= 1 && StigmaData.IsStigma(s.SkillId)) stigmas[s.SkillId] = Math.Min(s.Level, StigmaData.MaxSkillLevel);
    }

    void LoadStigmaBonus(int bonus) => stigmaBonusShards = Math.Max(0, bonus);

    /// <summary>
    /// At character load (stigmas on): drop another class's stigmas; clamp levels above Aion:Stigmas:MaxLevel; if the
    /// learned levels cost more shards than the level plus bonus shards give (a GM level-down or a lower ShardRate),
    /// reduce or forget only the excess (highest levels, then highest skill ids) instead of wiping every stigma.
    /// Bonus shards are part of the budget, so shop or staff purchases are not trimmed. Forgotten stigmas have their
    /// quick slots cleared. The entry path then sends 0x5105 (StigmaShardSync) when the tab is open.
    /// </summary>
    async Task TrimStigmasAsync(INativeCharacterStore store, Guid character, CancellationToken ct)
    {
        if (!StigmaRules.Enabled) return;
        var removed = new List<int>();
        var cap = Math.Min(StigmaRules.MaxLevel, StigmaData.MaxSkillLevel);
        foreach (var id in stigmas.Keys.Where(id => !StigmaData.IsStigmaOf(className, id)).ToList())
        {
            if (!await store.SetStigmaAsync(character, id, 0, ct)) continue;
            stigmas.Remove(id);
            removed.Add(id);
        }
        foreach (var id in stigmas.Keys.ToList())
        {
            var want = Math.Min(stigmas[id], cap);
            if (want == stigmas[id]) continue;
            if (await store.SetStigmaAsync(character, id, want, ct)) stigmas[id] = want;
        }
        while (stigmas.Count > 0 && StigmaShardsSpent > StigmaShardsTotal)
        {
            var reduce = stigmas.Where(p => p.Value > 1)
                .OrderByDescending(p => p.Value).ThenByDescending(p => p.Key)
                .Select(p => (int?)p.Key).FirstOrDefault();
            if (reduce is { } reduceId)
            {
                var next = stigmas[reduceId] - 1;
                if (!await store.SetStigmaAsync(character, reduceId, next, ct)) break;
                stigmas[reduceId] = next;
                continue;
            }
            var drop = stigmas.Keys.OrderByDescending(id => id).First();
            if (!await store.SetStigmaAsync(character, drop, 0, ct)) break;
            stigmas.Remove(drop);
            removed.Add(drop);
        }
        await ClearForgottenStigmaSlotsAsync(removed, ct);
    }

    /// <summary>
    /// Drop quick slots that still point at forgotten stigmas. The slot is always removed from memory (the stigma is
    /// already gone); a failed store save is retried on the next load.
    /// </summary>
    async Task ClearForgottenStigmaSlotsAsync(IReadOnlyCollection<int> forgotten, CancellationToken ct)
    {
        var drop = quickSlots.Values.Where(s => forgotten.Contains(s.SkillId)
            || (StigmaData.IsStigma(s.SkillId) && !stigmas.ContainsKey(s.SkillId))).ToList();
        foreach (var slot in drop)
        {
            if (Store is { } store && CharacterId is { } character)
                await Stored(async () => await store.SaveQuickSlotAsync(character, slot.Slot, null, ct) ? [[]] : []);
            quickSlots.Remove(slot.Slot);
        }
    }

    /// <summary>0x5105 SkillPointUpdate_NT {[type StigmaShard, point]} in the OBSERVED skill-point layout (CANDIDATE for stigmas).</summary>
    byte[] StigmaShardUpdate()
    {
        var point = StigmaRules.ShardUpdateSendsSpent ? StigmaShardsSpent : StigmaShardsLeft;
        return PacketWriter.Opcode(SkillPointUpdateNt).U8(1).U8(SkillPointTypeStigmaShard).Varint(point).U8(0).ToArray();
    }

    /// <summary>The shard update after skill-point syncs, from the stigma tab's level (stigmas on).</summary>
    IEnumerable<byte[]> StigmaShardSync() => StigmasOn && level >= StigmaData.UnlockLevel ? [StigmaShardUpdate()] : [];

    byte[] RemoveSkills(IReadOnlyList<int> ids)
    {
        var w = PacketWriter.Opcode(RemoveSkillListNt).Varint(playerKey).Varint((uint)ids.Count);
        foreach (var id in ids) w.I32(id);
        return w.ToArray();
    }

    /// <summary>What a stigma's next level needs, or why not: 0 ok, else the EResult the client shows.</summary>
    internal (int Result, int Next, int Cost) StigmaNext(int skillId)
    {
        if (!StigmasOn || !IsOwnStigma(skillId)) return (SkillAcquireInvalidClass, 0, 0);
        var next = stigmas.GetValueOrDefault(skillId) + 1;
        if (next > Math.Min(StigmaRules.MaxLevel, StigmaData.MaxSkillLevel) || StigmaData.Cost(next) is not { } cost) return (SkillAcquireInvalidLevel, next, 0);
        if (level < StigmaData.UnlockLevel) return (SkillAcquireNotEnoughCharLevel, next, cost);
        if (cost > StigmaShardsLeft) return (SkillAcquireNotEnoughSkillPoint, next, cost);
        return (0, next, cost);
    }

    async Task<bool> SaveStigmaAsync(int skillId, int stigmaLevel, CancellationToken ct)
    {
        if (Store is not { } store || CharacterId is not { } character) return true;
        var saved = await Stored(async () => await store.SetStigmaAsync(character, skillId, stigmaLevel, ct) ? [[]] : []);
        return saved.Count > 0;
    }

    static byte[] StigmaWireLevel(int skillId, int stigmaLevel, byte reason) => SkillLevelUpdate(skillId, stigmaLevel, stigmaLevel, 0, reason);

    /// <summary>0x383f for a stigma: learn it (level 1) or raise it one level for its Stigma Shards.</summary>
    async Task<List<byte[]>> StigmaLevelUpAsync(int skillId, CancellationToken ct)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(SkillLevelUpRs).U16(result).I32(skillId).ToArray();
        var (result, next, cost) = StigmaNext(skillId);
        if (result != 0)
        {
            Note($"StigmaLevelUp refused skill={skillId} result={result} next={next} cost={cost} shardsLeft={StigmaShardsLeft} charLevel={level}");
            return [Reply(result), ..StigmaShardSync()];
        }
        if (!await SaveStigmaAsync(skillId, next, ct)) return [Reply(SkillAcquirePersistFailed), ..StigmaShardSync()];
        stigmas[skillId] = next;
        RebuildWireClientSkillList();
        RecomputeCombatPower();
        return next == 1
            ? [Reply(0), GamePackets.AddSkillList(playerKey, [(skillId, 1)], SkillPointLevelUp, originalLevels: new Dictionary<int, int> { [skillId] = 1 }), StigmaShardUpdate()]
            : [Reply(0), StigmaWireLevel(skillId, next, SkillPointLevelUp), StigmaShardUpdate()];
    }

    /// <summary>0x3841 for a stigma: one stigma level back (not below 1), its shards refunded.</summary>
    async Task<List<byte[]>> StigmaLevelDownAsync(int skillId, CancellationToken ct)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(SkillLevelDownRs).U16(result).I32(skillId).ToArray();
        var current = stigmas.GetValueOrDefault(skillId);
        if (current <= 1) return [Reply(current == 0 ? SkillAcquireNotFoundData : SkillAcquireInvalidLevel), ..StigmaShardSync()];
        if (!await SaveStigmaAsync(skillId, current - 1, ct)) return [Reply(SkillAcquirePersistFailed), ..StigmaShardSync()];
        stigmas[skillId] = current - 1;
        RebuildWireClientSkillList();
        RecomputeCombatPower();
        return [Reply(0), StigmaWireLevel(skillId, current - 1, SkillPointLevelDown), StigmaShardUpdate()];
    }

    /// <summary>
    /// 0x3843 for stigmas (type Stigma, or a stigma skill): the stigma (every stigma when skill is 0) forgotten, its shards refunded
    /// and its quick slots cleared. Aion:Stigmas:ResetKinah Kinah is deducted in the same store step as the forget (0 = free).
    /// There is no named not-enough-Kinah EResult in our skill-reset path, so a short purse is refused with generic result 1
    /// (the same code as a failed store save) and nothing is forgotten. Reply 0x3844 {result, type, skill, modify list {skill, 0},
    /// modify points [StigmaShard, left]}. 0x3844 always carries shards left; the following 0x5105 follows ShardUpdateSendsSpent
    /// (spent by default). That split is CANDIDATE (no official reset has been captured) and is left as-is.
    /// </summary>
    async Task<List<byte[]>> StigmaResetAsync(byte acquireType, int skillId, CancellationToken ct)
    {
        var targets = skillId == 0 ? stigmas.Keys.Order().ToList() : stigmas.ContainsKey(skillId) ? [skillId] : [];
        byte[] Reply(int result, IReadOnlyList<int> reset)
        {
            var w = PacketWriter.Opcode(SkillLevelResetRs).U16(result).U8(acquireType).I32(skillId).Varint((uint)reset.Count);
            foreach (var id in reset) w.I32(id).U8(0);
            return w.Varint(1).AppendSkillPointInfo(StigmaShardsLeft, SkillPointTypeStigmaShard).ToArray();
        }
        if (acquireType != SkillAcquireTypeStigma)
            return [Reply(SkillAcquireInvalidClass, []), ..StigmaShardSync()];
        if (targets.Count == 0) return [Reply(SkillAcquireNotFoundData, []), ..StigmaShardSync()];
        var cost = Math.Max(0L, StigmaRules.ResetKinah);
        if (cost > 0 && CostStacks(KinaItemId, cost) is null)
        {
            Note($"StigmaReset refused skill={skillId} not enough kinah need={cost}");
            return [Reply(SkillAcquirePersistFailed, []), ..StigmaShardSync()];
        }
        var forgotten = new List<int>();
        if (Store is { } store && CharacterId is { } character)
        {
            var saved = await Stored(async () => await store.SpendKinahAndForgetStigmasAsync(character, cost, targets, ct) ? [[]] : []);
            if (saved.Count == 0) return [Reply(SkillAcquirePersistFailed, []), ..StigmaShardSync()];
            foreach (var id in targets) stigmas.Remove(id);
            forgotten.AddRange(targets);
        }
        else
        {
            foreach (var id in targets)
            {
                if (!await SaveStigmaAsync(id, 0, ct)) continue;
                stigmas.Remove(id);
                forgotten.Add(id);
            }
        }
        if (forgotten.Count == 0) return [Reply(SkillAcquireNotFoundData, []), ..StigmaShardSync()];
        await ClearForgottenStigmaSlotsAsync(forgotten, ct);
        RebuildWireClientSkillList();
        RecomputeCombatPower();
        var replies = new List<byte[]> { Reply(0, forgotten), RemoveSkills(forgotten), StigmaShardUpdate() };
        if (cost > 0) replies.AddRange(await InventoryChangesAsync(ct, 0, DecreaseShopBuy));
        return replies;
    }

    /// <summary>Staff or item use: add bonus shards, persist, and send a shard update when the stigma tab is open.</summary>
    public async Task<(bool Ok, string Message, List<byte[]> Packets)> GrantStigmaBonusAsync(int amount, CancellationToken ct)
    {
        if (amount <= 0) return (false, "Grant at least 1 Stigma Shard.", []);
        if (Store is not { } store || CharacterId is not { } character)
            return (false, "Enter the world first.", []);
        var total = await store.AddStigmaBonusShardsAsync(character, amount, ct);
        if (total is null) return (false, "The bonus shards could not be saved.", []);
        stigmaBonusShards = total.Value;
        var packets = StigmaShardSync().ToList();
        return (true, $"Granted {amount} bonus Stigma Shard{(amount == 1 ? "" : "s")}. Shards: {StigmaShardsLeft} of {StigmaShardsTotal} ({StigmaShardsBonus} bonus).", packets);
    }

    /// <summary>
    /// Stigma Shard (Bound) 531900010, Item.dat GetStigmaShard (AutoUseWhenAcquire): consume the stack and add its points
    /// to the bonus shard ledger. Below the stigma unlock level the item stays in the bag.
    /// </summary>
    async Task<List<byte[]>?> UseStigmaShardItemAsync(INativeCharacterStore store, Guid character, ItemState row, ItemUse use, bool reply, CancellationToken ct, int useCount = 1)
    {
        useCount = (int)Math.Clamp((long)useCount, 1L, Math.Min(row.Quantity, int.MaxValue));
        var perItem = 1;
        if (use.Values is [var amountText, ..] && int.TryParse(amountText, out var parsed) && parsed > 0) perItem = parsed;
        var grant = perItem * useCount;
        if (level < StigmaData.UnlockLevel) return null;
        var consumed = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, useCount, ct) ? [[]] : []);
        if (consumed.Count == 0) return null;
        var total = await store.AddStigmaBonusShardsAsync(character, grant, ct);
        if (total is null) return null;
        stigmaBonusShards = total.Value;
        usedCountApplied = true;
        return
        [
            .. Used(row, reply),
            .. await InventoryChangesAsync(ct, 0, DecreaseItemUse),
            .. StigmaShardSync(),
        ];
    }

    /// <summary>
    /// Whether a quick slot may hold this skill (stigmas on): 0 ok, else the EResult. A stigma must be learned and, when it is not
    /// on a slot yet, needs a free stigma slot; a Stigma-type slot (4) only takes a learned stigma. Null: not a stigma matter.
    /// </summary>
    int? StigmaSlotCheck(int slotNumber, int slotType, int skillId)
    {
        if (!StigmasOn || skillId == 0) return null;
        if (!IsOwnStigma(skillId)) return slotType == QuickSlotTypeStigma ? QuickSlotMismatchSlotType : null;
        if (!stigmas.ContainsKey(skillId)) return SkillAcquireNotFoundData;
        var others = quickSlots.Values.Where(s => s.Slot != slotNumber).Select(s => s.SkillId).Where(stigmas.ContainsKey).ToHashSet();
        return others.Contains(skillId) || others.Count < StigmaSlotsOpen ? 0 : QuickSlotStigmaMaxCount;
    }

    /// <summary>The reply to /stigma: shards, slots and the learned stigmas.</summary>
    public string StigmaSummary()
    {
        if (!StigmaRules.Enabled) return "Stigmas are off on this server (Aion:Stigmas:Enabled).";
        if (!StigmasOn) return "This class has no stigmas.";
        var equipped = EquippedStigmas;
        var learnedText = stigmas.Count == 0 ? "none learned"
            : string.Join(", ", stigmas.OrderBy(p => p.Key).Select(p =>
                $"{data.Skill(p.Key)?.Name ?? p.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)} lv{p.Value}{(equipped.Contains(p.Key) ? " (slotted)" : "")}"));
        var opens = level < StigmaData.UnlockLevel ? $" The stigma tab opens at level {StigmaData.UnlockLevel}." : "";
        var bonus = StigmaShardsBonus > 0 ? $" ({StigmaShardsBonus} bonus)" : "";
        var reset = StigmaRules.ResetKinah > 0 ? $" Reset costs {StigmaRules.ResetKinah} Kinah." : "";
        return $"Stigma Shards: {StigmaShardsLeft} of {StigmaShardsTotal}{bonus}. Slots: {equipped.Count}/{StigmaSlotsOpen}. Stigmas: {learnedText}.{opens}{reset}";
    }
}
