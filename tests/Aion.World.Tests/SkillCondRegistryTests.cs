using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class SkillCondRegistryTests
{
    [Theory]
    [InlineData("Groggy", "Stagger", true)]
    [InlineData("Stagger", "Groggy", true)]
    [InlineData("Precision", "Stagger", false)]
    public void Effect_types_match_groggy_and_stagger(string required, string actual, bool expect) =>
        Assert.Equal(expect, SkillCondRegistry.EffectTypesMatch(required, actual));

    [Fact]
    public void Unknown_cond_type_is_not_known_and_logs_once()
    {
        SkillCondRegistry.ResetLogForTests();
        Assert.False(SkillCondRegistry.IsKnownType("FutureCondType"));
        Assert.False(SkillCondRegistry.NoteUnknownType("FutureCondType", 14_070_000));
        Assert.False(SkillCondRegistry.NoteUnknownType("FutureCondType", 14_080_000));
    }

    [Fact]
    public void Unknown_skill_cond_allows_cast_without_prerequisite_buff()
    {
        SkillCondRegistry.ResetLogForTests();
        var data = GameSessionTestHelpers.LiveGameData;
        var baseDef = data.Skill(14_070_000)!;
        var foreign = baseDef with
        {
            SkillCond = new SkillCondNeed("FutureCondType", SelectTarget: "Self"),
        };
        var session = new GameSession(data, new GameSessionOptions { CharacterLevel = 45, TrainingDummy = true }, new Random(1));
        Assert.True(session.UseConditionMetForTests(foreign, 1_760_004_000_000, skillId: 14_070_000));
    }

    [Theory]
    [InlineData("HasAbnormalGroupId")]
    [InlineData("HasAbnormalEffectType")]
    public void Known_types_are_enforced(string condType)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var foreign = data.Skill(14_070_000)! with
        {
            SkillCond = condType == "HasAbnormalGroupId"
                ? new SkillCondNeed(condType, AbnormalGroup: 14_090_001, SelectTarget: "Self")
                : new SkillCondNeed(condType, AbnormalEffect: "Precision", SelectTarget: "Self"),
        };
        var session = new GameSession(data, new GameSessionOptions { CharacterLevel = 45, TrainingDummy = true }, new Random(2));
        Assert.False(session.UseConditionMetForTests(foreign, 1_760_004_000_000, skillId: 14_070_000));
    }
}