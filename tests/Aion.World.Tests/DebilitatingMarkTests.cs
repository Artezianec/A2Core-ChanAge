using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Cleric Debilitating Mark (17080000): target abnormal 170800001 with Dot_NormalCalc and amplify debuff.</summary>
public sealed class DebilitatingMarkTests
{
    const long Now = 1_760_040_000_000;
    const int Skill = 17_080_000;
    const int Mark = 170_800_001;

    [Fact]
    public async Task Mark_debuff_on_npc_ticks_dot_from_skill_area_row()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now);
        var mob = session.Targets![Combat.TrainingDummyKey];
        var hpBefore = mob.Hp;
        session.ApplyNpcDebuffForTests(mob, Mark, 10_000, Now + 10_000, skillId: Skill);
        Assert.True(session.NpcHasAbnormalId(mob.Key, Mark, session.PlayerKey));
        var dotReplies = await session.TickAsync(Now + 11_500);
        var dot = Assert.Single(dotReplies, p => GameSessionTestHelpers.Op(p) == 0x3805);
        Assert.Contains((byte)0x0a, dot);
        Assert.True(mob.Hp < hpBefore);
        Assert.Equal(500, session.NpcVictimAmplifyBp(mob.Key, pvp: false, Now + 11_000));
    }

    [Fact]
    public void Mark_abnormal_exports_scaled_dot_spec()
    {
        var dots = AbnormalDotRows.ScaledDots(Mark);
        Assert.Single(dots);
        Assert.Equal(1000, dots[0].IntervalMs);
        Assert.Equal(14190, dots[0].RatioMin);
        Assert.Equal(500, NpcVictimAmplify.DebuffPermille(Mark, pvp: false));
    }

    [Fact]
    public void Marked_npc_takes_amplified_skill_damage()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var weapon = data.StarterWeapon(data.Profile(5, 1)!)!.Value;
        var rng = new Random(9);
        var before = Combat.SkillHits(data, Skill, "Cleric", 55, weapon, 100, rng).Sum(h => h.Amount);
        var after = Combat.SkillHits(data, Skill, "Cleric", 55, weapon, 100, rng,
            amplifyAllBp: NpcVictimAmplify.DebuffPermille(Mark, pvp: false)).Sum(h => h.Amount);
        Assert.True(after > before);
    }
}