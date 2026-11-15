using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Xunit;

namespace Aion.World.Tests;

public sealed class SeasonRankingTests
{
    /// <summary>2026-11-15 12:00 UTC: inside live season 1 (2026-09-30 to 2026-12-16 07:00 UTC).</summary>
    static readonly long InSeason = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    /// <summary>2026-12-20: season 1's reward period.</summary>
    static readonly long RewardPeriod = new DateTimeOffset(2026, 12, 20, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    static ushort Op(byte[] p) => BitConverter.ToUInt16(p, 0);
    static byte[] Rq(ushort op, byte type, byte cls = 0) => new PacketWriter().U16(op).U8(type).U8(cls).ToArray();

    [Fact]
    public void Clear_points_follow_the_observed_formulas()
    {
        Assert.Equal(8600 - 30, GameSession.BossClearPoints(3, 1, 30));
        Assert.Equal(11300, GameSession.BossClearPoints(3, 10, 0));
        Assert.Equal(7595, GameSession.AwakenClearPoints(4, 61));
        Assert.Equal(4620 - 300, GameSession.AwakenClearPoints(3, 100));
        Assert.Equal(1, GameSession.BossClearPoints(1, 1, 10_000));
    }

    [Fact]
    public void The_live_season_and_the_weekly_reset_come_from_the_tables()
    {
        Assert.Equal(1, RankTables.Embedded.SeasonAt(InSeason)?.No);
        Assert.Equal(1, RankTables.Embedded.RewardSeasonAt(RewardPeriod)?.No);
        // Wednesday 2026-11-11 07:00 UTC (16:00 KST) is the reset before Sunday 2026-11-15.
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 7, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), RankTables.Embedded.WeekStartAt(InSeason));
        Assert.Contains(GameSession.RankedSeasonRows(InSeason), r => r.Type == GameSession.SeasonContentsAbyss && r.LatestNo == 1 && r.Next == RankTables.LiveGroup);
    }

    [Fact]
    public async Task Nobody_ranked_keeps_the_official_empty_reply()
    {
        var session = await GameSessionTestHelpers.EnterWorldAsync(new FakeCharacterStore(), InSeason);
        var reply = Assert.Single(await session.HandleAsync(Rq(0x8d56, GameSession.SeasonContentsAbyss), InSeason + 10));
        Assert.Equal(0x8d57, Op(reply));
        Assert.Equal(0x3e40, BitConverter.ToUInt16(reply, 2));
    }

    [Fact]
    public async Task A_clear_puts_the_player_on_the_list_with_its_rank()
    {
        var store = new FakeCharacterStore();
        store.SeasonOthers.Add(new SeasonRankingRow(Guid.NewGuid(), 99, "Other", 45, 1, 1, 9000, "{}", 9000));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, InSeason);
        session.RecordSeasonClearForTests(910014, 61_000);   // Ascension trial difficulty 4 in 61 s: 7595 points
        Assert.Equal(7595, session.SeasonPointOf(GameSession.SeasonContentsAwaken));
        var mine = Assert.Single(await session.HandleAsync(Rq(0x8d56, GameSession.SeasonContentsAwaken), InSeason + 10));
        Assert.Equal(0, BitConverter.ToUInt16(mine, 2));
        Assert.Equal(2, BitConverter.ToInt32(mine, 6));   // second behind the 9000-point clear
        var list = Assert.Single(await session.HandleAsync(Rq(0x8d54, GameSession.SeasonContentsAwaken), InSeason + 20));
        Assert.Equal(0x8d55, Op(list));
        Assert.Equal(2, list[6]);   // two entries
        Assert.Single(store.SeasonPointRows);   // saved before the list was built
    }

    [Fact]
    public async Task The_ended_seasons_reward_is_taken_once()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, RewardPeriod);
        var me = session.CharacterId!.Value;
        store.SeasonResultRows.Add(new SeasonResultRow(me, GameSession.SeasonContentsAbyss, 1, 2, 1, 10, 1, 3, 25_000_000, "{}", 0, 1, 1, false));
        await session.LoadSeasonRankingForTestsAsync(RewardPeriod);
        Assert.Single(session.PrevSeasonResults);
        var first = await session.HandleAsync(Rq(0x8d59, GameSession.SeasonContentsAbyss), RewardPeriod + 10);
        Assert.Equal(0, BitConverter.ToUInt16(first.First(p => Op(p) == 0x8d5a), 2));
        Assert.Contains(first, p => Op(p) == 0x8d58);
        Assert.True(store.SeasonResultRows[0].RewardTaken);
        var again = await session.HandleAsync(Rq(0x8d59, GameSession.SeasonContentsAbyss), RewardPeriod + 20);
        Assert.Equal(GameSession.SeasonNoRewardToTake, BitConverter.ToUInt16(Assert.Single(again), 2));
    }

    [Fact]
    public void Rank_buffs_keep_the_highest_of_a_family()
    {
        var contents = RankTables.Embedded.ContentsOf(1, GameSession.SeasonContentsAbyss)!;
        var row = new SeasonRankingRow(Guid.NewGuid(), 1, "A", 45, 1, 1, 25_000_000, "{}");
        var entry = new SeasonEntry(row, 2, 1, 1, 0, 1, 1, 0, 0, 0);
        var buffs = GameSession.RankBuffsFor([(contents, entry)], 1);
        Assert.Equal([(20100, 21)], buffs);
    }

    [Fact]
    public async Task The_weekly_abyss_rewards_are_mailed_once_per_week()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, InSeason);
        var me = session.CharacterId!.Value;
        store.SeasonPointRows.Add((me, new SeasonPointRow(GameSession.SeasonContentsAbyss, 1, 25_000_000, 0, "{}")));
        await session.RankRewardsTickForTestsAsync(InSeason + 1000);
        Assert.Single(store.WeeklySettled);
        Assert.Contains(store.Posts, p => p.DataId == GameSession.WeeklyClassRewardPost);
        var posts = store.Posts.Count;
        await session.RankRewardsTickForTestsAsync(InSeason + 120_000);
        Assert.Equal(posts, store.Posts.Count);
        Assert.Contains(session.RankBuffs, b => b.Abnormal == 20100);
    }
}
