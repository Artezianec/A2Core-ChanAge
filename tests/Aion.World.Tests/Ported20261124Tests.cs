using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>The 2.0.9 2026-11-24 upstream sync items ported to 2.0.12 (skill conditions/side data, dummy, conversion cancel, attendance claim).</summary>
public sealed class Ported20261124Tests
{
    const long Now = 1_793_368_800_000; // 2026-10-30 14:00 UTC, open Global attendance boards
    const int GlobalAttendanceGroup = 510_035;

    static int Op(byte[] packet) => GameSessionTestHelpers.Op(packet);

    [Fact]
    public void Unknown_skill_cond_types_are_allowed_and_groggy_matches_stagger()
    {
        Assert.False(SkillCondRegistry.IsKnownType("SomeFutureCond"));
        Assert.False(SkillCondRegistry.NoteUnknownType("SomeFutureCond", 11010000));
        Assert.True(SkillCondRegistry.NoteUnknownType(SkillCondRegistry.HasAbnormalEffectType));
        Assert.True(SkillCondRegistry.EffectTypesMatch("Groggy", "Stagger"));
        Assert.True(SkillCondRegistry.EffectTypesMatch("Stagger", "Groggy"));
        Assert.True(SkillCondRegistry.RequiresStaggerWindow(new SkillCondNeed("HasAbnormalEffectType", AbnormalEffect: "Groggy")));
    }

    [Fact]
    public void Skill_side_data_looks_up_the_specialization_parent()
    {
        // 11010010 Rending Blow: SpecializedSkill.dat child of 11010000 in tools/data/specialized-skills.json.
        Assert.Equal(11010000, SkillSideData.DataParent(11010010));
        Assert.Equal([11010010, 11010000], SkillSideData.LookupKeys(11010010));
        Assert.Equal(11010010, SkillSideData.WireEffectId(1101001011, 11010010) / 100);
    }

    [Fact]
    public async Task Training_dummy_takes_damage_but_does_not_die()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now, level: 45);
        var dummy = session.Targets![Combat.TrainingDummyKey];
        Assert.True(Combat.IsTrainingDummy(dummy));
        Assert.False(session.TrainingDummyKillAllowedForTests(dummy));
        Assert.Equal(1, session.ApplyDamageToNpcForTests(dummy, dummy.Hp + 1000));
        Assert.Equal(0, Combat.HpAfterDamage(new Target { Key = 2001, NpcId = 2000001, Name = "Monster", Hp = 100, HpMax = 100 }, 100, 500, killAllowed: true));
    }

    [Fact]
    public async Task Conversion_cancel_without_a_morph_reports_invalid_data()
    {
        var session = await GameSessionTestHelpers.EnterWorldAsync(new FakeCharacterStore(), Now, level: 25);
        var reply = await session.HandleAsync(new PacketWriter().U16(GameSession.ConversionCancelRq).ToArray(), Now + 100);
        var rs = Assert.Single(reply, p => Op(p) == GameSession.ConversionCancelRs);
        Assert.Equal(GameSession.ConversionInvalidData, BitConverter.ToUInt16(rs, 2));
        Assert.DoesNotContain(reply, p => Op(p) == GameSession.ConversionCanceledNt);
    }

    [Fact]
    public async Task Attendance_batch_checks_in_a_new_day_before_claiming()
    {
        var store = new FakeCharacterStore();
        var board = GameSession.LatestAttendanceSeasons(1, Now).Single(e => e.Board.SeasonGroupId == 37).Board;
        // Checked in two days ago with nothing claimed: the claim path must check in today first.
        store.Achievements.Add(new AchievementRecord(board.Achievement, 1, null, 0,
            DateTimeOffset.FromUnixTimeMilliseconds(Now - 2 * 86_400_000)));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var batch = await session.HandleAsync(
            new PacketWriter().U16(GameSession.TakeAchievementRewardBatchRq).U8(7).I32(GlobalAttendanceGroup).U8(1).ToArray(),
            Now + 101);
        Assert.Equal(2, session.AchievementCount(board.Achievement));
        var rs = Assert.Single(batch, p => Op(p) == GameSession.TakeAchievementRewardBatchRs);
        Assert.Equal(0, BitConverter.ToInt32(rs, 2));
        Assert.Equal(2, store.Achievements.Single(a => a.Id == board.Achievement).RewardedStep);
    }
}
