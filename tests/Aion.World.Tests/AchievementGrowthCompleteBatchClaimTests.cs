using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class AchievementGrowthCompleteBatchClaimTests
{
    const long Now = 1_793_368_800_000;
    const int GrowthCompleteAchievement = 301_100_300;
    const int GrowthCompleteGroup = 300_000_102;

    static int Op(byte[] packet) => GameSessionTestHelpers.Op(packet);

    [Fact]
    public async Task Batch_claim_step_two_sends_inventory_before_568a()
    {
        var store = new FakeCharacterStore();
        store.Achievements.Add(new AchievementRecord(
            GrowthCompleteAchievement, 300, null, 1, DateTimeOffset.FromUnixTimeMilliseconds(Now)));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);

        var batch = await session.HandleAsync(
            new PacketWriter().U16(GameSession.TakeAchievementRewardBatchRq).U8(1).I32(0).U8(0).ToArray(),
            Now + 101);

        var rsIndex = batch.FindIndex(p => Op(p) == GameSession.TakeAchievementRewardBatchRs);
        var updateIndex = batch.FindIndex(p => Op(p) == GameSession.AchievementCountUpdateNt);
        var invIndex = batch.FindIndex(p => Op(p) == 0x561b);
        Assert.True(rsIndex >= 0);
        Assert.True(invIndex >= 0 && invIndex < rsIndex, "0x561b precedes TakeAchievementRewardBatch_RS (capture 001158)");
        Assert.Equal(-1, updateIndex);
        Assert.DoesNotContain(batch, p => Op(p) == GameSession.AchievementListNt);
        Assert.Equal(0, BitConverter.ToInt32(batch[rsIndex], 2));
        Assert.Equal(2, store.Achievements.Single(a => a.Id == GrowthCompleteAchievement).RewardedStep);

        var body = batch[rsIndex].AsSpan(2).ToArray();
        Assert.Equal(
            Convert.FromHexString(
                "00000000" + "01" + "00000000" + "00" + "00" + "01" +
                "66A3E111" + "00" + "01" + "0C6DF211" + "02"),
            body);
    }
}
