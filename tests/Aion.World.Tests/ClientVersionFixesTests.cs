using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>2.0.12 field fixes verified against the client wire specs (see docs/TODO_TASK.md).</summary>
public sealed class ClientVersionFixesTests
{
    static ushort Op(byte[] packet) => BitConverter.ToUInt16(packet, 0);

    [Fact]
    public void Character_created_result_is_u16_before_the_lobby_desc()
    {
        var packet = GamePackets.CharacterCreated("Name", 1, 2, 1, 0x1234, 0x5678, [1, 2, 3], 4, 5, 1000);
        Assert.Equal(0x361C, Op(packet));
        Assert.Equal(0, BitConverter.ToUInt16(packet, 2));
        Assert.Equal(0x1234, BitConverter.ToInt32(packet, 4));   // Common_LobbyCharDesc._key
    }

    [Fact]
    public void Lobby_customizing_list_uses_the_2_0_12_opcode_and_u16_result()
    {
        var packet = GamePackets.CharCustomizingList(0, [(1, [9, 8], 2, 3)]);
        Assert.Equal(0x569E, Op(packet));
        Assert.Equal(0, BitConverter.ToUInt16(packet, 2));
        Assert.Equal(1, packet[4]);          // varint count
        Assert.Equal(1, packet[5]);          // slot
        Assert.Equal(2, packet[6]);          // customizing blob length
        Assert.Equal(9, packet[7]);
        Assert.Equal(2, packet[9]);          // voice
    }

    [Fact]
    public void Broadcast_customizing_uses_the_2_0_12_opcode_and_varint_key()
    {
        var packet = GamePackets.BroadcastCharCustomizingUpdate(0x1234, [1], 2, 3);
        Assert.Equal(0x56AB, Op(packet));
        Assert.Equal(0xB4, packet[2]);       // 0x1234 LEB128 low byte
        Assert.Equal(0x24, packet[3]);       // high byte
    }

    [Fact]
    public void Extended_character_slot_is_0x910a() =>
        Assert.Equal(0x910A, Op(GamePackets.ExtendedCharacterSlot(2)));

    [Fact]
    public void Party_leave_notice_is_0x931f() =>
        Assert.Equal(0x931F, SocialOpcodes.PartyLeaveNt);

    [Fact]
    public void Respawn_request_reads_the_2_0_12_has_item_and_type()
    {
        var uid = 2199023958545L;
        var withItem = new byte[12];
        withItem[0] = 0x22; withItem[1] = 0x8D;
        withItem[2] = 1; withItem[3] = 3;   // has_item, ERespawnType.Item
        BitConverter.GetBytes(uid).CopyTo(withItem, 4);
        Assert.Equal(((byte)3, uid), GameSession.ParseRespawnRequest(withItem));

        var auto = new byte[] { 0x22, 0x8D, 0, 2 };
        Assert.Equal(((byte)2, 0L), GameSession.ParseRespawnRequest(auto));

        var legacy = new byte[11];
        legacy[0] = 0x22; legacy[1] = 0x8D; legacy[2] = 3;
        BitConverter.GetBytes(uid).CopyTo(legacy, 3);
        Assert.Equal(((byte)3, uid), GameSession.ParseRespawnRequest(legacy));
    }

    [Fact]
    public void Quest_accept_reads_the_flag_quest_and_optional_npc()
    {
        var withNpc = new byte[11];
        withNpc[0] = 0x0A; withNpc[1] = 0x8D;
        withNpc[2] = 1;                                   // npc present
        BitConverter.GetBytes(1100010).CopyTo(withNpc, 3);
        BitConverter.GetBytes(880301).CopyTo(withNpc, 7);
        Assert.Equal((1100010, 880301), GameSession.ParseQuestAcceptRequest(withNpc));

        var withoutNpc = new byte[7];
        withoutNpc[0] = 0x0A; withoutNpc[1] = 0x8D;
        BitConverter.GetBytes(1100010).CopyTo(withoutNpc, 3);
        Assert.Equal((1100010, 0), GameSession.ParseQuestAcceptRequest(withoutNpc));

        var legacy = new byte[10];
        legacy[0] = 0x0A; legacy[1] = 0x8D;
        BitConverter.GetBytes(1100010).CopyTo(legacy, 2);
        BitConverter.GetBytes(880301).CopyTo(legacy, 6);
        Assert.Equal((1100010, 880301), GameSession.ParseQuestAcceptRequest(legacy));
    }

