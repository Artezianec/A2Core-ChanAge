using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class HoldChargeSupportTests
{
    static SkillDefinition TwoStageSkill() => GameData.Embedded.Skill(14_010_000)!;

    [Theory]
    [InlineData(0, 1)]
    [InlineData(800, 1)]
    [InlineData(1501, 2)]
    [InlineData(4000, 2)]
    public void Deadshot_step_from_server_hold_ms(int holdMs, int expectedStep)
    {
        Assert.Equal(expectedStep, HoldChargeSupport.StepForElapsed(TwoStageSkill(), holdMs));
    }

    [Fact]
    public void Data_levels_override_default_staging()
    {
        var skill = TwoStageSkill() with
        {
            HoldCharge = new SkillHoldCharge(MaxHoldMs: 2000, Levels:
            [
                new SkillHoldChargeLevel(0),
                new SkillHoldChargeLevel(500, DamageRow: 1),
            ]),
        };
        Assert.Equal(1, HoldChargeSupport.StepForElapsed(skill, 100));
        Assert.Equal(2, HoldChargeSupport.StepForElapsed(skill, 600));
        Assert.Equal(0, HoldChargeSupport.DamageRowIndex(skill, 1));
        Assert.Equal(1, HoldChargeSupport.DamageRowIndex(skill, 2));
    }
}