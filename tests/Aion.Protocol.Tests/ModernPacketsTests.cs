using System;
using Aion.Protocol.Native;
using Xunit;

namespace Aion.Protocol.Tests;

/// <summary>Wire layouts of the 2.0.13 packets the 2.0.9 tables lack.</summary>
public sealed class ModernPacketsTests
{
    static ushort Op(byte[] packet) => (ushort)(packet[0] | packet[1] << 8);

    static WireVersion Pin(WireVersion version)
    {
        var previous = WireContext.Current;
        WireContext.Current = version;
        return previous;
    }

    [Fact]
    public void Kina_and_gather_limit_status_are_i64_pairs()
    {
        var kina = ModernPackets.KinaLimitStatus(11944, 1_700_000_000_000);
        Assert.Equal(0xFFA6, Op(kina));
        Assert.Equal(18, kina.Length);
        Assert.Equal(11944L, BitConverter.ToInt64(kina, 2));
        Assert.Equal(1_700_000_000_000L, BitConverter.ToInt64(kina, 10));

        var initKina = ModernPackets.InitKinaLimitStatus(0, 5);
        Assert.Equal(0xFFA5, Op(initKina));
        Assert.Equal(18, initKina.Length);

        var gather = ModernPackets.GatherLimitStatus(7, 9);
        Assert.Equal(0xFFA8, Op(gather));
        Assert.Equal(18, gather.Length);
        Assert.Equal(7L, BitConverter.ToInt64(gather, 2));

        var initGather = ModernPackets.InitGatherLimitStatus(3, 4);
        Assert.Equal(0xFFA7, Op(initGather));
        Assert.Equal(18, initGather.Length);
    }

    [Fact]
    public void Account_currency_limit_status_lists_the_three_observed_categories()
    {
        var packet = ModernPackets.AccountCurrencyLimitStatus(1_700_000_000_000);
        Assert.Equal(0xFFAE, Op(packet));
        Assert.Equal(26, packet.Length);
        Assert.Equal(3u, NativeVarint.Read(packet, 2, out var at));
        Assert.Equal(1, packet[at]);
        Assert.Equal(2, packet[at + 5]);
        Assert.Equal(4, packet[at + 10]);
        Assert.Equal(1_700_000_000_000L, BitConverter.ToInt64(packet, at + 15));
    }

    [Fact]
    public void Combat_state_replies_are_result_u16()
    {
        var on = ModernPackets.CombatStateOnReply();
        Assert.Equal(0x383B, Op(on));
        Assert.Equal(4, on.Length);
        Assert.Equal(0, BitConverter.ToUInt16(on, 2));

        var off = ModernPackets.CombatStateOffReply(1);
        Assert.Equal(0x383D, Op(off));
        Assert.Equal(1, BitConverter.ToUInt16(off, 2));
    }

    [Fact]
    public void Skill_casting_stop_carries_key_result_skill_and_cooltime()
    {
        var packet = ModernPackets.SkillCastingStop(11944, 0, 14030000, 300);
        Assert.Equal(0x380C, Op(packet));
        Assert.Equal(12, packet.Length);
        Assert.Equal(11944u, NativeVarint.Read(packet, 2, out var at));
        Assert.Equal(0, BitConverter.ToUInt16(packet, at));
        Assert.Equal(14030000, BitConverter.ToInt32(packet, at + 2));
        Assert.Equal(300u, NativeVarint.Read(packet, at + 6, out _));
    }

    [Fact]
    public void Reward_cube_ceiling_is_a_varint_and_six_byte_rows()
    {
        var packet = ModernPackets.OdEnergyCubeCeilingInfo([(600004, (byte)3, (byte)2)]);
        Assert.Equal(0x5712, Op(packet));
        Assert.Equal(9, packet.Length);
        Assert.Equal(1u, NativeVarint.Read(packet, 2, out var at));
        Assert.Equal(600004, BitConverter.ToInt32(packet, at));
    }

    [Fact]
    public void Gather_source_result_matches_the_official_success_bytes()
    {
        // ingame-3.a2p #4034: 34 8d ee 59 00 3a fb fe 01 = player 11502, result 0x3A00, node 32635.
        var result = ModernPackets.BroadcastGatherSourceResult(11502, ModernPackets.GatherResultSuccess, 32635);
        Assert.Equal(0x8D34, Op(result));
        Assert.Equal(new byte[] { 0x34, 0x8D, 0xEE, 0x59, 0x00, 0x3A, 0xFB, 0xFE, 0x01 }, result);
        Assert.Equal(0x3A00, ModernPackets.GatherResultSuccess);
        Assert.Equal(0x3A02, ModernPackets.GatherResultFail);

        var result2 = ModernPackets.BroadcastGatherSourceResult(11944, 0, 7);
        Assert.Equal(11944u, NativeVarint.Read(result2, 2, out var at));
        Assert.Equal(0, BitConverter.ToUInt16(result2, at));
        Assert.Equal(7u, NativeVarint.Read(result2, at + 2, out _));

        var combo = ModernPackets.GatherComboStart(4, 1_700_000_000_000);
        Assert.Equal(0x8D3E, Op(combo));
        Assert.Equal(14, combo.Length);
        Assert.Equal(4, BitConverter.ToInt32(combo, 2));
        Assert.Equal(1_700_000_000_000L, BitConverter.ToInt64(combo, 6));
    }

    [Fact]
    public void Guild_craft_and_post_notice_empty_forms_are_count_zero()
    {
        var craft = ModernPackets.GuildCraftRecipeList();
        Assert.Equal(0x8D83, Op(craft));
        Assert.Equal(new byte[] { 0x83, 0x8D, 0x00 }, craft);

        var notice = ModernPackets.RetrievePostNoticeInfo();
        Assert.Equal(0xE34C, Op(notice));
        Assert.Equal(new byte[] { 0x4C, 0xE3, 0x00 }, notice);

        var full = ModernPackets.RetrievePostNoticeInfo([new ModernPackets.PostNotice(1, "s", "t", "b", 2, 3, [], 1)]);
        Assert.Equal(0xE34C, Op(full));
        Assert.Equal(1u, NativeVarint.Read(full, 2, out _));
    }

    [Fact]
    public void Abyss_rift_progress_result_is_0x385D_on_2_0_9_and_0x6304_on_2_0_13()
    {
        var previous = Pin(WireVersion.V213);
        try { Assert.Equal(0x6304, Op(ModernPackets.AbyssRiftProgressResult(1))); }
        finally { WireContext.Current = previous; }

        previous = Pin(WireVersion.V209);
        try
        {
            var legacy = ModernPackets.AbyssRiftProgressResult(1);
            Assert.Equal(0x385D, Op(legacy));
            Assert.Equal(1, BitConverter.ToInt32(legacy, 2));
        }
        finally { WireContext.Current = previous; }
    }
}
