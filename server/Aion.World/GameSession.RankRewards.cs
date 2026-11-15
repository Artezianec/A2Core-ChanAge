using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Rank rewards while a season runs. Ported from zaraza87/A2Core dell-merge-2026-11 (29934bea).
//
// Reward buffs. SeasonRankingReward rows carry Light/DarkRewardBuffList (SkillAbnormal 20100 "Abyss Tier Rewards", 20101/20102 the
// arenas, 20103 Nightmare, 20104 Subjugation, 20105/20106 Ascension/Transcendence; StatChange rows per level from SkillAbnormalEffectLv).
// Client text: "Buffs are applied based on current Class Rank. If there are multiple buffs of the same type, only the highest-Grade
// buff of that type will be applied." OBSERVED upstream (397 official 0x3645 with abnormal 20100): the level is the class grade's row of
// the season's class reward group; expire = the season's end; applied again at each hourly ranking update. Applied here at the first
// tick in the world and at every list update; removed (reason 7, CANDIDATE) when no grade gives it any more. The families
// (RankTables.BuffFamilies) keep the highest level of each. Not saved with the other buffs: every entry computes them afresh.
//
// Weekly Abyss rewards. GlobalSetting midseason_class_ranking_reward_use true / Weekly_Reward_Class_Abyss (class grades 1-4), Post.dat
// 26 "Abyss Class Rank Reward has been sent.", valid 7 days. At the weekly reset (Wednesday 16:00 KST = 07:00 UTC) the Abyss class list
// as of that moment pays the week that ended. game.season_weekly_settlements (migration 130): the store writes the week's row and its
// mails in one transaction and does nothing when the row exists, so a week pays once. CANDIDATE: no recording spans a reset.
public sealed partial class GameSession
{
    public const int WeeklyRewardPost = 25, WeeklyClassRewardPost = 26, WeeklyRewardValidDays = 7;
    const long RankRetryMs = 60_000;
    long rankBuffCheckAt;
    long weeklyCheckAt;

    /// <summary>The reward buffs of a player's current entries: the class group's row of the class grade and the common group's row of
    /// the whole list's grade, the highest level per abnormal, then per family the highest level (ties: the lower abnormal id).</summary>
    public static List<(int Abnormal, int Level)> RankBuffsFor(IEnumerable<(RankTables.Contents Contents, SeasonEntry Entry)> entries, int race)
    {
        var all = new List<(int Abnormal, int Level)>();
        foreach (var (contents, entry) in entries)
        {
            all.AddRange(RankTables.RewardBuffs(Rank.RewardOf(contents.RewardClass, entry.Class, entry.ClassGrade), race));
            all.AddRange(RankTables.RewardBuffs(Rank.RewardOf(contents.RewardCommon, 0, entry.Grade), race));
        }
        var best = all.Where(b => b.Level > 0).GroupBy(b => b.Abnormal).Select(g => (Abnormal: g.Key, Level: g.Max(b => b.Level))).ToList();
        return best.Where(b => RankTables.BuffFamilies.FirstOrDefault(f => f.Contains(b.Abnormal)) is not { } family
                || !best.Any(o => o.Abnormal != b.Abnormal && family.Contains(o.Abnormal) && (o.Level > b.Level || o.Level == b.Level && o.Abnormal < b.Abnormal)))
            .OrderBy(b => b.Abnormal).ToList();
    }

    /// <summary>The session tick's rank work: the week's rewards when a weekly reset passed, the reward buffs at each list update.</summary>
    async Task<List<byte[]>> RankRewardsTickAsync(long now, CancellationToken ct)
    {
        if (Store is null || CharacterId is null) return [];
        await FlushSeasonPointsAsync(ct);
        if (now >= weeklyCheckAt) await SettleWeekAsync(now, ct);
        return resources is not null && now >= rankBuffCheckAt ? await RefreshRankBuffsAsync(now, ct) : [];
    }

    async Task<List<byte[]>> RefreshRankBuffsAsync(long now, CancellationToken ct)
    {
        rankBuffCheckAt = now + RankRetryMs;
        if (CharacterId is not { } me) return [];
        var season = Rank.SeasonAt(now);
        var entries = new List<(RankTables.Contents, SeasonEntry)>();
        var next = SeasonSnapshot.NextUpdateAfter(now, 60);
        try
        {
            if (season is not null)
                foreach (var contents in Rank.RankedContents(season.No))
                {
                    if (await SnapshotAsync(contents.Type, season.No, now, ct) is not { } snapshot) continue;
                    next = Math.Min(next, snapshot.NextUpdate);
                    if (snapshot.Of(me) is { } entry) entries.Add((contents, entry));
                }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Note($"rank buffs not refreshed: {e.GetType().Name}: {e.Message}");
            return [];
        }
        rankBuffCheckAt = season is null ? now + RankRetryMs : Math.Min(next, season.End);
        var wanted = season is null ? new List<(int Abnormal, int Level)>() : RankBuffsFor(entries, profileRace);
        var replies = new List<byte[]>();
        var speedBefore = BuffStat("MoveSpeed");
        var before = buffs.Where(b => b.Rank).Select(b => (b.AbnormalId, b.Level)).OrderBy(b => b.AbnormalId).ToList();
        replies.AddRange(RemoveBuffs(buffs.Where(b => b.Rank && !wanted.Any(w => w.Abnormal == b.AbnormalId)).ToList(), ExpireEtc));
        foreach (var (abnormal, level) in wanted)
        {
            var duration = (int)Math.Clamp(season!.End - now, 1, int.MaxValue);
            if (AddBuff(abnormal, duration, 0, now, replies) is not { } b) continue;
            b.Rank = true;
            b.Level = level;
            b.ExpireAt = season.End;
            b.Stats.Clear();
            foreach (var (stat, value) in Rank.BuffStats(abnormal, level)) b.Stats[stat] = value;
        }
        if (!before.SequenceEqual(wanted.Select(w => (w.Abnormal, w.Level)))) replies.AddRange(BuffStatsChanged(speedBefore));
        return replies;
    }

