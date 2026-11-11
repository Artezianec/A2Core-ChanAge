using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>tools/data/bot-rotations.json (ported from ckrealm/A2Core) against our class data, and BotBrain's choices.</summary>
public sealed class BotRotationTests
{
    static readonly GameData Data = GameData.Embedded;
    static readonly BotRotations Rotations = BotRotations.Embedded;
    static readonly HashSet<string> Roles = ["tank", "dps", "heal"];

    /// <summary>Classes a character can be created as here (Fighter's rotation is kept for when the class is released).</summary>
    static readonly HashSet<string> Creatable = Enumerable.Range(1, 64).Select(c => Data.Profile(c, 1)?.Class).OfType<string>().ToHashSet();

    public static TheoryData<string> Playable()
    {
        var classes = new TheoryData<string>();
        foreach (var cls in Rotations.Classes.Keys.Where(Creatable.Contains)) classes.Add(cls);
        return classes;
    }

    [Fact]
    public void Every_creatable_class_has_a_rotation()
    {
        Assert.True(Rotations.GlobalCooldownMs > 0);
        Assert.True(Rotations.TankHateMultiplier > 1);
        Assert.True(Creatable.Count >= 8);
        foreach (var cls in Creatable.Where(Data.Stigmas.Classes.Contains)) Assert.True(Rotations.For(cls) is not null, cls);
    }

    [Theory]
    [MemberData(nameof(Playable))]
    public void Every_step_is_a_skill_of_its_class_with_a_supported_condition(string cls)
    {
        var rotation = Rotations.For(cls)!;
        var kit = Data.LearnedSkills(cls, Data.MaxLevel).Select(k => k.SkillId).ToHashSet();
        var stigmas = Data.Stigmas.ClassStigmas(cls).ToHashSet();
        Assert.True(rotation.Range > 0);
        Assert.NotEmpty(rotation.Roles);
        Assert.All(rotation.Roles, r => Assert.Contains(r, Roles));
        foreach (var step in rotation.Rotation)
        {
            Assert.True(kit.Contains(step.Skill) || stigmas.Contains(step.Skill), $"{cls} {step.Skill} is neither in the kit nor a class stigma");
            Assert.NotNull(Data.Skill(step.Skill));
            Assert.Contains(step.Condition.Target, BotCondition.SupportedTargets);
            Assert.All(step.Roles ?? [], r => Assert.Contains(r, Roles));
            Assert.True(step.Area is null or "self" or "target");
        }
        // The last entry is an unconditional attack learned at level 1: the bot always has something to cast.
        var last = rotation.Rotation[^1];
        Assert.Equal("enemy", last.Condition.Target);
        Assert.Equal(1, last.Level);
        Assert.Contains(last.Skill, kit);
    }

    [Fact]
    public void Gladiator_ends_on_Keen_Strike_and_the_Cleric_opens_with_its_ally_heal()
    {
        Assert.Equal(11020000, Rotations.For("Gladiator")!.Rotation[^1].Skill);
        var cleric = Rotations.For("Cleric")!;
        Assert.Equal("heal", cleric.Roles[0]);
        Assert.Equal(17100000, cleric.Rotation[0].Skill);
        Assert.Equal("ally", cleric.Rotation[0].Condition.Target);
        Assert.Equal(["heal"], cleric.Rotation[0].Roles ?? []);
    }
}
