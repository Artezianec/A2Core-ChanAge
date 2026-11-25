using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class AttendanceDay4PendingRewardTests
{
    const int GlobalDay28Achievement = 510_350_100; // Global_Attendance_Group_4 / board 1025
    static readonly long Now = 1_795_549_740_000; // 2026-11-24 19:49 UTC (playtest)

    static int Op(byte[] packet) => GameSessionTestHelpers.Op(packet);

    [Fact]
    public async Task E34d_with_count_4_and_rewarded_step_3_claims_day_4_and_clears_red_dot()
    {
        var store = new FakeCharacterStore();
        var checkedInToday = DateTimeOffset.FromUnixTimeMilliseconds(ContentResetSchedule.DailyStartMs(Now) + 3_600_000);
        store.Achievements.Add(new Application.Gameplay.AchievementRecord(GlobalDay28Achievement, 4, null, 3, checkedInToday));

        var board = GameSession.LatestAttendanceSeasons(1, Now).Single(e => e.Board.Achievement == GlobalDay28Achievement).Board;
        var step4 = AchievementTables.Embedded.Of(GlobalDay28Achievement)!.StepRewards(4);
        Assert.NotEmpty(step4);

        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var reply = await session.HandleAsync([0x4d, 0xe3], Now);

        Assert.Equal(0, BitConverter.ToUInt16(Assert.Single(reply, p => Op(p) == GameSession.AttendanceRs), 2));
        Assert.Contains(reply, p => Op(p) == 0x561b);
        Assert.True(store.Items.Where(i => i.TemplateId == step4[0].ItemId).Sum(i => i.Quantity) >= step4[0].Count);

        var stored = Assert.Single(store.Achievements, a => a.Id == GlobalDay28Achievement);
        Assert.Equal(4, stored.Count);
        Assert.Equal(4, stored.RewardedStep);

        var todayKey = ContentResetSchedule.GameDayKey(Now);
        var contents = Assert.Single(reply, p => Op(p) == GameSession.AccountContentsInfoListNt);
        var entry = Parse56b6(contents).Single(e => e.GroupId == board.SeasonGroupId);
        Assert.Equal(todayKey, entry.Value1);
        Assert.Equal(todayKey, entry.Value2);
    }

    [Fact]
    public async Task E34d_after_prior_days_check_in_today_stamps_count_and_claims_step_4()
    {
        var store = new FakeCharacterStore();
        var yesterday = DateTimeOffset.FromUnixTimeMilliseconds(ContentResetSchedule.DailyStartMs(Now) - 86_400_000 + 3_600_000);
        store.Achievements.Add(new Application.Gameplay.AchievementRecord(GlobalDay28Achievement, 3, null, 3, yesterday));

        var step4 = AchievementTables.Embedded.Of(GlobalDay28Achievement)!.StepRewards(4);
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var reply = await session.HandleAsync([0x4d, 0xe3], Now);

        Assert.Equal(0, BitConverter.ToUInt16(Assert.Single(reply, p => Op(p) == GameSession.AttendanceRs), 2));
        Assert.Contains(reply, p => Op(p) == 0x561b);
        var stored = Assert.Single(store.Achievements, a => a.Id == GlobalDay28Achievement);
        Assert.Equal(4, stored.Count);
        Assert.Equal(4, stored.RewardedStep);
        Assert.Contains(store.Items, i => i.TemplateId == step4[0].ItemId);
    }

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
}