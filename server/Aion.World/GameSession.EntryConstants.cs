using Aion.Protocol.Native;

namespace Aion.World;

// Small state messages the official world entry carries for a new character (capture 20261013-144836, entries #183-#463),
// sent with their OBSERVED bytes in the observed order. Names from the catalogue runs (0x54ca / 0xdd07); the ones named
// '?' sit in runs the catalogue does not align with. Each is a list or flag set that is empty or default for a character
// with nothing yet: trade server info, the previous season's missions, godstones, Daevanion points and boards, contents
// control, the arena ranking (1500 points per type), subscriptions, account contents, admin feature flags, the shop
// tutorial, message cards, the world-chat free chance (its time is now), dungeon kill records.
public sealed partial class GameSession
{
    static byte[] Observed(ushort opcode, string hex) => [.. PacketWriter.Opcode(opcode).ToArray(), .. Convert.FromHexString(hex)];

    /// <summary>OURS: MonolithList_NT from this level (recorded absent at levels 1 and 9, present at 23, 44, 45; CANDIDATE: the level-10 unlocks).</summary>
    public const int MonolithListLevel = 10;

    /// <summary>
    /// MonolithList_NT 0xe30b {list of {monolith id i32, resonate count i32}}: three monoliths by race, OBSERVED in all 27 entries
    /// at level 23+ (Light 1001, 1002, 3001; Dark 2001, 2002, 4001). The counts are per character there (0-560); sent at 0 (OURS).
    /// </summary>
    internal static byte[] MonolithList(int race)
    {
        int[] ids = race == 2 ? [2001, 2002, 4001] : [1001, 1002, 3001];
        var w = PacketWriter.Opcode(0xe30b).Varint(ids.Length);
        foreach (var id in ids) w.I32(id).I32(0);
        return w.ToArray();
    }

    /// <summary>
    /// ArenaMatchingInfo_NT 0x400a, by race: Light 010102230501290502 (the level-1 entry 2026-10-28), Dark 010102630901650902
    /// (the level-1 entries 2026-11-04 and the Dark characters at 45). OBSERVED; their meaning is not decoded.
    /// </summary>
    internal static byte[] ArenaMatchingInfo(int race) => Observed(0x400a, race == 2 ? "010102630901650902" : "010102230501290502");

    /// <summary>
    /// The observed constants of the entry burst (none depends on the character but the world-chat time, the account contents,
    /// the membership, the race and the level). The messages marked 827b359 are added in their recorded places (Addiboii/A2Core
    /// 827b359, the 34 world entries of its recordings, 2026-10-13 to 11-06); the rest of our entry order is unchanged.
    /// </summary>
    static List<byte[]> EntryConstants(long now, byte[] accountContents, byte[] subscribeList, int race = 1, int level = 1)
    {
        var replies = new List<byte[]>
        {
            Observed(0xe263, "023f005300"),            // TradeServerInfo_NT
            Observed(0xe224, "02010202"),              // ContentsControl_NT
            Observed(0xe32a, "0301dc05000000000000000000000002dc05000000000000000000000003dc050000000000000000000000"),   // ArenaRanking_NT: Single/Team/Strategy at 1500
            Observed(0x9331, "00"),                    // ? (0x9331, after BookmarkedContentList_NT; 827b359)
            subscribeList,                             // SubscribeList_NT (`00` without a membership)
            Observed(0xe33e, "00"),                    // ? (0xe33e)
            PacketWriter.Opcode(0xffae).U8(0).I64(now).ToArray(),   // ? (0xffae): none, a time (OBSERVED 00 + the server time at level 1; 827b359)
            Observed(0x6103, "00"),                    // ? (0x6103)
        };
        if (level >= MonolithListLevel) replies.Add(MonolithList(race));   // MonolithList_NT 0xe30b (827b359)
        replies.AddRange(
        [
            accountContents,                           // AccountContentsInfoList_NT (`00` without keyed attendance seasons)
            PacketWriter.Opcode(0xffa5).I64(0).I64(now).ToArray(),  // ? (0xffa5): 0, a time (OBSERVED at level 1; 827b359)
            PacketWriter.Opcode(0xffa7).I64(0).I64(now).ToArray(),  // ? (0xffa7): 0, a time (OBSERVED at level 1; 827b359)
            TrustLimitInfoNotice(now),                 // TrustLimitInfo_NT 0xe262 (827b359; GameSession.Shop.NP.cs)
            Observed(0xe25c, "014b01"),                // ContentsFeatureOnOffListByAdmin_NT
            Observed(0x572d, "00"),                    // ? (0x572d; 00 in all 34; 827b359)
            Observed(0xe251, "00"),                    // BMShopTutorialInfo_NT
            ArenaMatchingInfo(race),                   // ArenaMatchingInfo_NT 0x400a (827b359)
            Observed(0xe200, "00002392ffffffffff000000"),   // MessageCardInitData_NT
            // ? (0xffab): i64 0, i64 3000000 (OBSERVED 2.0.6, 31 of 31; the first value 0 for the new characters; 827b359).
            PacketWriter.Opcode(0xffab).I64(0).I64(3_000_000).ToArray(),
            PacketWriter.Opcode(0xe223).I64(now).I32(0).ToArray(),   // WorldChatFreeChance_NT (observed: a time then four zero bytes)
            FieldEventInfo(race, now),                 // FieldEventInfo_NT: the Shugo festival events running now ("00" when none)
            Observed(0x570f, "000000"),                // MonsterCubeRewardList_RS (the usmap position, 0x47 above the old base; 0x6108 is DungeonClearKillNpc_NT)
        ]);
        return replies;
    }
}
