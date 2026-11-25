using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class AttendanceCollectRewardsTests
{
    const long Now = 1_793_368_800_000; // 2026-10-30 14:00 UTC — four Global attendance boards (Light)
    const int AtreiaAchievement = 510_350_100;
    const int LaunchAchievement = 510_340_100;

    static int Op(byte[] packet) => GameSessionTestHelpers.Op(packet);

    static List<(int GroupId, int Value1, int Value2)> Parse56b6(byte[] packet)
    {
        var count = packet[2];
        var list = new List<(int, int, int)>();
        var i = 3;
        for (var n = 0; n < count; n++)
        {
            i++;
            var groupId = BitConverter.ToInt32(packet, i);
            i += 4 + 4 + 4 + 8;
            list.Add((groupId, BitConverter.ToInt32(packet, i), BitConverter.ToInt32(packet, i + 4)));
            i += 8;
        }
        return list;
    }

    [Fact]
    public async Task Check_in_on_e34d_does_not_grant_and_collect_all_grants_for_every_tab()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var seasons = GameSession.LatestAttendanceSeasons(1, Now + 100);
        var reward = AchievementTables.Embedded.Of(AtreiaAchievement)!.StepRewards(1);
        Assert.NotEmpty(reward);
        var before = store.Items.Where(i => i.TemplateId == reward[0].ItemId).Sum(i => i.Quantity);

        // Official (capture 20261124-130131 #49843): the check-in answers Attendance_RS only. The tab's reward
        // stays unclaimed, so its F Collect Rewards has something to take.
        var checkIn = await session.HandleAsync([0x4d, 0xe3], Now + 100);

        var rs = Assert.Single(checkIn, p => Op(p) == GameSession.AttendanceRs);
        Assert.Equal(0, BitConverter.ToUInt16(rs, 2));
        Assert.Contains(checkIn, p => Op(p) == GameSession.AchievementCountUpdateNt);
        Assert.DoesNotContain(checkIn, p => Op(p) == 0x561b);
        Assert.Equal(before, store.Items.Where(i => i.TemplateId == reward[0].ItemId).Sum(i => i.Quantity));

        var todayKey = ContentResetSchedule.GameDayKey(Now + 100);
        var contents = Assert.Single(checkIn, p => Op(p) == GameSession.AccountContentsInfoListNt);
        foreach (var (_, board) in seasons)
        {
            var entry = Assert.Single(Parse56b6(contents), e => e.GroupId == board.SeasonGroupId);
            Assert.Equal(todayKey, entry.Value1);
            Assert.Equal(0, entry.Value2);   // checked in, not collected yet
            Assert.Equal(1, session.AchievementCount(board.Achievement));
            var stored = Assert.Single(store.Achievements, a => a.Id == board.Achievement);
            Assert.Equal(0, stored.RewardedStep);
        }

        // Collect all (category 7, group 0) takes every tab's pending step.
        var collect = await session.HandleAsync(GameSessionTestHelpers.AttendanceBatch(0), Now + 101);

        Assert.Equal(0, BitConverter.ToInt32(Assert.Single(collect, p => Op(p) == GameSession.TakeAchievementRewardBatchRs), 2));
        Assert.Contains(collect, p => Op(p) == 0x561b);
        var after = store.Items.Where(i => i.TemplateId == reward[0].ItemId).Sum(i => i.Quantity);
        Assert.True(after >= before + reward[0].Count);
        var contentsAfter = Assert.Single(collect, p => Op(p) == GameSession.AccountContentsInfoListNt);
        foreach (var (_, board) in seasons)
        {
            var entry = Assert.Single(Parse56b6(contentsAfter), e => e.GroupId == board.SeasonGroupId);
            Assert.Equal(todayKey, entry.Value1);
            Assert.Equal(todayKey, entry.Value2);
            Assert.Equal(1, Assert.Single(store.Achievements, a => a.Id == board.Achievement).RewardedStep);
        }
    }

    [Fact]
    public async Task Atreias_call_group_510035_claims_its_day_from_the_live_request()
    {
        // Live client trace 20261124: 0x5689 body 07 53C80700 00 = category 7, group 0x0007C853 (510035), only false.
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var reward = AchievementTables.Embedded.Of(AtreiaAchievement)!.StepRewards(1)[0];
        await session.HandleAsync([0x4d, 0xe3], Now + 100);
        var before = store.Items.Where(i => i.TemplateId == reward.ItemId).Sum(i => i.Quantity);

        var collect = await session.HandleAsync(
            new PacketWriter().U16(GameSession.TakeAchievementRewardBatchRq).U8(7).I32(510_035).U8(0).ToArray(),
            Now + 101);

        Assert.Equal(0, BitConverter.ToInt32(Assert.Single(collect, p => Op(p) == GameSession.TakeAchievementRewardBatchRs), 2));
        Assert.Equal(1, Assert.Single(store.Achievements, a => a.Id == AtreiaAchievement).RewardedStep);
        Assert.True(store.Items.Where(i => i.TemplateId == reward.ItemId).Sum(i => i.Quantity) >= before + reward.Count);
        var todayKey = ContentResetSchedule.GameDayKey(Now + 101);
        var board = GameSession.LatestAttendanceSeasons(1, Now + 101).Single(e => e.Board.Achievement == AtreiaAchievement).Board;
        var entry = Assert.Single(Parse56b6(Assert.Single(collect, p => Op(p) == GameSession.AccountContentsInfoListNt)),
            e => e.GroupId == board.SeasonGroupId);
        Assert.Equal(todayKey, entry.Value2);
    }

    [Fact]
    public async Task Second_e34d_same_day_is_a_no_op_refresh()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var atreiaReward = AchievementTables.Embedded.Of(AtreiaAchievement)!.StepRewards(1)[0];
        await session.HandleAsync([0x4d, 0xe3], Now + 100);
        var qtyAfterFirst = store.Items.Where(i => i.TemplateId == atreiaReward.ItemId).Sum(i => i.Quantity);

        var again = await session.HandleAsync([0x4d, 0xe3], Now + 101);
        Assert.Equal(0, BitConverter.ToUInt16(Assert.Single(again, p => Op(p) == GameSession.AttendanceRs), 2));
        Assert.DoesNotContain(again, p => Op(p) == 0x561b);
        Assert.Equal(qtyAfterFirst, store.Items.Where(i => i.TemplateId == atreiaReward.ItemId).Sum(i => i.Quantity));
        var contents = Assert.Single(again, p => Op(p) == GameSession.AccountContentsInfoListNt);
        var atreiaBoard = GameSession.LatestAttendanceSeasons(1, Now + 100).Single(e => e.Board.Achievement == AtreiaAchievement).Board;
        var todayKey = ContentResetSchedule.GameDayKey(Now + 101);
        var entry = Assert.Single(Parse56b6(contents), e => e.GroupId == atreiaBoard.SeasonGroupId);
        Assert.Equal(todayKey, entry.Value1);
        Assert.Equal(0, entry.Value2);   // still only checked in
    }

    [Fact]
    public async Task Collect_after_prior_check_in_grants_and_persists()
    {
        var store = new FakeCharacterStore();
        GameSessionTestHelpers.SeedAttendanceCheckedInNotRewarded(store, Now + 100);
        var launchBoard = GameSession.LatestAttendanceSeasons(1, Now + 200).Single(e => e.Board.Achievement == LaunchAchievement).Board;
        var launchReward = AchievementTables.Embedded.Of(LaunchAchievement)!.StepRewards(1)[0];
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now + 150, level: 55);

        var refresh = await session.HandleAsync([0x4d, 0xe3], Now + 200);
        Assert.DoesNotContain(refresh, p => Op(p) == 0x561b);

        var before = store.Items.Where(i => i.TemplateId == launchReward.ItemId).Sum(i => i.Quantity);
        var claim = await session.HandleAsync(
            GameSessionTestHelpers.AttendanceBatch(GameSessionTestHelpers.AttendanceGroupId(LaunchAchievement)),
            Now + 201);
        Assert.Contains(claim, p => Op(p) == 0x561b);
        Assert.Equal(before + launchReward.Count, store.Items.Where(i => i.TemplateId == launchReward.ItemId).Sum(i => i.Quantity));
        Assert.Equal(1, Assert.Single(store.Achievements, a => a.Id == LaunchAchievement).RewardedStep);
        var todayKey = ContentResetSchedule.GameDayKey(Now + 201);
        var entry = Assert.Single(Parse56b6(Assert.Single(claim, p => Op(p) == GameSession.AccountContentsInfoListNt)),
            e => e.GroupId == launchBoard.SeasonGroupId);
        Assert.Equal(todayKey, entry.Value2);
    }

    [Fact]
    public async Task Reloaded_session_keeps_reward_day_after_collect()
    {
        var store = new FakeCharacterStore();
        var session1 = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        await session1.HandleAsync([0x4d, 0xe3], Now + 100);
        await session1.HandleAsync(GameSessionTestHelpers.AttendanceBatch(0), Now + 101);

        var session2 = await GameSessionTestHelpers.EnterWorldAsync(store, Now + 200, level: 55);
        var refresh = await session2.HandleAsync([0x4d, 0xe3], Now + 201);
        var todayKey = ContentResetSchedule.GameDayKey(Now + 201);
        var contents = Assert.Single(refresh, p => Op(p) == GameSession.AccountContentsInfoListNt);
        foreach (var (_, board) in GameSession.LatestAttendanceSeasons(1, Now + 201))
        {
            var entry = Assert.Single(Parse56b6(contents), e => e.GroupId == board.SeasonGroupId);
            Assert.Equal(todayKey, entry.Value1);
            Assert.Equal(todayKey, entry.Value2);
        }
    }
}
