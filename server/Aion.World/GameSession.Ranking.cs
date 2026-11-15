using System.Globalization;
using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Season ranking (the client's Ranking window; SeasonRanking.dat: BossChallenge, PartyChallenge, Abyss, ArenaSingle/Team, Awaken;
// UpdatePeriod 60 min). Ported from zaraza87/A2Core dell-merge-2026-11 (29934bea), adapted to our boss challenges, Abyss ranking
// points, arena and dungeon clears. The 0x8d run (catalogue 0x398e + 0x53c5, verified with tools/packet_structs.py):
//   SeasonSchedule_NT 0x8d53 (ours: the attendance rows, plus the live season's rows of the ranked contents here)
//   RetrieveLatestSeasonRanking_RQ 0x8d54 {ESeasonContentsType u8, ECharacterClass u8 (0 all)} -> RS 0x8d55
//   RetrieveMyLatestSeasonRanking_RQ 0x8d56 {type, class} -> RS 0x8d57 (the client asks {Abyss, 0} at every entry)
//   MyPrevSeasonRanking_NT 0x8d58 (entry; OBSERVED "00"), TakePrevSeasonRankingReward_RQ 0x8d59 {type} -> RS 0x8d5a
// SeasonRankingData OBSERVED upstream (8 official lists of 376-500 entries): rank i32 | dbid i64 | nickname str | guild str | guild
// emblem u16 | level i32 | conqueror level i32 | ECharacterClass u8 | point i64 | extra display JSON str | rank change i32 | previous
// season rank i32 | grade i32 | grade display i32. List RS: u16 0 | type | class | entries varint | next update i64 | total ranked i32.
// My RS: result u16 | type | class | entry | total i32 | next update i64; nobody ranked = the OBSERVED 403e (SeasonRanking_NoRanker).
//
// Rules: points count per live season (game.season_ranking_points, migration 129); the lists update every 60 minutes (SharedWorld
// .Rankings) ordered by the best clear's points, then the ranking point; grades by RankTables.GradeOf; at the season's end the lists
// are final (game.season_ranking_results, written at the first entry after it) and their rewards can be taken in the reward period.
// Points: boss challenge clear = 4000 x (tier - 1) + 300 x (level + 1) - seconds (OBSERVED on 230 clears); Ascension trial
// 7900 - 5 x seconds at difficulty 4, 4620 - 3 x seconds at difficulty 3 (OBSERVED; others CANDIDATE); Transcendence ~ 1000 x stage
// + 600 - 0.12 x stage x seconds (CANDIDATE fit); the ranking point is the season's sum of clear points. Abyss: our Abyss ranking
// point (GameSession.Abyss.cs), MinRankingPoint 1500. Arena: a rating from 1500, Elo K 32 per match (CANDIDATE).
public sealed partial class GameSession
{
    public const byte SeasonContentsAbyss = 1, SeasonContentsPartyChallenge = 4, SeasonContentsArenaSingle = 5, SeasonContentsArenaTeam = 6,
        SeasonContentsAwaken = 21;
    public const ushort LatestSeasonRankingRq = 0x8d54, LatestSeasonRankingRs = 0x8d55, MyPrevSeasonRankingNt = 0x8d58,
        TakePrevSeasonRankingRewardRq = 0x8d59, TakePrevSeasonRankingRewardRs = 0x8d5a;
    public const int SeasonRankingListMax = 500;
    /// <summary>EResult SeasonRanking_NoRewardToTake 15937 / SeasonRanking_NoRewardableTime 15938 (the enum after NoRanker 15936).</summary>
    public const int SeasonNoRewardToTake = 15937, SeasonNoRewardableTime = 15938;
    public const long ArenaStartRating = 1500;
    public const int ArenaRatingFactor = 32;
    static RankTables Rank => RankTables.Embedded;

    static readonly Dictionary<string, byte> ClassIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Novice"] = 1, ["Gladiator"] = 2, ["Templar"] = 3, ["Ranger"] = 4, ["Assassin"] = 5, ["Elementalist"] = 6, ["Sorcerer"] = 7,
        ["Cleric"] = 8, ["Chanter"] = 9, ["Fighter"] = 12,
    };

    byte ClassOf(int creationId, int gender) => data.Profile(creationId, gender) is { } p && ClassIds.TryGetValue(p.Class, out var id) ? id : (byte)0;
    byte MyClass => className is { } name && ClassIds.TryGetValue(name, out var id) ? id : ClassOf(character?.CreationId ?? 0, character?.Gender ?? 0);

    int seasonNo;
    readonly Dictionary<int, SeasonPointRow> seasonPoints = [];
    readonly Dictionary<(int Type, int Season), SeasonPointRow> seasonPointsDirty = [];
    List<SeasonResultRow> prevSeasonResults = [];
    int prevSeasonNo;
    /// <summary>Tests only: a ranking cache when the session has no shared world.</summary>
    SeasonRankingCache? ownRankings;
    SeasonRankingCache Rankings => World?.Rankings ?? (ownRankings ??= new SeasonRankingCache());

    public long SeasonPointOf(int type) => seasonPoints.GetValueOrDefault(type)?.Point ?? 0;
    public SeasonPointRow? SeasonRowOf(int type) => seasonPoints.GetValueOrDefault(type);
    public IReadOnlyList<SeasonResultRow> PrevSeasonResults => prevSeasonResults;

    /// <summary>World entry: the season's points, the ended season finalized (its results written once) and this character's results.
    /// A store without the tables (migration 129 not applied) leaves everything empty.</summary>
    async Task LoadSeasonRankingAsync(INativeCharacterStore store, Guid character, long now, CancellationToken ct)
    {
        seasonPoints.Clear();
        seasonPointsDirty.Clear();
        prevSeasonResults = [];
        prevSeasonNo = 0;
        (rankBuffCheckAt, weeklyCheckAt) = (0, 0);
        seasonNo = Rank.SeasonAt(now)?.No ?? 0;
        try
        {
            if (seasonNo > 0)
                foreach (var row in await store.SeasonPointsAsync(character, seasonNo, ct)) seasonPoints[row.ContentsType] = row;
            if (Rank.RewardSeasonAt(now) is { } ended)
            {
                await FinalizeSeasonAsync(store, ended, ct);
                prevSeasonNo = ended.No;
                prevSeasonResults = (await store.SeasonResultsAsync(ended.No, 0, character, ct)).ToList();
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Note($"season ranking not loaded: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>The final results of an ended season, written once for every ranked type (at the first entry after its end).</summary>
    async Task FinalizeSeasonAsync(INativeCharacterStore store, RankTables.Season ended, CancellationToken ct)
    {
        if (Rankings.Finalized(ended.No)) return;
        foreach (var contents in Rank.RankedContents(ended.No))
        {
            if ((await store.SeasonResultsAsync(ended.No, contents.Type, null, ct)).Count > 0) continue;
            var rows = await store.SeasonRankingAsync(contents.Type, ended.No, 20_000, ct);
            if (rows.Count == 0) continue;
            var final = SeasonSnapshot.Build(contents.Type, ended.No, contents, rows, r => ClassOf(r.CreationId, r.Gender), ended.End, ended.End, null, null, Rank.MinPoint(contents.Type));
            var written = await store.SaveSeasonResultsAsync(final.Results(), ct);
            Note($"season {ended.No} type {contents.Type}: {written} final results written");
        }
        Rankings.MarkFinalized(ended.No);
    }

    /// <summary>A season ranking as of its last update (rebuilt from the store when the update time passed).</summary>
    async Task<SeasonSnapshot?> SnapshotAsync(int type, int season, long now, CancellationToken ct)
    {
        if (Store is not { } store || season <= 0) return null;
        var cache = Rankings;
        if (cache.Current(type, season, now) is { } held) return held;
        await FlushSeasonPointsAsync(ct);   // this player's own changes count in the update
        var rows = await StoredValue(() => store.SeasonRankingAsync(type, season, 20_000, ct), (IReadOnlyList<SeasonRankingRow>)[]);
        var prevRanks = season > 1
            ? (await StoredValue(() => store.SeasonResultsAsync(season - 1, type, null, ct), (IReadOnlyList<SeasonResultRow>)[])).ToDictionary(r => r.Character, r => r.Rank)
            : null;
        var snapshot = SeasonSnapshot.Build(type, season, Rank.ContentsOf(season, type), rows, r => ClassOf(r.CreationId, r.Gender), now,
            SeasonSnapshot.NextUpdateAfter(now, Rank.UpdateMinutes(type)), cache.Latest(type, season), prevRanks, Rank.MinPoint(type));
        cache.Store(snapshot);
        return snapshot;
    }

    /// <summary>0x8d54 / 0x8d56 / 0x8d59, else null.</summary>
    async Task<List<byte[]>?> RankingRequestAsync(byte[] plain, long now, CancellationToken ct)
    {
        var opcode = Opcode(plain);
        if (opcode == TakePrevSeasonRankingRewardRq) return await TakeSeasonRewardAsync(plain, now, ct);
        if (opcode is not (LatestSeasonRankingRq or MySeasonRankingRq) || plain.Length < 4) return null;
        var type = plain[2];
        var cls = plain[3];
        var season = Rank.SeasonAt(now);
        var snapshot = season is not null && Rank.ContentsOf(season.No, type) is { Ranked: true } ? await SnapshotAsync(type, season.No, now, ct) : null;
        var next = snapshot?.NextUpdate ?? SeasonSnapshot.NextUpdateAfter(now, Rank.UpdateMinutes(type));
        var list = snapshot?.List(cls) ?? [];
        if (opcode == LatestSeasonRankingRq)
        {
            var w = PacketWriter.Opcode(LatestSeasonRankingRs).U16(0).U8(type).U8(cls).Varint((uint)Math.Min(list.Count, SeasonRankingListMax));
            for (var i = 0; i < list.Count && i < SeasonRankingListMax; i++) WriteSeasonEntry(w, list[i], cls);
            return [w.I64(next).I32(list.Count).ToArray()];
        }
        var mine = CharacterId is { } me ? snapshot?.Of(me) : null;
        if (mine is null || cls != 0 && mine.Class != cls)
        {
            var body = (byte[])SeasonRankingNone.Clone();
            body[2] = type;
            body[3] = cls;
            return [PacketWriter.Opcode(MySeasonRankingRs).Raw(body).I64(next).ToArray()];
        }
        var my = PacketWriter.Opcode(MySeasonRankingRs).U16(0).U8(type).U8(cls);
        WriteSeasonEntry(my, mine, cls);
        return [my.I32(list.Count).I64(next).ToArray()];
    }

    /// <summary>One SeasonRankingData in the observed layout: the whole list's rank, change and grade for class 0, else the class list's.</summary>
    public static void WriteSeasonEntry(PacketWriter w, SeasonEntry e, byte cls) =>
        WriteSeasonEntry(w, cls == 0 ? e.Rank : e.ClassRank, e.Row, e.Class, cls == 0 ? e.RankChange : e.ClassRankChange, e.PrevSeasonRank,
            cls == 0 ? e.Grade : e.ClassGrade, e.Display);

    public static void WriteSeasonEntry(PacketWriter w, int rank, SeasonRankingRow row, byte cls, int change = 0, int prevSeasonRank = 0, int grade = 0, int display = 0) =>
        w.I32(rank).I64(row.WireId).String(row.Name).String(row.Guild).U16((ushort)row.GuildEmblem).I32(row.Level).I32(0).U8(cls).I64(row.Point).String(row.Extra)
            .I32(change).I32(prevSeasonRank).I32(grade).I32(display);

    // ---------------------------------------------------------------- points

    void EnsureSeason(long now)
    {
        var current = Rank.SeasonAt(now)?.No ?? 0;
        if (current == seasonNo) return;
        seasonNo = current;
        seasonPoints.Clear();
    }

    long RankNow => lastNow == 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : lastNow;

    void SetSeasonPoints(int type, long point, long best, string extra)
    {
        EnsureSeason(RankNow);
        if (seasonNo <= 0) return;   // between seasons nothing counts (CANDIDATE)
        var row = new SeasonPointRow(type, seasonNo, point, best, extra);
        seasonPoints[type] = row;
        seasonPointsDirty[(type, seasonNo)] = row;
    }

    /// <summary>A clear of a ranked content: its points added to the season's sum; its extra JSON kept when it is the best clear.</summary>
    void AddClearPoints(int type, long points, string extra)
    {
        EnsureSeason(RankNow);
        var row = seasonPoints.GetValueOrDefault(type);
        var best = row?.BestPoint ?? 0;
        SetSeasonPoints(type, (row?.Point ?? 0) + points, Math.Max(best, points), points >= best || row is null ? extra : row.Extra);
        Note($"season points: type {type} +{points} = {(row?.Point ?? 0) + points}");
    }

    /// <summary>Session tick and logout: changed season points saved.</summary>
    async Task FlushSeasonPointsAsync(CancellationToken ct)
    {
        if (seasonPointsDirty.Count == 0 || Store is not { } store || CharacterId is not { } character) return;
        var rows = seasonPointsDirty.Values.ToList();
        seasonPointsDirty.Clear();
        foreach (var row in rows) await Stored(async () => await store.SaveSeasonPointsAsync(character, row, ct) ? [[]] : []);
    }

    /// <summary>OBSERVED (230 boss challenge clears of the official lists): best_clear_point + best_clear_point_time = 4000 x (tier - 1)
    /// + 300 x (level + 1). At least 1 (CANDIDATE floor).</summary>
    public static long BossClearPoints(int tier, int level, int seconds) => Math.Max(1, 4000L * (Math.Max(1, tier) - 1) + 300L * (level + 1) - Math.Max(0, seconds));

    /// <summary>Ascension trial clear points: OBSERVED 7900 - 5 x seconds at difficulty 4, 4620 - 3 x seconds at difficulty 3; the other
    /// difficulties CANDIDATE.</summary>
    public static long AwakenClearPoints(int difficulty, int seconds)
    {
        seconds = Math.Max(0, seconds);
        var points = difficulty switch
        {
            >= 4 => 7900L + 3280L * (difficulty - 4) - 5L * seconds,
            3 => 4620L - 3L * seconds,
            _ => 1540L * Math.Max(1, difficulty) - (long)Math.Max(1, difficulty) * seconds,
        };
        return Math.Max(1, points);
    }

    /// <summary>Transcendence clear points (CANDIDATE fit of the official levels 2-4).</summary>
    public static long PartyClearPoints(int level, int seconds) => Math.Max(1000L * level / 2, (long)Math.Round(1000.0 * level + 600 - 0.12 * level * Math.Max(0, seconds)));

    /// <summary>A boss challenge clear: the season's points with the official extra JSON.</summary>
    void RecordBossChallengeSeason(int groupId, int level, long points, int seconds) =>
        AddClearPoints(SeasonContentsBossChallenge, points, string.Create(CultureInfo.InvariantCulture,
            $"{{\"boss_group_id\":{groupId},\"best_clear_point_level\":{level},\"best_clear_point_time\":{seconds},\"best_clear_point\":{points}}}"));

    // ---------------------------------------------------------------- Abyss

    static (long Kills, long Deaths, long Assists) AbyssCounts(string? extra)
    {
        if (string.IsNullOrEmpty(extra)) return (0, 0, 0);
        try
        {
            using var doc = JsonDocument.Parse(extra);
            long Get(string k) => doc.RootElement.TryGetProperty(k, out var v) && v.TryGetInt64(out var n) ? n : 0;
            return (Get("kill_count"), Get("death_count"), Get("assist_count"));
        }
        catch (JsonException) { return (0, 0, 0); }
    }

    /// <summary>The Abyss season row follows our Abyss ranking point; kills counted in the extra JSON.</summary>
    void AbyssSeason(long rankingPoint, int kills)
    {
        var row = seasonPoints.GetValueOrDefault(SeasonContentsAbyss);
        var (k, d, a) = AbyssCounts(row?.Extra);
        SetSeasonPoints(SeasonContentsAbyss, Math.Max(0, rankingPoint), 0,
            string.Create(CultureInfo.InvariantCulture, $"{{\"kill_count\":{k + kills},\"death_count\":{d},\"assist_count\":{a}}}"));
    }

    // ---------------------------------------------------------------- arena

    static (long Play, long Win, long Lose) ArenaCounts(string? extra)
    {
        if (string.IsNullOrEmpty(extra)) return (0, 0, 0);
        try
        {
            using var doc = JsonDocument.Parse(extra);
            long Get(string k) => doc.RootElement.TryGetProperty(k, out var v) && v.TryGetInt64(out var n) ? n : 0;
            return (Get("play_count"), Get("win_count"), Get("lose_count"));
        }
        catch (JsonException) { return (0, 0, 0); }
    }

    /// <summary>The arena rating of a type (ArenaSingle 5 / ArenaTeam 6): 1500 before the first match.</summary>
    public long ArenaRating(int type) => seasonPoints.GetValueOrDefault(type)?.Point ?? ArenaStartRating;

    /// <summary>Elo change of one match: <paramref name="score"/> 1 won, 0 lost against the other team's mean rating.</summary>
    public static int ArenaRatingChange(long mine, double opponents, double score) =>
        (int)Math.Round(ArenaRatingFactor * (score - 1 / (1 + Math.Pow(10, (opponents - mine) / 400.0))));

    /// <summary>A finished arena match of this player (matching 1 Single, 2 Team): the rating and the counts.</summary>
    internal void ArenaRated(int matchingId, double score, double opponents)
    {
        var type = matchingId switch { 1 => SeasonContentsArenaSingle, 2 => SeasonContentsArenaTeam, _ => 0 };
        if (type == 0) return;
        var rating = ArenaRating(type);
        var (play, win, lose) = ArenaCounts(seasonPoints.GetValueOrDefault(type)?.Extra);
        var next = Math.Max(1, rating + ArenaRatingChange(rating, opponents, score));
        SetSeasonPoints(type, next, 0, string.Create(CultureInfo.InvariantCulture,
            $"{{\"play_count\":{play + 1},\"win_count\":{win + (score >= 1 ? 1 : 0)},\"lose_count\":{lose + (score <= 0 ? 1 : 0)}}}"));
    }

    // ---------------------------------------------------------------- the ended season

    /// <summary>MyPrevSeasonRanking_NT {count, {type u8, class u8, rank, total i32, point i64, extra str, reward taken bool, grade,
    /// grade display i32}} (usmap; OBSERVED "00" with none).</summary>
    public byte[] MyPrevSeasonRanking()
    {
        var w = PacketWriter.Opcode(MyPrevSeasonRankingNt).Varint((uint)prevSeasonResults.Count);
        foreach (var r in prevSeasonResults)
        {
            var byClass = Rank.ContentsOf(r.Season, r.ContentsType) is not { ShowsClassGrade: false };
            w.U8((byte)r.ContentsType).U8(byClass ? r.Class : (byte)0).I32(byClass ? r.ClassRank : r.Rank).I32(byClass ? r.ClassTotal : r.Total).I64(r.Point)
                .String(r.Extra).Bool(r.RewardTaken).I32(byClass ? r.ClassGrade : r.Grade).I32(r.GradeDisplay);
        }
        return w.ToArray();
    }

    /// <summary>The season reward items of a result: the common group by the whole list's grade and the class group by the class grade.</summary>
    public static List<(int Item, long Count)> SeasonRewardItems(SeasonResultRow result, int race)
    {
        var contents = Rank.ContentsOf(result.Season, result.ContentsType);
        if (contents is null) return [];
        return [.. RankTables.RewardItems(Rank.RewardOf(contents.RewardCommon, 0, result.Grade), race),
            .. RankTables.RewardItems(Rank.RewardOf(contents.RewardClass, result.Class, result.ClassGrade), race)];
    }

    /// <summary>TakePrevSeasonRankingReward_RQ {type u8} -> RS {result u16, type u8, has reward item bool}: once per type during the
    /// ended season's reward period; then MyPrevSeasonRanking_NT again.</summary>
    async Task<List<byte[]>> TakeSeasonRewardAsync(byte[] plain, long now, CancellationToken ct)
    {
        var type = plain.Length >= 3 ? plain[2] : (byte)0;
        byte[] Reply(int result, bool items) => PacketWriter.Opcode(TakePrevSeasonRankingRewardRs).U16(result).U8(type).Bool(items).ToArray();
        if (Rank.RewardSeasonAt(now) is not { } season || season.No != prevSeasonNo) return [Reply(SeasonNoRewardableTime, false)];
        var index = prevSeasonResults.FindIndex(r => r.ContentsType == type);
        if (index < 0 || prevSeasonResults[index].RewardTaken) return [Reply(SeasonNoRewardToTake, false)];
        var result = prevSeasonResults[index];
        if (Store is not { } store || CharacterId is not { } character || !await StoredValue(() => store.TakeSeasonRewardAsync(character, season.No, type, ct), false))
            return [Reply(SeasonNoRewardToTake, false)];
        prevSeasonResults[index] = result with { RewardTaken = true };
        var items = SeasonRewardItems(result, profileRace);
        if (items.Count > 0) pendingRewards.Add(items);
        Note($"season {season.No} reward type {type}: grade {result.Grade}/{result.ClassGrade}, {items.Count} items");
        return [Reply(0, items.Count > 0), MyPrevSeasonRanking()];
    }

    // ---------------------------------------------------------------- the season list

    /// <summary>
    /// The live season's rows for SeasonSchedule_NT 0x8d53, for the ranked contents of that season (OBSERVED upstream: every
    /// Season_Main_Live type lists latest = the live season, previous = Season_Main 1 in season 1, next = the following live season).
    /// Ours keeps the attendance rows; these are added for the types the attendance rows do not cover.
    /// </summary>
    internal static List<(byte Type, string Latest, int LatestNo, string Prev, int PrevNo, string Next, int NextNo)> RankedSeasonRows(long now)
    {
        if (Rank.SeasonAt(now) is not { } season) return [];
        var prev = season.No > 1 ? RankTables.LiveGroup : "Season_Main";
        var prevNo = season.No > 1 ? season.No - 1 : 1;
        var (next, nextNo) = Rank.LiveSeason(season.No + 1) is { } after ? (RankTables.LiveGroup, after.No) : ("", 0);
        return Rank.RankedContents(season.No).Select(c => c.Type).Where(t => t is > 0 and < 256).Distinct().Order()
            .Select(t => ((byte)t, RankTables.LiveGroup, season.No, prev, prevNo, next, nextNo)).ToList();
    }

    // ---------------------------------------------------------------- clears (Transcendence, Ascension trial)

    /// <summary>The season type a cleared dungeon scores in and its level, or null.</summary>
    static (byte Type, int Level)? SeasonClearOf(DungeonInfo room, int dungeonId)
    {
        if (room.Type == "Awaken") return (SeasonContentsAwaken, Math.Clamp(dungeonId % 10, 1, 9));
        if (Patch20261028.TranscendenceStage(dungeonId, room) is var stage and > 0) return (SeasonContentsPartyChallenge, stage);
        return null;
    }

    /// <summary>A Transcendence or Ascension trial clear: its points in the season ranking.</summary>
    void RecordSeasonClear(DungeonInfo room, int dungeonId, long elapsedMs)
    {
        if (SeasonClearOf(room, dungeonId) is not { } clear) return;
        var seconds = (int)Math.Max(0, elapsedMs / 1000);
        var points = clear.Type == SeasonContentsAwaken ? AwakenClearPoints(clear.Level, seconds) : PartyClearPoints(clear.Level, seconds);
        var extra = clear.Type == SeasonContentsAwaken
            ? JsonSerializer.Serialize(new { dungeon_id = dungeonId, dungeon_difficulty = clear.Level, best_clear_point_time = seconds, best_clear_point = points })
            : JsonSerializer.Serialize(new { dungeon_info = new[] { new { dungeon_group_id = PartyGroupNumber(room.Group), best_clear_level = clear.Level,
                best_clear_point_time = seconds, best_clear_point = points } } });
        AddClearPoints(clear.Type, points, extra);
    }

    internal void RecordSeasonClearForTests(int dungeonId, long elapsedMs) => RecordSeasonClear(MapEventData.Dungeon(dungeonId)!, dungeonId, elapsedMs);
    internal Task LoadSeasonRankingForTestsAsync(long now) =>
        Store is { } store && CharacterId is { } character ? LoadSeasonRankingAsync(store, character, now, CancellationToken.None) : Task.CompletedTask;

    /// <summary>DungeonGroup.dat ids of the party challenge groups (the official extra's dungeon_group_id, Arkanis 1006 OBSERVED).</summary>
    static readonly Dictionary<string, int> PartyGroupIds = new()
    {
        ["DeusCenter_G_Group"] = 1005, ["Arkanis_G_Group"] = 1006, ["SunkenTemple_G_Group"] = 1010, ["MirrorHeart_G_Group"] = 1011, ["FafniteGully_G_Group"] = 1015,
    };

    static int PartyGroupNumber(string group) => PartyGroupIds.GetValueOrDefault(group);
}
