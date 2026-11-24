using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class AchievementBatchGroupCollectTests
{
    const long Now = 1_793_368_800_000;
    const int GlobalAttendanceGroup = 510_035;
    const int GrowthCompleteAchievement = 301_100_300;

    static int Op(byte[] packet) => GameSessionTestHelpers.Op(packet);

    [Fact]
    public async Task Truncated_batch_request_still_returns_568a()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var batch = await session.HandleAsync(
            new PacketWriter().U16(GameSession.TakeAchievementRewardBatchRq).U8(1).I32(0).ToArray(),
            Now + 50);
        var rs = Assert.Single(batch, p => Op(p) == GameSession.TakeAchievementRewardBatchRs);
        Assert.Equal(1, BitConverter.ToInt32(rs, 2));
    }

    [Fact]
    public async Task Per_group_attendance_batch_matches_client_payload_and_grants()
    {
        var store = new FakeCharacterStore();
        GameSessionTestHelpers.SeedAttendanceCheckedInNotRewarded(store, Now + 100);
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var board = GameSession.LatestAttendanceSeasons(1, Now + 100).Single(e => e.Board.SeasonGroupId == 37).Board;

        var batch = await session.HandleAsync(
            Convert.FromHexString("89560753c8070000"),
            Now + 101);

        Assert.Contains(batch, p => Op(p) == 0x561b);
        var rs = batch.First(p => Op(p) == GameSession.TakeAchievementRewardBatchRs);
        Assert.Equal(0, BitConverter.ToInt32(rs, 2));
        Assert.Equal(7, rs[6]);
        Assert.Equal(GlobalAttendanceGroup, BitConverter.ToInt32(rs, 7));
        Assert.Equal(1, store.Achievements.Single(a => a.Id == board.Achievement).RewardedStep);
    }

}