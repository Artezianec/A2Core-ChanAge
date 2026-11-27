using System;
using Aion.Protocol.Native;
using Xunit;

namespace Aion.Protocol.Tests;

/// <summary>
/// The 2.0.13 sniffer findings: opcodes that keep their 2.0.9 number must not be rewritten, and
/// the packets that did move must be written with their modern number directly.
/// </summary>
public sealed class ProtocolTranslation213Tests
{
    static ushort Op(byte[] packet) => (ushort)(packet[0] | packet[1] << 8);

    static byte[] Body(ushort opcode) => [(byte)opcode, (byte)(opcode >> 8), 0];

    static WireVersion Pin(WireVersion version)
    {
        var previous = WireContext.Current;
        WireContext.Current = version;
        return previous;
    }

    [Theory]
    // Movement keeps its 2.0.9 numbers on 2.0.13 (Parkour, ForwardAttackMove, Rolling,
    // TrailWindPath, Swimming).
    [InlineData(0x372C)]
    [InlineData(0x372D)]
    [InlineData(0x372E)]
    [InlineData(0x372F)]
    [InlineData(0x3730)]
    [InlineData(0x3732)]
    [InlineData(0x3733)]
    [InlineData(0x3734)]
    [InlineData(0x3736)]
    [InlineData(0x3737)]
    [InlineData(0x3739)]
    [InlineData(0x373A)]
    // Skills / combat keep theirs (SkillLevelUpdate, Abnormal_Move_*, ChainSkill*, SkillCharging*,
    // SkillAutoLoad, BasicAtk) plus LockOnTarget_RQ.
    [InlineData(0x3813)]
    [InlineData(0x3818)]
    [InlineData(0x381C)]
    [InlineData(0x3821)]
    [InlineData(0x3824)]
    [InlineData(0x3828)]
    [InlineData(0x3830)]
    // Skill projectiles and auto-use settings keep theirs.
    [InlineData(0x385B)]
    [InlineData(0x385C)]
    [InlineData(0x385E)]
    [InlineData(0x385F)]
    public void Kept_opcodes_are_not_rewritten_on_2_0_13(ushort opcode)
    {
        var previous = Pin(WireVersion.V213);
        try
        {
            Assert.Equal(opcode, Op(ProtocolTranslation.ToClient(Body(opcode))));
            Assert.Equal(opcode, Op(ProtocolTranslation.ToServer(Body(opcode))));
        }
        finally { WireContext.Current = previous; }
    }

    [Fact]
    public void Airspace_battle_is_dispatched_by_its_modern_number_and_moves_on_the_wire()
    {
        var previous = Pin(WireVersion.V213);
        try
        {
            // Inbound modern number is left for the handler's own modern dispatch.
            Assert.Equal(0x4300, Op(ProtocolTranslation.ToServer(Body(0x4300))));
            Assert.Equal(0x430E, Op(ProtocolTranslation.ToServer(Body(0x430E))));
            // Outbound handler number -> the modern client's number, emitted by the writer.
            Assert.Equal(0x4300, ProtocolTranslation.ForModern(0x372C));
            Assert.Equal(0x430E, ProtocolTranslation.ForModern(0x373A));
            // ToClient no longer rewrites the movement packets.
            Assert.Equal(0x372C, Op(ProtocolTranslation.ToClient(Body(0x372C))));
        }
        finally { WireContext.Current = previous; }
    }

    [Fact]
    public void Distribution_is_dispatched_by_its_modern_number_and_moves_on_the_wire()
    {
        var previous = Pin(WireVersion.V213);
        try
        {
            Assert.Equal(0x5718, Op(ProtocolTranslation.ToServer(Body(0x5718))));
            Assert.Equal(0x5722, Op(ProtocolTranslation.ToServer(Body(0x5722))));
            Assert.Equal(0x5735, Op(ProtocolTranslation.ToServer(Body(0x5735))));
            Assert.Equal(0x5718, ProtocolTranslation.ForModern(0x3813));
            Assert.Equal(0x5735, ProtocolTranslation.ForModern(0x3830));
            Assert.Equal(0x3813, Op(ProtocolTranslation.ToClient(Body(0x3813))));
        }
        finally { WireContext.Current = previous; }
    }

