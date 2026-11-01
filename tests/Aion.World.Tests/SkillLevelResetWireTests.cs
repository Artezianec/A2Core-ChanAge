using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Skill reset wire (capture 000506): 0x3656, 0x3844 with u8 levels, one batched 0x3813, 0x5105 spent 0.</summary>
public sealed class SkillLevelResetWireTests
{
    static readonly long Now = 1_700_000_000_000;
    const int RendingBlow = 11010000;
    const int Cleave = 11020000;

    static int SkillLevelUpdateCount(byte[] packet)
    {
        Assert.Equal(GameSession.SkillLevelUpdateNt, GameSessionTestHelpers.Op(packet));
        return (int)new PacketReader(packet) { Offset = 2 }.Varint();
    }

    [Fact]
    public async Task Full_reset_sends_one_3813_3844_u8_levels_and_5105_spent_zero()
    {
        var store = new FakeCharacterStore { Progress = (10, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(RendingBlow).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(Cleave).ToArray(), Now);
        Assert.True(session.SkillPointsLeft < session.SkillPointsEarned);

        var reset = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelResetRq).U8(1).I32(0).ToArray(), Now);
        Assert.Equal([0x3656, GameSession.SkillLevelResetRs, GameSession.SkillLevelUpdateNt, GameSession.SkillPointUpdateNt],
            GameSessionTestHelpers.SkillMasteryCoreOpcodes(reset));
        Assert.DoesNotContain(reset, p => GameSessionTestHelpers.Op(p) == 0x5100);

        var rs = reset.Single(p => GameSessionTestHelpers.Op(p) == GameSession.SkillLevelResetRs);
        Assert.Equal(0, BitConverter.ToUInt16(rs, 2));
        Assert.Equal(1, rs[4]); // acquire type
        Assert.Equal(0, BitConverter.ToInt32(rs, 5)); // skill 0 = all
        var reader = new PacketReader(rs) { Offset = 9 };
        var count = (int)reader.Varint();
        Assert.Equal(2, count);
        for (var i = 0; i < count; i++)
        {
            _ = reader.I32();
            Assert.Equal(1, reader.U8()); // u8 kit level, not 4-byte i32
        }

        var update = reset.Single(p => GameSessionTestHelpers.Op(p) == GameSession.SkillLevelUpdateNt);
        Assert.Equal(2, SkillLevelUpdateCount(update));
        Assert.Equal(5, update[^1]); // SkillLevelModify

        Assert.Equal(GamePackets.SkillPointUpdate(0), reset[^1]);
        Assert.Equal(session.SkillPointsEarned, session.SkillPointsLeft);
        Assert.Equal(0, GameSessionTestHelpers.SkillPoint5105Spent(reset[^1]));
    }
}