    /// <summary>The rank reward buffs the player has: (abnormal, level, expire time).</summary>
    public IReadOnlyList<(int Abnormal, int Level, long Expire)> RankBuffs => buffs.Where(b => b.Rank).Select(b => (b.AbnormalId, b.Level, b.ExpireAt)).ToList();

    /// <summary>The weekly reset at or before <paramref name="now"/> pays the week that ended there, once.</summary>
    async Task SettleWeekAsync(long now, CancellationToken ct)
    {
        var reset = Rank.WeekStartAt(now);
        var cache = Rankings;
        weeklyCheckAt = now + RankRetryMs;
        if (cache.WeekSettled(reset) || Store is not { } store) { weeklyCheckAt = reset + RankTables.WeekMs; return; }
        var season = Rank.SeasonAt(reset - 1);
        var contents = season is null ? null : Rank.ContentsOf(season.No, SeasonContentsAbyss);
        var weekly = Rank.Weekly;
        if (season is null || contents is not { Ranked: true } || !weekly.ClassUse && !weekly.CommonUse)
        {
            cache.MarkWeekSettled(reset);
            weeklyCheckAt = reset + RankTables.WeekMs;
            return;
        }
        try
        {
            var rows = await store.SeasonRankingAsync(SeasonContentsAbyss, season.No, 20_000, ct);
            var list = SeasonSnapshot.Build(SeasonContentsAbyss, season.No, contents, rows, r => ClassOf(r.CreationId, r.Gender), reset, reset, null, null,
                Rank.MinPoint(SeasonContentsAbyss));
            var mails = WeeklyRewardMails(list, weekly, r => data.Profile(r.CreationId, r.Gender)?.Race ?? 1);
            var sent = await store.SettleWeeklyRankingAsync(SeasonContentsAbyss, season.No, DateTimeOffset.FromUnixTimeMilliseconds(reset), mails,
                TimeSpan.FromDays(WeeklyRewardValidDays), ct);
            cache.MarkWeekSettled(reset);
            weeklyCheckAt = reset + RankTables.WeekMs;
            if (sent is null) return;   // settled before (another session, an earlier run)
            Note($"weekly Abyss rewards of the week to {DateTimeOffset.FromUnixTimeMilliseconds(reset):u}: {sent.Count} mails");
            foreach (var (character, post) in sent)
                if (World?.FindOnline(character) is { } online)
                    online.Send(WritePost(PacketWriter.Opcode(PostRecvNt), post).ToArray());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Note($"weekly rewards not settled: {e.GetType().Name}: {e.Message}");   // tried again after RankRetryMs
        }
    }

    /// <summary>The weekly reward mails of a list: Post.dat 26 with the class grade's items, Post.dat 25 with the whole list's (only when
    /// GlobalSetting turns them on), the receiver's race's items.</summary>
    public static List<WeeklyRewardMail> WeeklyRewardMails(SeasonSnapshot list, RankTables.WeeklySettings weekly, Func<SeasonRankingRow, int> raceOf)
    {
        var mails = new List<WeeklyRewardMail>();
        foreach (var e in list.Entries)
        {
            var race = raceOf(e.Row);
            if (weekly.ClassUse && RankTables.RewardItems(Rank.RewardOf(weekly.ClassGroup, e.Class, e.ClassGrade), race) is { Count: > 0 } classItems)
                mails.Add(new WeeklyRewardMail(e.Row.Character, WeeklyClassRewardPost, classItems.Select(i => (i.Item, i.Count)).ToList()));
            if (weekly.CommonUse && RankTables.RewardItems(Rank.RewardOf(weekly.CommonGroup, 0, e.Grade), race) is { Count: > 0 } items)
                mails.Add(new WeeklyRewardMail(e.Row.Character, WeeklyRewardPost, items.Select(i => (i.Item, i.Count)).ToList()));
        }
        return mails;
    }

    internal Task<List<byte[]>> RankRewardsTickForTestsAsync(long now) => RankRewardsTickAsync(now, CancellationToken.None);
}
