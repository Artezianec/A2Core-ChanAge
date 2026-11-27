namespace Aion.Protocol.Native;

/// <summary>A drop on the ground (Common_DropItemPS): source entity, its place, the item's spot, uid, item, count, owner, expiry, race.</summary>
public sealed record DropItem(int Key, Vector3d Center, Vector3d At, long Uid, int ItemId, int Count, long Owner, long OwnershipExpireTime, byte OwnerRace,
    byte OwnerType = 2, byte DropType = 1);

/// <summary>
/// Monster loot (catalogue 340-343 + 0x54ca, the 0x56 run): 0x561e DropItem_NT, 0x561f DropItemRemove_NT,
/// 0x5620 ItemLooting_RQ, 0x5621 ItemLooting_RS. Usmap DevPacketData_GameServer_ItemLooting_RS (_result wire u16
/// OBSERVED, _drop_item_uid i64, _is_auto_looting bool). Successful pickup (capture): 0x5621 result 0 | uid | auto off,
/// then 0x561f DropItemRemove_NT, 0x561b ItemModificationList_NT, 0x5616 ItemUse_NT when auto-use applies.
/// Batches (OBSERVED, AVION2 captures: 1,498 drops, 1,457 takes, 425 own pick-ups): a u8 count, fixed-size records back
/// to back, one byte after the batch. Every record starts with the source, the entity the item dropped from (u32, shared by
/// all drops of one corpse or chest), so drops are joined by their uid, never by the source.
/// </summary>
public static class LootPackets
{
    public const ushort DropNt = 0x561e, DropRemoveNt = 0x561f, LootRq = 0x5620, LootRs = 0x5621;
    /// <summary>EDropNotifyReason Create; EDropItemOwnerType Player; EDropType Ground.</summary>
    public const byte Create = 1;

    /// <summary>
    /// 0x561e DropItem_NT: count u8, then 63 bytes per drop: source u32 | source position | item spot | uid i64 | item u32 |
    /// count u32 | owner type u8 | owner i64 (character, server in the top 16 bits) | ownership expiry i64 | drop type u8 |
    /// race u8; then EDropNotifyReason.
    /// </summary>
    public static byte[] DropItems(IReadOnlyList<DropItem> drops, byte reason = Create)
    {
        if (drops.Count > byte.MaxValue) throw new ArgumentException("At most 255 drops in one batch", nameof(drops));
        var w = PacketWriter.Opcode(DropNt).U8(drops.Count);
        foreach (var d in drops)
            w.U32((uint)d.Key).Vec3(d.Center).Vec3(d.At).I64(d.Uid).I32(d.ItemId).I32(d.Count).U8(d.OwnerType).I64(d.Owner)
                .I64(d.OwnershipExpireTime).U8(d.DropType).U8(d.OwnerRace);
        return w.U8(reason).ToArray();
    }

    /// <summary>
    /// 0x561f DropItemRemove_NT. 2.0.9: count u8, 20 bytes per drop (source u32 | uid i64 | taker dbid i64), then one
    /// auto-looting byte for the batch. 2.0.13: 21 bytes per drop, the bool on each record (OBSERVED SDK
    /// Common_DropItemSmall); one 24-byte packet per drop through <see cref="DropsRemovedPackets"/>.
    /// </summary>
    public static byte[] DropsRemoved(IReadOnlyList<(int Key, long Uid, long CharDbId, bool Auto)> removed)
    {
        if (removed.Count > byte.MaxValue) throw new ArgumentException("At most 255 drops in one batch", nameof(removed));
        var w = PacketWriter.Opcode(DropRemoveNt).U8(removed.Count);
        if (WireContext.Current == WireVersion.V213)
        {
            foreach (var (key, uid, dbid, auto) in removed) w.U32((uint)key).I64(uid).I64(dbid).Bool(auto);
            return w.ToArray();
        }
        foreach (var (key, uid, dbid, _) in removed) w.U32((uint)key).I64(uid).I64(dbid);
        return w.U8(removed.Any(r => r.Auto) ? 1 : 0).ToArray();
    }

    /// <summary>
    /// Removal frames for a batch: 2.0.13 sends one 24-byte 0x561f per removed drop (all 92 official 2.0.13
    /// frames are count 1); 2.0.9 and 2.0.12 keep the single batched frame.
    /// </summary>
    public static List<byte[]> DropsRemovedPackets(IReadOnlyList<(int Key, long Uid, long CharDbId, bool Auto)> removed)
    {
        if (WireContext.Current != WireVersion.V213) return [DropsRemoved(removed)];
        var packets = new List<byte[]>(removed.Count);
        foreach (var (key, uid, dbid, auto) in removed)
            packets.Add(PacketWriter.Opcode(DropRemoveNt).U8(1).U32((uint)key).I64(uid).I64(dbid).Bool(auto).ToArray());
        return packets;
    }

    /// <summary>0x5621 ItemLooting_RS: result u16 | drop uid i64 | auto looting bool.</summary>
    public static byte[] LootReply(int result, long uid, bool auto) => PacketWriter.Opcode(LootRs).U16(result).I64(uid).Bool(auto).ToArray();
}
