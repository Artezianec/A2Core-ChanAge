using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class OfficialSummonSprintWireTests
{
    static readonly Vector3d Pos = new(21125, -26425, 4838);
    const int OwnerKey = 15643;

    [Fact]
    public void Summon_appear_uses_official_mask_stats_and_parent_u32()
    {
        var appear = new NpcAppearance(37490, 2920101, Pos, 310.78, 958, 958, 25, 22, true, false,
            MoveState: NpcAppearance.FlyingOnAirStop, MoveSubstate: 2, Reason: NpcAppearance.NpcSpawnReason,
            SpawnAct: GamePackets.SpiritSpawnActId,
            Abnormals: [new AbnormalWire(1, ElementalSummons.SpiritAbnormalId(2920101), -1, Target.PermanentExpiry, CasterKey: OwnerKey, RangeBase: Pos)]);
        var packet = GamePackets.NpcSummonAppear(appear, OwnerKey, wireLevel: 4, spawnTag: ElementalSummons.SpiritSpawnTag(2920101));
        Assert.Equal(0x3641, GameSessionTestHelpers.Op(packet));
        var reader = new PacketReader(packet) { Offset = 2 };
        Assert.Equal(37490, (int)reader.Varint());
        Assert.Equal(GamePackets.NpcSummonAppearMask, reader.U16());
        var statsNeedle = new byte[] { 0xdc, 0x05, 0x00, 0x00, 0xdc, 0x05, 0x00, 0x00 };
        Assert.True(packet.AsSpan().IndexOf(statsNeedle) > 0, "official stat block 1500/1500 present");
        var ownerBytes = BitConverter.GetBytes((uint)OwnerKey);
        Assert.True(packet.AsSpan().IndexOf(ownerBytes) > 0, "parent owner key as u32 LE");
        Assert.True(packet.AsSpan().IndexOf(BitConverter.GetBytes(ElementalSummons.SpiritSpawnTag(2920101))) > 0);
    }

    [Fact]
    public void Summon_follow_move_opcode_and_move_state()
    {
        var packet = GamePackets.SummonFollowMove(37490, Pos, 1.5f, 1_760_300_000_000);
        Assert.Equal(0x373c, GameSessionTestHelpers.Op(packet));
        var reader = new PacketReader(packet) { Offset = 2 };
        _ = reader.Varint();
        Assert.Equal((byte)3, reader.U8());
    }

    [Fact]
    public async Task Summon_from_skill_sends_appear_follow_and_spawn_skill()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, 1_760_300_000_000, level: 25, rng: new Random(3));
        var replies = session.SummonFromSkillForTest(16100000, Combat.TrainingDummyKey, 1_760_300_000_100);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3641);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x373c);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3802 && p.AsSpan().IndexOf(BitConverter.GetBytes(GamePackets.SpiritSpawnSkillId)) >= 0);
    }
}
