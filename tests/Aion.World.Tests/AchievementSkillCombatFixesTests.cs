using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class AchievementSkillCombatFixesTests
{
    static readonly long Now = 1_793_368_800_000;
    const int BattlePassMissionId = 910430700;

    [Fact]
    public async Task Battle_pass_exp_granted_when_mission_step_completes()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var mission = AchievementTables.Embedded.Of(BattlePassMissionId)!;
        var need = mission.StepCount(1);
        session.SeedAchievementForTests(BattlePassMissionId, need - 1, Now);
        var passExpBefore = AchievementTables.Embedded.OfType("BattlePassExp").Sum(e => session.AchievementCount(e.Id));
        var replies = session.AchieveForTests("KillNpcAll", 1, now: Now);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == GameSession.BattlePassInfoListNt);
        Assert.Equal(need, session.AchievementCount(BattlePassMissionId));
        var passExpAfter = AchievementTables.Embedded.OfType("BattlePassExp").Sum(e => session.AchievementCount(e.Id));
        Assert.True(passExpAfter > passExpBefore);
    }

    [Fact]
    public async Task Ap_heal_with_zero_ap_max_does_not_restore_sp()
    {
        var (session, _) = await SkillEffectTests.Setup("Cleric");
        session.SetApForTests(0, 0);
        var spBefore = session.Resource!.Sp;
        session.ReceiveFriendlyRow(["ApHeal", "500", "500"], Now);
        Assert.Equal(spBefore, session.Resource.Sp);
    }

    [Fact]
    public async Task Target_scope_heal_skips_invalid_target_instead_of_healing_caster()
    {
        var (session, _) = await SkillEffectTests.Setup("Cleric");
        var hpBefore = session.Resource!.Hp;
        session.SetHpForChecks(hpBefore / 2);
        var recipients = session.FriendlyRecipientsForTests(17100000, session.PlayerKey + 50_000);
        Assert.Empty(recipients);
        Assert.Equal(hpBefore / 2, session.Resource.Hp);
    }

    [Fact]
    public async Task Heal_roll_handles_int_max_upper_bound()
    {
        var (session, _) = await SkillEffectTests.Setup("Cleric");
        Assert.NotEmpty(session.ReceiveFriendlyRow(["HpHeal", "2147483647", "2147483647"], Now));
    }

    [Fact]
    public void Achievement_saturating_add_clamps_overflow()
    {
        Assert.Equal(int.MaxValue, GameSession.SaturatingAddForTests(int.MaxValue - 1, 5));
        Assert.Equal(int.MinValue, GameSession.SaturatingAddForTests(int.MinValue + 1, -5));
    }

    [Fact]
    public async Task Flush_keeps_dirty_when_save_fails()
    {
        var store = new FakeCharacterStore { FailSaveAchievements = true };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 4);
        session.AchieveForTests("LoginCharacter", now: Now);
        Assert.True(session.PendingAchievementWrites > 0);
        await session.TickAsync(Now + 5_000);
        Assert.True(session.PendingAchievementWrites > 0);
    }
}