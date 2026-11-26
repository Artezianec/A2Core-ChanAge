using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Self-cleanse rows (Dispel_True [DeBuff]) and the skill-target scope fallback.</summary>
public sealed class SkillEffectControlTests
{
    [Fact]
    public void Dispel_true_on_a_self_skill_breaks_control()
    {
        // 301 has a Dispel_True [DeBuff] row and no area scope; its Skill.dat target is Self.
        Assert.Equal("self", SkillEffects.Scope(301));
        Assert.True(SkillEffects.BreaksControl(301));
        Assert.False(SkillEffects.BreaksControl(11010000));
    }
}
