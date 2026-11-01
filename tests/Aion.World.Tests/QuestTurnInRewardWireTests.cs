using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QuestTurnInRewardWireTests
{
    const long Now = 1_760_090_400_000;

    static int Op(byte[] p) => p.Length >= 2 ? p[0] | (p[1] << 8) : 0;

    [Fact]
    public async Task Quest_turn_in_level_up_batch_matches_official_order()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 2, level: 1);
        await session.QuestCommandAsync("QuestAdd", "HQ1101010", default);
        await session.QuestCommandAsync("QuestComplete", "HQ1101010", default);
        var uid = (int)session.Quests!.Get("HQ1101010")!.Uid;
        var reward = await session.HandleAsync(new PacketWriter().U16(0x8d0e).I32(uid).I32(0).ToArray(), Now + 100);
        var ops = reward.Select(Op).ToList();
        var guide = ops.IndexOf(0x8d15);
        var stats = ops.IndexOf(0x364a);
        var cp = ops.IndexOf(0x3656);
        var counter = ops.IndexOf(0x5684);
        var exp = ops.IndexOf(0x8d07);
        var lvl = ops.IndexOf(0x8d08);
        var take = ops.IndexOf(0x8d0f);
        var addSkill = ops.IndexOf(0x5101);
        var progress = ops.IndexOf(0x8d10);
        Assert.True(guide >= 0 && stats > guide, "Tutorial guide before stat sheet on level-up turn-in");
        Assert.True(cp > stats, "0x3656 after 0x364a");
        Assert.True(counter >= 0 && exp > counter, "0x5684 before exp");
        Assert.True(lvl > exp && take > lvl, "level then take-reward reply");
        Assert.True(addSkill > take, "AddSkillList after 0x8d0f");
        if (progress >= 0) Assert.True(progress > addSkill, "quest progress after new skills");
        var statPkt = reward.First(p => Op(p) == 0x364a);
        var skillPt = new PacketWriter().U16(104).I32(session.SkillPointsEarned).ToArray();
        Assert.True(statPkt.AsSpan().IndexOf(skillPt) >= 0, "skill points on stat 104 (0x68)");
    }
}
