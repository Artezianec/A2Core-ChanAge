namespace Aion.Protocol.Native;

// Ported from zaraza87/A2Core dell-merge-2026-11 (29934bea, docs/DELL-CHANGES-2026-11.md): the inspect window only (their emotes
// and content sweeps are not part of this port).

/// <summary>One worn item of an inspected character (Common_ViewCharEquipItemInfo).</summary>
public sealed record ViewCharItem(int ItemId, EquipDetail Detail, byte VisualSlot = 0, int SkinId = 0, string Maker = "", long PeriodExpireTime = 0,
    bool PeriodExpired = false, bool Display = true, bool DefaultMesh = true, bool Override = true);

/// <summary>What ViewChar_RS shows of a character (the inspect window).</summary>
public sealed record ViewCharInfo
{
    public string Name { get; init; } = "";
    public int PcId { get; init; }
    public byte Race { get; init; } = 1;
    public byte Gender { get; init; } = 1;
    public int Level { get; init; } = 1;
    public int ConquerorLevel { get; init; }
    public int EquipItemLevel { get; init; }
    public int BornServerId { get; init; }
    public int CurrentServerId { get; init; }
    public bool Online { get; init; }
    public int TitleId { get; init; }
    /// <summary>CharGuildDesc: id, name, emblem and the member's grade (0 without a guild).</summary>
    public long GuildId { get; init; }
    public string GuildName { get; init; } = "";
    public int GuildEmblem { get; init; }
    public int GuildGrade { get; init; }
    public IReadOnlyList<ViewCharItem> Items { get; init; } = [];
    public byte[] CustomizingData { get; init; } = [];
    /// <summary>Daevanion boards: board id and its open node count.</summary>
    public IReadOnlyList<(int Board, int Open)> Boards { get; init; } = [];
    /// <summary>ETitleEquipCategory (1..3) -> title id.</summary>
    public IReadOnlyList<(byte Category, int Title)> Titles { get; init; } = [];
    /// <summary>ECreatureType -> its board's slots (index, ECreatureOptionGrade).</summary>
    public IReadOnlyList<(byte Type, IReadOnlyList<(byte Slot, byte Grade)> Slots)> CreatureBoards { get; init; } = [];
    /// <summary>EContentsUnlockType values.</summary>
    public IReadOnlyList<byte> Unlocks { get; init; } = [];
    public long CombatPower { get; init; }
    /// <summary>Role vote counts by type.</summary>
    public IReadOnlyList<(int Type, int Count)> RoleVotes { get; init; } = [];
}

/// <summary>
/// Inspect another player: ViewChar_RQ 0x364f {target server id, target db id} -> ViewChar_RS 0x3650 (dev = live in the 0x36xx
/// family). OFFICIAL layout, decoded from one official reply (3,432 bytes, a level-45 character with 28 worn items): result u16 |
/// CharDesc (mask u8 0x07, nickname, pc_id i32, race u8, gender u8: as in 0x3645) | level i32 | conqueror level i32 | equip item
/// level i32 | born server u16 | current server u16 | online (packed bool) | title i32 | CharGuildDesc | items: varint count, each
/// {item id i32, maker, period expire i64, expired (packed bool), EquipItemDetail (as Common_Item), visual slot u8, skin i32, dye
/// slots (varint count), display / default mesh / override (packed bools)} | customizing (varint + bytes) | daevanion boards:
/// varint count, each {mask u8 0x01, board i32, open count u8} | equipped titles: varint count, {category u8, title i32} | creature
/// boards: varint count, {creature type u8, varint count, {slot u8, grade u8}} | contents unlocks: varint count, u8 each | combat
/// power i64 | role votes: varint count, {type i32, count i32}. The request body is not recorded (encrypted in the official
/// trace): server id u16 or i32 and db id i64 or varint are all taken (CANDIDATE).
/// </summary>
public static class InspectPackets
{
    public const ushort ViewCharRq = 0x364f, ViewCharRs = 0x3650;
    /// <summary>EResult Community_Fail_NotFoundCharacter.</summary>
    public const int NotFoundCharacter = 15371;

    /// <summary>ViewChar_RS: the character (an empty record with a failure result).</summary>
    public static byte[] ViewChar(int result, ViewCharInfo c)
    {
        var w = PacketWriter.Opcode(ViewCharRs).U16(result).U8(0x07).String(c.Name).I32(c.PcId).U8(c.Race).U8(c.Gender)
            .I32(c.Level).I32(c.ConquerorLevel).I32(c.EquipItemLevel).U16(c.BornServerId).U16(c.CurrentServerId).Bool(c.Online).I32(c.TitleId);
        // CharGuildDesc as the official reply carries it: id i64, name, emblem u16, grade u8, i32, i64, u16, string.
        w.I64(c.GuildId).String(c.GuildName).U16(c.GuildEmblem).U8(c.GuildId == 0 ? 0 : c.GuildGrade).I32(0).I64(0).U16(0).String("");
        w.Varint(c.Items.Count);
        foreach (var item in c.Items)
        {
            w.I32(item.ItemId).String(item.Maker).I64(item.PeriodExpireTime).Bool(item.PeriodExpired);
            GamePackets.WriteEquipItemDetail(w, item.Detail);
            w.U8(item.VisualSlot).I32(item.SkinId).Varint(0).Bool(item.Display).Bool(item.DefaultMesh).Bool(item.Override);
        }
        w.Bytes(c.CustomizingData).Varint(c.Boards.Count);
        foreach (var (board, open) in c.Boards) w.U8(1).I32(board).U8(Math.Clamp(open, 0, 255));
        w.Varint(c.Titles.Count);
        foreach (var (category, title) in c.Titles) w.U8(category).I32(title);
        w.Varint(c.CreatureBoards.Count);
        foreach (var (type, slots) in c.CreatureBoards)
        {
            w.U8(type).Varint(slots.Count);
            foreach (var (slot, grade) in slots) w.U8(slot).U8(grade);
        }
        w.Varint(c.Unlocks.Count);
        foreach (var unlock in c.Unlocks) w.U8(unlock);
        w.I64(c.CombatPower).Varint(c.RoleVotes.Count);
        foreach (var (type, count) in c.RoleVotes) w.I32(type).I32(count);
        return w.ToArray();
    }

    /// <summary>The ViewChar_RQ target: server id and character db id (every width the usmap allows; null when none fits).</summary>
    public static (int Server, long DbId)? ParseViewChar(ReadOnlySpan<byte> plain)
    {
        var body = plain.Length >= 2 ? plain[2..] : [];
        // u16 + i64, i32 + i64 (fixed widths), then u16 / i32 + varint.
        if (body.Length == 10) return (BitConverter.ToUInt16(body), BitConverter.ToInt64(body[2..]));
        if (body.Length == 12) return (BitConverter.ToInt32(body), BitConverter.ToInt64(body[4..]));
        foreach (var serverWidth in new[] { 2, 4 })
        {
            if (body.Length <= serverWidth) continue;
            var server = serverWidth == 2 ? BitConverter.ToUInt16(body) : BitConverter.ToInt32(body);
            if (TryVarint(body[serverWidth..], out var id, out var used) && used == body.Length - serverWidth) return (server, id);
        }
        return null;
    }

    static bool TryVarint(ReadOnlySpan<byte> data, out long value, out int used)
    {
        value = 0;
        for (used = 0; used < data.Length && used < 10; used++)
        {
            value |= (long)(data[used] & 0x7f) << (7 * used);
            if (data[used] < 0x80) { used++; return true; }
        }
        return false;
    }
}