    [Fact]
    public void Distribution_packets_write_the_2_0_13_opcodes_on_modern_and_2_0_9_otherwise()
    {
        var previous = Pin(WireVersion.V213);
        try
        {
            Assert.Equal(0x5718, Op(DistributionPackets.BeginItemBidding(1, 2, 3, 4)));
            Assert.Equal(0x5723, Op(DistributionPackets.RollDiceReply(0, 1, 2)));
            Assert.Equal(0x5735, Op(DistributionPackets.RewardDistributionResult(0, "", 0)));
        }
        finally { WireContext.Current = previous; }

        previous = Pin(WireVersion.V209);
        try
        {
            Assert.Equal(0x3813, Op(DistributionPackets.BeginItemBidding(1, 2, 3, 4)));
            Assert.Equal(0x381E, Op(DistributionPackets.RollDiceReply(0, 1, 2)));
            Assert.Equal(0x3830, Op(DistributionPackets.RewardDistributionResult(0, "", 0)));
            // 2.0.9 keeps the old number even through the reply rewrite.
            Assert.Equal(0x3813, Op(ProtocolTranslation.ToClient(Body(0x3813))));
        }
        finally { WireContext.Current = previous; }
    }

    [Fact]
    public void Abyss_rift_is_dispatched_by_its_modern_number_and_moves_on_the_wire()
    {
        var previous = Pin(WireVersion.V213);
        try
        {
            Assert.Equal(0x6302, Op(ProtocolTranslation.ToServer(Body(0x6302))));
            Assert.Equal(0x6306, Op(ProtocolTranslation.ToServer(Body(0x6306))));
            Assert.Equal(0x6302, ProtocolTranslation.ForModern(0x385B));
            Assert.Equal(0x6306, ProtocolTranslation.ForModern(0x385F));
            Assert.Equal(0x385B, Op(ProtocolTranslation.ToClient(Body(0x385B))));
        }
        finally { WireContext.Current = previous; }
    }

    [Fact]
    public void World_map_abyss_rankers_and_ranking_points_use_the_2_0_13_numbers()
    {
        var previous = Pin(WireVersion.V213);
        try
        {
            Assert.Equal(0x910B, Op(ProtocolTranslation.ToClient(Body(0x3A17))));
            Assert.Equal(0x910C, Op(ProtocolTranslation.ToClient(Body(0x3A18))));
            Assert.Equal(0xE273, Op(ProtocolTranslation.ToClient(Body(0x3B78))));
            Assert.Equal(0x3A17, Op(ProtocolTranslation.ToServer(Body(0x910B))));
            Assert.Equal(0x3A18, Op(ProtocolTranslation.ToServer(Body(0x910C))));
        }
        finally { WireContext.Current = previous; }
    }

    [Fact]
    public void Two_0_9_keeps_every_translated_number()
    {
        var previous = Pin(WireVersion.V209);
        try
        {
            Assert.Equal(0x3813, Op(ProtocolTranslation.ToClient(Body(0x3813))));
            Assert.Equal(0x372C, Op(ProtocolTranslation.ToClient(Body(0x372C))));
            Assert.Equal(0x372C, ProtocolTranslation.ForModern(0x372C));
            Assert.Equal(0x3813, ProtocolTranslation.ForModern(0x3813));
            // The 2.0.9 wire still carries the 2.0.9 numbers for the packets that moved on modern.
            Assert.Equal(0x3A18, ProtocolTranslation.ForModern(0x3A18));
            Assert.Equal(0x3B78, ProtocolTranslation.ForModern(0x3B78));
        }
        finally { WireContext.Current = previous; }
    }

    [Fact]
    public void Load_complete_rs_encodes_the_observed_2_0_13_layout()
    {
        var packet = GamePackets.ServerEnterMapPos(new Vector3d(1, 2, 3), charKey: 11944, ticket: 0, frame: 2);
        Assert.Equal(0xD79E, Op(packet));
        Assert.Equal(26, packet.Length);
        Assert.Equal(1f, BitConverter.ToSingle(packet, 2));
        Assert.Equal(2f, BitConverter.ToSingle(packet, 6));
        Assert.Equal(3f, BitConverter.ToSingle(packet, 10));
        var key = NativeVarint.Read(packet, 14, out var at);
        Assert.Equal(11944u, key);
        Assert.Equal(0, BitConverter.ToUInt16(packet, at));
        at += 2;
        Assert.Equal(0, BitConverter.ToInt32(packet, at));
        at += 4;
        Assert.Equal(2, BitConverter.ToInt32(packet, at));
    }
}
