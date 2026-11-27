namespace Aion.Protocol.Native;

/// <summary>
/// 2.0.13 packets that have no 2.0.9 counterpart (OBSERVED in the ingame-1/ingame-3 captures;
/// field names from the 2.0.13 SDK). Everything here is opt-in: 2.0.9 never receives these.
/// </summary>
public static class ModernPackets
{
    // Limit status (S2C, 18 B for the Kina/Gather pairs, 26 B for the currency table).
    public const ushort InitKinaLimitStatusNt = 0xFFA5, KinaLimitStatusNt = 0xFFA6,
        InitGatherLimitStatusNt = 0xFFA7, GatherLimitStatusNt = 0xFFA8,
        AccountCurrencyLimitStatusNt = 0xFFAE;

    // Combat state (C2S empty requests, 4 B replies) and skill-cast stop (S2C).
    public const ushort CombatStateOnRq = 0x383A, CombatStateOnRs = 0x383B,
        CombatStateOffRq = 0x383C, CombatStateOffRs = 0x383D,
        SkillCastingStopNt = 0x380C;

    // World / misc.
    public const ushort OdEnergyCubeCeilingInfoNt = 0x5712, BroadcastGatherSourceResultNt = 0x8D34,
        GatherComboStartNt = 0x8D3E, GuildCraftRecipeListNt = 0x8D83,
        RetrievePostNoticeInfoNt = 0xE34C, ClientKeyBindingInfoRt = 0xFFB3;
    /// <summary>0x385D on 2.0.9; the 2.0.13 client reads it as 0x6304.</summary>
    public const ushort AbyssRiftProgressResultNt = 0x385D;

    /// <summary>EAccountCurrencyLimitCategory rows OBSERVED at entry (PartyDungeon Easy 1, Normal 2, Hard 4).</summary>
    public static readonly byte[] AccountCurrencyCategories = [1, 2, 4];

    /// <summary>0xFFA5 InitKinaLimitStatus_NT: acquired Kina i64, server time i64.</summary>
    public static byte[] InitKinaLimitStatus(long acquiredKina, long now) =>
        PacketWriter.Opcode(InitKinaLimitStatusNt).I64(acquiredKina).I64(now).ToArray();

    /// <summary>0xFFA6 KinaLimitStatus_NT: acquired Kina i64, server time i64.</summary>
    public static byte[] KinaLimitStatus(long acquiredKina, long now) =>
        PacketWriter.Opcode(KinaLimitStatusNt).I64(acquiredKina).I64(now).ToArray();

    /// <summary>0xFFA7 InitGatherLimitStatus_NT: gather count i64, server time i64.</summary>
    public static byte[] InitGatherLimitStatus(long count, long now) =>
        PacketWriter.Opcode(InitGatherLimitStatusNt).I64(count).I64(now).ToArray();

    /// <summary>0xFFA8 GatherLimitStatus_NT: gather count i64, server time i64.</summary>
    public static byte[] GatherLimitStatus(long count, long now) =>
        PacketWriter.Opcode(GatherLimitStatusNt).I64(count).I64(now).ToArray();

    /// <summary>0xFFAE AccountCurrencyLimitStatus_NT: [{category u8, reward count i32}], then server time i64.</summary>
    public static byte[] AccountCurrencyLimitStatus(long now, IReadOnlyList<(byte Category, int RewardCount)>? rows = null)
    {
        var entries = rows ?? AccountCurrencyCategories.Select(category => (category, 0)).ToList();
        var w = PacketWriter.Opcode(AccountCurrencyLimitStatusNt).Varint((uint)entries.Count);
        foreach (var (category, rewardCount) in entries) w.U8(category).I32(rewardCount);
        return w.I64(now).ToArray();
    }

    /// <summary>0x383B CombatStateOn_RS: result u16 (0 accepted).</summary>
    public static byte[] CombatStateOnReply(ushort result = 0) => PacketWriter.Opcode(CombatStateOnRs).U16(result).ToArray();

