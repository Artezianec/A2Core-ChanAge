using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Boss groggy (capture 233052) vs slow regular-mob groggy.</summary>
public sealed class StaggerGaugeCalibrationTests
{
    static Target LakeGuardian() => Combat.MapTargets(GameSessionTestHelpers.LiveGameData, 210016).Values.Single(t => t.NpcId == 2701034);

    static Target NormalCherub() => Combat.MapTargets(GameSessionTestHelpers.LiveGameData, 100010).Values.First(t => t.NpcId == 2290000);

    [Fact]
    public void Boss_capture_233052_per_hit_drain_capped_at_seven()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var boss = LakeGuardian();
        StaggerGaugeCombat.InitBossGauge(boss);
        Assert.Equal(StaggerGaugeCombat.BossCaptureGroggyMax, boss.GroggyMax);
        foreach (var hit in new[] { 100, 250, 500, 900 })
        {
            var gauge = StaggerGaugeCombat.GaugeDamage(data, 11010000, boss, hit);
            Assert.Equal(7, gauge);
        }
        Assert.Equal(10, StaggerGaugeCombat.GaugeDamage(data, 16_140_000, boss, hitDamage: 100));
    }

    [Fact]
    public void Capture_233052_wire_energy_steps_before_jointstrike_break()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var boss = LakeGuardian();
        StaggerGaugeCombat.InitBossGauge(boss);
        Assert.Equal(50, StaggerGaugeCombat.WireGroggyEnergy(boss));
        int[] skills = [11_010_000, 11_010_000, 16_140_000, 11_010_000, 11_010_000, 11_010_000];
        int[] expectedEnergy = [43, 36, 26, 19, 12, 5];
        for (var i = 0; i < skills.Length; i++)
        {
            var gauge = StaggerGaugeCombat.GaugeDamage(data, skills[i], boss, hitDamage: 200);
            StaggerGaugeCombat.AddGaugeDamage(boss, gauge, 1, 1000 + i, null, firstRow: true);
            Assert.Equal(expectedEnergy[i], StaggerGaugeCombat.WireGroggyEnergy(boss));
        }
        var breakGauge = StaggerGaugeCombat.GaugeDamage(data, 16_140_000, boss, hitDamage: 200);
        var breakReplies = StaggerGaugeCombat.AddGaugeDamage(boss, breakGauge, 1, 2000, null, firstRow: true, breakingSkillId: 16_140_000);
        Assert.Contains(breakReplies, p => p.SequenceEqual(GamePackets.UpdateGroggyInfoBreak(boss.Key)));
    }

    [Fact]
    public void Hero_boss_first_break_still_within_captured_hp_band()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var boss = LakeGuardian();
        StaggerGaugeCombat.InitBossGauge(boss);
        const int hit = 100;
        var gaugePerHit = StaggerGaugeCombat.GaugeDamage(data, 11010000, boss, hit);
        var hitsToBreak = (int)Math.Ceiling(StaggerGaugeCombat.BossCaptureGroggyMax / (double)gaugePerHit);
        var hpToFirstBreak = hitsToBreak * hit;
        Assert.InRange(hpToFirstBreak, (int)(boss.HpMax * 0.08), (int)(boss.HpMax * 0.45));
    }

    [Fact]
    public void Regular_mob_gauge_fills_one_point_per_hit_and_rarely_breaks_in_one_kill()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var mob = NormalCherub();
        StaggerGaugeCombat.InitRegularMobGaugeForTests(mob);
        const int hit = 80;
        var perHit = StaggerGaugeCombat.GaugeDamage(data, 16_040_000, mob, hit);
        Assert.Equal(StaggerGaugeCombat.RegularMobMaxGroggyDrainPerHit, perHit);
        var total = 0;
        var breaks = 0;
        for (var i = 0; i < mob.HpMax / hit; i++)
        {
            total += perHit;
            if (total >= StaggerGaugeCombat.DefaultGaugeMax)
            {
                breaks++;
                total = 0;
            }
        }
        Assert.Equal(0, breaks);
    }
}
