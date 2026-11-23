using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class NpcBehaviorCasesTests
{
    [Fact]
    public void Embedded_index_resolves_berk_casting_talk()
    {
        var index = NpcBehaviorCases.Embedded;
        var target = new Target
        {
            Key = 1, Name = "M_PD_Krao_Berc_P1_Type01", Hp = 100, HpMax = 100, Level = 50,
            Position = Vector3d.Zero, WalkSpeed = 1, RunSpeed = 1, Radius = 50, SubType = "HeroMonster",
        };
        var casting = index.Matching(target, "casting", skill: 1800053);
        Assert.NotEmpty(casting);
        Assert.Contains(casting, c => c.Talk.StartsWith("PD_KraoCave_", StringComparison.Ordinal));
    }

    [Fact]
    public void Combat_start_emits_caption_once()
    {
        var target = new Target
        {
            Key = 9, Name = "M_PD_ReforgedAbyss_Durbati_01_V01_001", NpcId = 2400001, Hp = 50_000, HpMax = 50_000,
            Level = 60, Position = Vector3d.Zero, WalkSpeed = 1, RunSpeed = 1, Radius = 50, SubType = "HeroMonster",
        };
        var ai = new NpcAi(new Random(1)) { BehaviorCases = NpcBehaviorCases.Embedded };
        var first = ai.BehaviorOnCombatStart(target, 1000);
        var second = ai.BehaviorOnCombatStart(target, 2000);
        Assert.NotEmpty(first);
        Assert.Empty(second);
        Assert.All(first, p => Assert.True(p.Length > 4));
    }
}