    /// <summary>0x383D CombatStateOff_RS: result u16 (0 accepted).</summary>
    public static byte[] CombatStateOffReply(ushort result = 0) => PacketWriter.Opcode(CombatStateOffRs).U16(result).ToArray();

    /// <summary>0x380C SkillCastingStop_NT: caster key varint, result u16, skill i32, cooltime varint.</summary>
    public static byte[] SkillCastingStop(int key, ushort result, int skillId, uint cooltime = 0) =>
        PacketWriter.Opcode(SkillCastingStopNt).Varint(key).U16(result).I32(skillId).Varint(cooltime).ToArray();

    /// <summary>0x5712 OdEnergyCubeCeilingInfo_NT: [{dungeon i32, ceiling point u8, reward count u8}].</summary>
    public static byte[] OdEnergyCubeCeilingInfo(IReadOnlyList<(int DungeonId, byte CeilingPoint, byte RewardCount)> rows) =>
        WriteRows(PacketWriter.Opcode(OdEnergyCubeCeilingInfoNt), rows,
            (w, row) => w.I32(row.DungeonId).U8(row.CeilingPoint).U8(row.RewardCount));

    /// <summary>0x8D34 BroadcastGatherSourceResult_NT: source key varint, result u16, target key varint.</summary>
    public static byte[] BroadcastGatherSourceResult(int sourceKey, ushort result, int targetKey) =>
        PacketWriter.Opcode(BroadcastGatherSourceResultNt).Varint(sourceKey).U16(result).Varint(targetKey).ToArray();

    /// <summary>0x8D3E GatherComboStart_NT: combo count i32, expiration i64.</summary>
    public static byte[] GatherComboStart(int comboCount, long expirationMs) =>
        PacketWriter.Opcode(GatherComboStartNt).I32(comboCount).I64(expirationMs).ToArray();

    /// <summary>0x8D83 GuildCraftRecipeList_NT: count varint (the empty 3-byte form is OBSERVED).</summary>
    public static byte[] GuildCraftRecipeList() => PacketWriter.Opcode(GuildCraftRecipeListNt).Varint(0).ToArray();

    /// <summary>A notice row of 0xE34C: id, sender, title, body, times, localized texts and default language.</summary>
    public sealed record PostNotice(int Id, string Sender, string Title, string Body, long SentTimeMs, long ExpireTimeMs,
        IReadOnlyList<(byte Lang, string Title, string Body)> Texts, byte DefaultLang);

    /// <summary>
    /// 0xE34C RetrievePostNoticeInfo_NT: count varint, then each notice with its localized texts.
    /// The empty form (2-byte count 0) is what a server without notices sends.
    /// </summary>
    public static byte[] RetrievePostNoticeInfo(IReadOnlyList<PostNotice>? notices = null)
    {
        var rows = notices ?? [];
        var w = PacketWriter.Opcode(RetrievePostNoticeInfoNt).Varint((uint)rows.Count);
        foreach (var notice in rows)
        {
            w.I32(notice.Id).String(notice.Sender).String(notice.Title).String(notice.Body)
                .I64(notice.SentTimeMs).I64(notice.ExpireTimeMs).Varint((uint)notice.Texts.Count);
            foreach (var (lang, title, body) in notice.Texts) w.U8(lang).String(title).String(body);
            w.U8(notice.DefaultLang);
        }
        return w.ToArray();
    }

    /// <summary>
    /// 0x6304 AbyssRiftProgressResult_NT: rift id i32, winning race u8, reward count varint.
    /// The reward row (SDK RewardItemEnchant) is not encoded yet, so the count stays 0 (OURS).
    /// </summary>
    public static byte[] AbyssRiftProgressResult(int riftId, byte winRace = 0) =>
        PacketWriter.Opcode(ProtocolTranslation.ForModern(AbyssRiftProgressResultNt))
            .I32(riftId).U8(winRace).Varint(0).ToArray();

    static byte[] WriteRows<T>(PacketWriter w, IReadOnlyList<T> rows, Action<PacketWriter, T> write)
    {
        w.Varint((uint)rows.Count);
        foreach (var row in rows) write(w, row);
        return w.ToArray();
    }
}
