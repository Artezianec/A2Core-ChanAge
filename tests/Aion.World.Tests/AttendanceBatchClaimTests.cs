using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class AttendanceBatchClaimTests
{
    const long Now = 1_793_368_800_000; // 2026-10-30 14:00 UTC
    const int GlobalAttendanceGroup = 510_035; // 0x0007C853 — L_Global_Attendance_261028_28Day_Check_1

    static int Op(byte[] packet) => GameSessionTestHelpers.Op(packet);

    [Fact]
    public async Task Batch_claim_sends_568a_without_trailing_achievement_nts_and_persists_rewarded_step()
    {
        var store = new FakeCharacterStore();
        GameSessionTestHelpers.SeedAttendanceCheckedInNotRewarded(store, Now + 100);
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var board = GameSession.LatestAttendanceSeasons(1, Now + 100).Single(e => e.Board.SeasonGroupId == 37).Board;

        var batch = await session.HandleAsync(
            new PacketWriter().U16(GameSession.TakeAchievementRewardBatchRq).U8(7).I32(GlobalAttendanceGroup).U8(1).ToArray(),
            Now + 101);

        var rsIndex = batch.FindIndex(p => Op(p) == GameSession.TakeAchievementRewardBatchRs);
        Assert.True(rsIndex >= 0);
        Assert.Equal(0, BitConverter.ToInt32(batch[rsIndex], 2));
        // Capture 001158: no per-step 0x5684 / 0x5683 after the batch reply (they empty the Rewards Collected popup).
        Assert.DoesNotContain(batch, p => Op(p) == GameSession.AchievementCountUpdateNt);
        Assert.DoesNotContain(batch, p => Op(p) == GameSession.AchievementListNt);

        var again = await session.HandleAsync(
            new PacketWriter().U16(GameSession.TakeAchievementRewardBatchRq).U8(7).I32(GlobalAttendanceGroup).U8(1).ToArray(),
            Now + 102);
        var rs = Assert.Single(again, p => Op(p) == GameSession.TakeAchievementRewardBatchRs);
        Assert.Equal(1, BitConverter.ToInt32(rs, 2));
        Assert.DoesNotContain(again, p => Op(p) == GameSession.AchievementCountUpdateNt);
        Assert.Equal(1, session.AchievementCount(board.Achievement));
    }

    [Fact]
    public async Task Single_claim_sends_5684_before_5686()
    {
        var store = new FakeCharacterStore();
        GameSessionTestHelpers.SeedAttendanceCheckedInNotRewarded(store, Now + 100);
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var board = GameSession.LatestAttendanceSeasons(1, Now + 100)[0].Board;
        var reward = await session.HandleAsync(
            new PacketWriter().U16(GameSession.TakeAchievementRewardRq).I32(board.Achievement).U8(1).ToArray(),
            Now + 101);
        var updateIndex = reward.FindIndex(p => Op(p) == GameSession.AchievementCountUpdateNt);
        var rsIndex = reward.FindIndex(p => Op(p) == GameSession.TakeAchievementRewardRs);
        Assert.True(updateIndex >= 0 && rsIndex > updateIndex);
        Assert.Equal(0, BitConverter.ToInt32(reward[rsIndex], 2));
    }
}