    [Fact]
    public void Warp_leave_has_the_2_0_12_flag_byte_and_trailing_duration()
    {
        var at = new Vector3d(1, 2, 3);
        var packet = PlayerPackets.WarpLeave(5, at, 90, 0, 0, 1, effectId: 7);
        Assert.Equal(0x373C, Op(packet));
        Assert.Equal(5, packet[2]);          // key varint
        Assert.Equal(2, packet[3]);          // flags: effect present
        Assert.Equal(1f, BitConverter.ToSingle(packet, 4));   // position
        Assert.Equal(28, packet.Length);
        var durationAt = packet.Length - 1;
        Assert.Equal(0, packet[durationAt]); // duration varint
    }

    [Fact]
    public void Party_loot_uses_the_2_0_12_distribution_opcodes()
    {
        Assert.Equal(0x5722, DistributionOpcodes.RollDiceRq);
        Assert.Equal(0x5723, DistributionOpcodes.RollDiceRs);
        Assert.Equal(0x5719, DistributionOpcodes.BidItemRq);
        Assert.Equal(0x571A, DistributionOpcodes.BidItemRs);
        Assert.NotEqual(GameSession.SkillChargingEndRq, DistributionOpcodes.RollDiceRq);
        Assert.Equal(0x5723, Op(DistributionPackets.RollDiceReply(0, 1, 50)));
        Assert.Equal(0x571A, Op(DistributionPackets.BidItemReply(0, 1, 50)));
    }

    [Fact]
    public void Trade_uses_the_2_0_12_opcodes()
    {
        Assert.Equal(0xE264, GameSession.TradeSellRq);
        Assert.Equal(0xE265, GameSession.TradeSellRs);
        Assert.Equal(0xE266, GameSession.TradeCancelSaleRq);
        Assert.Equal(0xE26C, GameSession.TradePurchaseRq);
        Assert.Equal(0xE26E, GameSession.TradeSoldNt);
        Assert.Equal(0xE271, GameSession.TradeGetSummaryRq);   // CANDIDATE: RS is 0xE272
        Assert.Equal(0xE272, GameSession.TradeGetSummaryRs);
    }

    [Fact]
    public void Godstone_and_duel_use_the_2_0_12_opcodes()
    {
        Assert.Equal(0x5634, GameSession.GodstoneUpdateNt);
        Assert.Equal(0x5635, GameSession.ItemEnchantGodstoneRq);
        Assert.Equal(0x5636, GameSession.ItemEnchantGodstoneRs);
        Assert.Equal(0x5637, GameSession.ItemEnchantGodstoneNt);
        Assert.Equal(0x5638, GameSession.CombineGodstoneRq);
        Assert.Equal(0x9400, SocialOpcodes.DuelCallOutRq);
        Assert.Equal(0x9409, SocialOpcodes.DuelStateUpdateNt);
        Assert.Equal(0x940E, SocialOpcodes.DuelCancelNt);
    }

    [Fact]
    public void Monolith_uses_the_2_0_12_opcodes_and_info_flag()
    {
        Assert.Equal(0xE30B, GamePackets.MonolithListNt);
        Assert.Equal(0xE30C, GamePackets.MonolithInfoNt);
        Assert.Equal(0xE30D, GamePackets.MonolithResonateRq);
        Assert.Equal(0xE30E, GamePackets.MonolithResonateRs);
        Assert.Equal(0xE341, GamePackets.MonolithUnlockNt);
        Assert.Equal(0xFFAC, GamePackets.SyncMonolithAccountAchievementsRq);
        var info = GamePackets.MonolithInfo(7, 3, 0x1234);
        Assert.Equal(0xE30C, Op(info));
        Assert.Equal(1, info[2]);                        // env key present
        Assert.Equal(7, BitConverter.ToInt32(info, 3));
        Assert.Equal(3, BitConverter.ToInt32(info, 7));
        Assert.Equal(0xB4, info[11]);                    // varint 0x1234
        Assert.Equal(0x24, info[12]);
    }

    [Fact]
    public void Arena_and_matching_use_the_2_0_12_opcodes()
    {
        Assert.Equal(0xE30F, GameSession.MatchingStateChangeNt);
        Assert.Equal(0xE310, GameSession.MatchingStartRq);
        Assert.Equal(0xE312, GameSession.MatchingStartResponseRq);
        Assert.Equal(0xE314, GameSession.MatchingCancelRq);
        Assert.Equal(0xE31A, GameSession.ArenaDungeonReadyRq);
        Assert.Equal(0xE325, GameSession.ArenaDungeonKillNt);
        Assert.Equal(0xE32B, GameSession.ArenaRecordRq);
        Assert.Equal(0xE32C, GameSession.ArenaRecordRs);
    }

