using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

[CollectionDefinition("PlaytestDebug", DisableParallelization = true)]
public sealed class PlaytestDebugTestCollection;

[Collection("PlaytestDebug")]
public sealed class AchievementCollectAllRewardsTests
{
    const long Now = 1_793_368_800_000;
    const int GrowthCompleteAchievement = 301_100_300;
    const int Kina = GameSession.KinaItemId;
    const int GrowthStepTwoOrb = 530_116_006;

    static int Op(byte[] packet) => GameSessionTestHelpers.Op(packet);

    [Fact]
    public async Task Category_zero_collect_all_grants_pending_step_with_561b()
    {
        var store = new FakeCharacterStore();
        store.Achievements.Add(new AchievementRecord(
            GrowthCompleteAchievement, 300, null, 1, DateTimeOffset.FromUnixTimeMilliseconds(Now)));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);

        var batch = await session.HandleAsync(
            new PacketWriter().U16(GameSession.TakeAchievementRewardBatchRq).U8(0).I32(0).U8(0).ToArray(),
            Now + 50);

        Assert.Contains(batch, p => Op(p) == 0x561b);
        Assert.Equal(0, BitConverter.ToInt32(batch.First(p => Op(p) == GameSession.TakeAchievementRewardBatchRs), 2));
        var record = store.Achievements.Single(a => a.Id == GrowthCompleteAchievement);
        Assert.Equal(2, record.RewardedStep);
        Assert.Contains(store.Items, i => i.TemplateId == Kina && i.Quantity >= 4000);
        Assert.Contains(store.Items, i => i.TemplateId == GrowthStepTwoOrb && i.Quantity >= 1);
    }

    [Fact]
    public void Category_matches_supports_collect_all_summary_category_zero()
    {
        Assert.True(GameSession.CategoryMatches(1, "Achievement"));
        Assert.True(GameSession.CategoryMatches(0, "Unlock"));
        Assert.False(GameSession.CategoryMatches(1, "Unlock"));
    }

    [Fact]
    public async Task Batch_claim_logs_achievement_reward_playtest_action()
    {
        var store = new FakeCharacterStore();
        store.Achievements.Add(new AchievementRecord(
            GrowthCompleteAchievement, 300, null, 1, DateTimeOffset.FromUnixTimeMilliseconds(Now)));
        var capture = new CapturePlaytestSink();
        PlaytestDebug.Sink = capture;
        try
        {
            var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
            await session.HandleAsync(
                new PacketWriter().U16(GameSession.TakeAchievementRewardBatchRq).U8(1).I32(0).U8(0).ToArray(),
                Now + 50);
            var rewards = capture.Logged.Where(a => a.Kind == "achievement_reward" && a.Fields["achievementId"] is int id && id == GrowthCompleteAchievement).ToList();
            var reward = Assert.Single(rewards, a => a.Fields["step"] is int s && s == 2);
            Assert.Equal("batch", reward.Fields["source"]);
            Assert.Contains($"{Kina}x4000", (string)reward.Fields["grants"]!);
        }
        finally
        {
            PlaytestDebug.Sink = null;
        }
    }

    [Fact]
    public async Task Second_batch_claim_is_refused()
    {
        var store = new FakeCharacterStore();
        store.Achievements.Add(new AchievementRecord(
            GrowthCompleteAchievement, 300, null, 2, DateTimeOffset.FromUnixTimeMilliseconds(Now)));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);
        var batch = await session.HandleAsync(
            new PacketWriter().U16(GameSession.TakeAchievementRewardBatchRq).U8(1).I32(0).U8(0).ToArray(),
            Now + 50);
        Assert.Equal(1, BitConverter.ToInt32(batch.First(p => Op(p) == GameSession.TakeAchievementRewardBatchRs), 2));
        Assert.DoesNotContain(batch, p => Op(p) == 0x561b);
    }

    sealed class CapturePlaytestSink : IPlaytestDebugSink
    {
        public bool Packets => false;
        public bool Actions => true;
        public bool Entities => false;

        public List<(string Kind, Dictionary<string, object?> Fields)> Logged { get; } = [];

        public void Packet(in PlaytestPacketRecord record) { }

        public void Action(string kind, IReadOnlyDictionary<string, object?> fields) =>
            Logged.Add((kind, new Dictionary<string, object?>(fields)));

        public void EntitySpawn(int entityKey, int datId, string? name) { }
    }
}
