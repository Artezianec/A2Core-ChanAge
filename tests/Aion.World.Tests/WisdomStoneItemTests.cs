using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class WisdomStoneItemTests
{
    const long Now = 1_760_050_000_000;
    const int WisdomStoneBound = 531900001;
    const int WisdomStone10 = 531900002;

    [Fact]
    public async Task GetSkillPoint_wisdom_stone_consumes_item_grants_point_and_sends_0x36bb()
    {
        Assert.Equal("GetSkillPoint", RewardBoxes.Embedded.Use(WisdomStoneBound)!.Effect);
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (4, 0) };
        foreach (var (skillId, lvl) in data.LearnedSkills("Gladiator", 4))
            store.Skills.Add(new SkillState(skillId, lvl, null));
        store.Items.Add(FakeCharacterStore.BagItem(WisdomStoneBound, 1, 881));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 4);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        Assert.Equal(1, data.SkillPoints(4));
        Assert.Equal(1, session.SkillPointsLeft);

        var replies = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(881).ToArray(), Now + 1);
        Assert.Equal(0, replies[0][2]);
        Assert.DoesNotContain(store.Items, i => i.TemplateId == WisdomStoneBound && i.Quantity > 0);
        Assert.Equal(1, store.SkillBonusPoints);
        Assert.Equal(2, session.SkillPointsLeft);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == GameSession.UpdateItemSkillPointNt);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x364a);

        var up = await session.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelUpRq).I32(11010000).ToArray(), Now + 2);
        Assert.Equal(0, BitConverter.ToUInt16(GameSessionTestHelpers.SkillLevelUpRs(up), 2));
        Assert.Equal(1, session.SkillPointsLeft);
        Assert.Equal(GamePackets.SkillPointUpdate(1), up.Single(p => GameSessionTestHelpers.Op(p) == GameSession.SkillPointUpdateNt));
    }

    [Fact]
    public async Task Bonus_skill_points_survive_relog_and_appear_in_0x3633_tail()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (10, 0) };
        foreach (var (skillId, lvl) in data.LearnedSkills("Gladiator", 10))
            store.Skills.Add(new SkillState(skillId, lvl, null));
        store.SkillBonusPoints = 5;
        var session1 = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        Assert.Equal(data.SkillPoints(10) + 5, session1.SkillPointsEarned);
        Assert.Equal(data.SkillPoints(10) + 5, session1.SkillPointsLeft);

        var session2 = new GameSession(data, new GameSessionOptions { CharacterLevel = 10, WorldId = store.WorldId }, new Random(9))
        {
            Store = store,
        };
        await session2.HandleAsync(GameSessionTestHelpers.Login(), Now);
        var wireId = store.States[0].WireId;
        await session2.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        var entry = await session2.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 1);
        Assert.Equal(data.SkillPoints(10) + 5, session2.SkillPointsLeft);
        var appear = entry.Single(p => GameSessionTestHelpers.Op(p) == 0x3633);
        var expectedItemSkill = Convert.FromHexString("03010500020000030000"); // item-granted group: type 1 = 5
        Assert.True(appear.AsSpan().IndexOf(expectedItemSkill) > 0, "item-granted skill points on appear");
    }

    [Fact]
    public async Task Ten_point_wisdom_stone_grants_per_item_dat_value()
    {
        var store = new FakeCharacterStore { Progress = (12, 0) };
        store.Items.Add(FakeCharacterStore.BagItem(WisdomStone10, 1, 882));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 12);
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        var before = session.SkillPointsLeft;
        var replies = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(882).ToArray(), Now + 1);
        Assert.Equal(0, replies[0][2]);
        Assert.Equal(before + 10, session.SkillPointsLeft);
        Assert.Equal(10, store.SkillBonusPoints);
    }
}