    [Fact]
    public void Airspace_abyss_world_map_and_reward_limits_use_the_2_0_12_opcodes()
    {
        Assert.Equal(0x4300, GameSession.AirspaceBattleRq);
        Assert.Equal(0x430C, GameSession.CharAirspacePointNt);
        Assert.Equal(0x6302, GameSession.AbyssRiftProgressInfoRq);
        Assert.Equal(0x9100, GameSession.WorldMapContentsInfoRq);
        Assert.Equal(0x910B, GameSession.WorldMapAbyssRankerInfoRq);
        Assert.Equal(0xE273, GameSession.RankingPointUpdateNt);
        Assert.Equal(0xFFAB, GameSession.AbyssPointUpdateNt);
        Assert.Equal(0xFFA9, GameSession.RewardLimitUpdateNt);
        Assert.Equal(0x9322, GameSession.DungeonCoolTimesNt);
        Assert.Equal(0xE333, GameSession.RefreshCurrencyNt);
    }

    [Fact]
    public void Force_achievements_and_skill_customizing_use_the_2_0_12_opcodes()
    {
        Assert.Equal(0x9600, SocialOpcodes.ForceCreateRq);
        Assert.Equal(0x9624, SocialOpcodes.ForceMemberItemLootingNt);
        Assert.Equal(0x9633, SocialOpcodes.ForceJoinRq);
        Assert.Equal(0xE32F, GameSession.CloseNpcAchievementRq);
        Assert.Equal(0xE332, GameSession.CloseEnvObjAchievementRs);
        Assert.Equal(0x3845, GameSession.SkillCustomizingUpdateRq);   // CANDIDATE: RS is 0x3846
        Assert.Equal(0x3846, GameSession.SkillCustomizingUpdateRs);
        Assert.Equal(0xE500, GameSession.DaevanionOpenAllNodeRq);
        Assert.Equal(0xE501, GameSession.DaevanionOpenAllNodeRs);
    }

    [Fact]
    public void Other_character_appearance_sets_the_ride_and_vessel_mask_bits()
    {
        var packet = PlayerPackets.CharAppear(new OtherCharacter
        {
            Key = 1, Name = "Rider", RideDataId = 11, VesselKey = 22, DefaultRideDataId = 33,
        });
        Assert.Equal(0x3645, Op(packet));
        var mask = BitConverter.ToUInt32(packet, 3);   // opcode 2 + key varint (1)
        Assert.True((mask & 0x8000) != 0, "ride data gate");
        Assert.True((mask & 0x10000) != 0, "vessel gate");
        Assert.True((mask & 0x800000) != 0, "default ride gate");
    }

    [Fact]
    public void Customizing_status_metadata_and_settlement_packets_match_2_0_12()
    {
        Assert.Equal(0x56A0, Op(GamePackets.CharCustomizingStatusCheckReply()));
        Assert.Equal(0x56A6, Op(GamePackets.CharCustomizingMetaDataReply()));
        Assert.Equal(0x56A8, Op(GamePackets.CharCustomizingSettlementStatusReply()));
        var settle = GamePackets.CharCustomizingSettleReply(3);
        Assert.Equal(0x56AA, Op(settle));
        Assert.Equal(3, BitConverter.ToInt32(settle, 4));
    }

    [Fact]
    public void Party_dungeon_room_family_uses_the_2_0_12_opcodes()
    {
        Assert.Equal(0x9323, GameSession.PartyDungeonEnterRq);
        Assert.Equal(0x9330, GameSession.BookmarkedContentListNt);
        Assert.Equal(0x9700, GameSession.DungeonRoomListRq);
        Assert.Equal(0x9703, GameSession.DungeonRoomCreateRq);
        Assert.Equal(0x9708, GameSession.DungeonRoomApplyResponseRq);
        Assert.Equal(0x9714, GameSession.DungeonRoomReadyRq);
        Assert.Equal(0x9717, GameSession.DungeonRoomStartRq);
        Assert.Equal(0x9720, GameSession.DungeonRoomEditSettingRq);
        Assert.Equal(0x9734, GameSession.DungeonRoomRetryFailNt);
        Assert.Equal(0x9333, GameSession.PartyDungeonClearNt);
    }
}
