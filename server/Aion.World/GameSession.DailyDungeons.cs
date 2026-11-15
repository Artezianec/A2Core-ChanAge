using Aion.Protocol.Native;

namespace Aion.World;

// Ported from zaraza87/A2Core dell-merge-2026-11 (29934bea, docs/DELL-CHANGES-2026-11.md), adapted to our dungeon copies and waves.
// The daily dungeons' run (DailyDungeons.cs has the tables and the messages):
// - the countdown: DungeonState_NT 0x6100 Process from the run's start to its deadline (the OBSERVED form of the other timed dungeons;
//   the length is CANDIDATE, see DailyDungeons.cs), Prepare first where the dungeon has a ready time (the buff choice window);
// - the monsters: the map's WaveStepData waves (tools/data/daily-waves.json; their events only ClearWave them, no StartWave) run on
//   our map event waves (GameSession.Bosses.cs): every order's spawner waits dormant, the waves start when the run's Process
//   begins and bring the next order when the one before is down; the map's other "_Multi_Monster_Wave_" spawners no wave names
//   stay dormant (as the source). At the end the waves stop and every monster still standing goes;
// - the points: each kill / object use worth DungeonPoint.dat points adds them while the run is on, DungeonPoint_NT 0x6104 shows the
//   total (CANDIDATE form);
// - the end: at the deadline or when the map event says DungeonClear: WaveDungeonCompleteResult_NT 0x6102 (CANDIDATE form) with the
//   points and the reward of the highest RewardDungeon tier reached, DungeonCompleteResult_NT 0x6101 (OBSERVED form), the state
//   Complete then End after Dungeon.dat's hold (30 s), then out. The reward is granted, the clear achievements count, the best run is
//   kept (setting "daily_dungeon_records") and sent as WaveDungeonInfo_NT 0x6103 (OBSERVED form) at every entry.
// CANDIDATE: a run that reaches the deadline is a completed run (paid by its points; no time bonus rule in the client data);
// grade 1 (the official records say 1).
public sealed partial class GameSession
{
    /// <summary>The grade a finished run records (OBSERVED: 1 in both official records; meaning unknown, CANDIDATE).</summary>
    public const int DailyGrade = 1;

    static DailyDungeons Dailies => DailyDungeons.Embedded;

    /// <summary>The character's best run per daily dungeon (setting "daily_dungeon_records" {dungeon: [grade, seconds, points]}).</summary>
    readonly Dictionary<int, DailyRecord> dailyRecords = [];
    bool dailyRecordsDirty;

    void LoadDailyRecords(Dictionary<string, int[]>? stored)
    {
        dailyRecords.Clear();
        foreach (var (key, v) in stored ?? [])
            if (int.TryParse(key, System.Globalization.CultureInfo.InvariantCulture, out var id) && v is [var grade, var seconds, var points])
                dailyRecords[id] = new DailyRecord(id, grade, seconds, points);
    }

    async Task FlushDailyRecordsAsync(CancellationToken ct)
    {
        if (!dailyRecordsDirty) return;
        dailyRecordsDirty = false;
        await SaveSettingAsync("daily_dungeon_records", dailyRecords.ToDictionary(p => p.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
            p => new[] { p.Value.Grade, p.Value.DurationS, p.Value.Point }), ct);
    }

    /// <summary>WaveDungeonInfo_NT at the world entry: this character's records (`00` with none, as before).</summary>
    byte[] WaveDungeonInfoAtEntry() => DailyDungeons.WaveDungeonInfo(dailyRecords.Values);

    internal IReadOnlyDictionary<int, DailyRecord> DailyRecordsForChecks => dailyRecords;

    /// <summary>When the run's countdown started (after the ready time).</summary>
    static long ProcessStart(DungeonCopy c) => c.ProcessAtMs > 0 ? c.ProcessAtMs : c.StartedMs;

    /// <summary>A kill in the host's daily copy while the run is on: its points.</summary>
    void DailyKilled(Target target, long now) => DailyPoints(d => DailyDungeons.KillPoints(d, target.NpcId), now);

    /// <summary>An object used in the host's daily copy (orbs, coins: UseEnvObj points).</summary>
    void DailyUsedObject(int envId, long now)
    {
        if (CopyOwner is { } owner) { owner.DailyUsedObject(envId, now); return; }
        DailyPoints(d => DailyDungeons.UsePoints(d, envId), now);
    }

    void DailyPoints(Func<DailyDungeon, int> worth, long now)
    {
        if (copy is not { Cleared: false, Failed: false, Phase: "Process" } c || c.Host != this || Dailies.Of(c.DungeonId) is not { } d) return;
        if (c.DeadlineMs > 0 && now >= c.DeadlineMs) return;
        var points = worth(d);
        if (points <= 0) return;
        c.Points += points;
        mapEventOut.Add(DailyDungeons.DungeonPoint(c.DungeonId, c.Points));
    }

    /// <summary>The host's tick: the waves start with the run's Process; the deadline ends a daily run with its points (instead of failing it).</summary>
    void DailyTick(long now)
    {
        if (copy is not { Cleared: false, Failed: false, Phase: "Process" } c || c.Host != this || Dailies.Of(c.DungeonId) is not { } d) return;
        if (!c.DailyWavesStarted)
        {
            c.DailyWavesStarted = true;
            foreach (var wave in DailyWaves(worldMap).Keys.Where(waveParts.ContainsKey)) ((IMapEventHost)this).StartWave(wave);
        }
        if (c.DeadlineMs > 0 && now >= c.DeadlineMs) DailyEnd(d, c, c.DeadlineMs, timeUp: true);
    }

    static readonly Lazy<Dictionary<int, Dictionary<string, string[]>>> dailyWaves = new(() =>
    {
        using var stream = typeof(GameSession).Assembly.GetManifestResourceStream("daily-waves.json")
            ?? throw new InvalidOperationException("daily-waves.json is not embedded");
        using var doc = System.Text.Json.JsonDocument.Parse(stream);
        return doc.RootElement.GetProperty("maps").EnumerateObject().ToDictionary(
            m => int.Parse(m.Name, System.Globalization.CultureInfo.InvariantCulture),
            m => m.Value.EnumerateObject().ToDictionary(w => w.Name, w => w.Value.EnumerateArray().Select(s => s.GetString()!).ToArray(), StringComparer.OrdinalIgnoreCase));
    });

    /// <summary>A daily map's WaveStepData waves: each order's spawner in order (tools/data/daily-waves.json).</summary>
    internal static IReadOnlyDictionary<string, string[]> DailyWaves(int map) =>
        dailyWaves.Value.TryGetValue(map, out var waves) ? waves : new Dictionary<string, string[]>();

    /// <summary>At the map's preparation: a daily map's wave spawners (and the wave spawners no wave names) wait dormant.</summary>
    void PrepareDailyWaves(IReadOnlySet<string> names)
    {
        if (Dailies.OfMap(worldMap) is null) return;
        var waves = DailyWaves(worldMap);
        foreach (var (wave, orders) in waves)
        {
            var parts = orders.Where(names.Contains).ToList();
            if (parts.Count == 0) continue;
            waveParts[wave] = parts;
            dormantSpawners.UnionWith(parts);
        }
        dormantSpawners.UnionWith(names.Where(n => n.Contains("_Multi_Monster_Wave_", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The run is over: the waves stop and the monsters still standing go (0x3642 act 7); the map's objects stay.</summary>
    void StopDailyMonsters()
    {
        foreach (var wave in waveRuns.Keys.ToList()) EndWaveRun(wave);
        foreach (var t in targets?.Values.Where(t => t.Hostile && !t.Dead && t.Key != Combat.TrainingDummyKey).ToList() ?? []) Vanish(t.Key);
    }

    internal void DailyTickForTests(long now) => DailyTick(now);
    internal void DailyKilledForTests(int npcId, long now) => DailyKilled(new Target { Key = 0, Name = "", HpMax = 1, NpcId = npcId }, now);
    internal void DailyUsedObjectForTests(int envId, long now) => DailyUsedObject(envId, now);

    /// <summary>The run ends: the result with the points and the reward, Complete then End after the hold, then out.</summary>
    void DailyEnd(DailyDungeon d, DungeonCopy c, long now, bool timeUp, string? mapEvent = null)
    {
        if (c.Cleared) return;
        c.Cleared = true;
        var duration = Math.Clamp(now - ProcessStart(c), 0, d.LimitS > 0 ? d.LimitS * 1000L : long.MaxValue);
        const int bonus = 0;   // CANDIDATE: no time-bonus rule in the client data
        var total = c.Points + bonus;
        var tier = DailyDungeons.Reward(d, profileRace, total);
        var items = tier?.Items.ToList() ?? [];
        Note($"daily dungeon {d.Name}: {(timeUp ? "time up" : "cleared")} after {duration / 1000} s, {total} points, reward {(tier is null ? "none" : $"tier {tier.RowId} ({tier.NeedPoint})")}");
        StopDailyMonsters();
        if (items.Count > 0) pendingRewards.Add(items);
        var room = MapEventData.Dungeon(d.Id);
        var hold = (room is { Hold: > 0 } ? room.Hold : d.HoldS > 0 ? d.HoldS : 30) * 1000L;
        c.ClearedMs = now;
        c.CompleteEndsMs = now + hold;
        c.EndSent = false;
        mapEventOut.Add(DailyDungeons.WaveDungeonCompleteResult(c.DungeonId, DailyGrade, duration, total, bonus, items));
        mapEventOut.Add(DungeonCompleteResult(c.DungeonId, true, items));
        mapEventOut.Add(DungeonState(c.DungeonId, DungeonStateComplete, now, now + hold));
        instanceExitAt ??= now + hold;
        // The best run (most points, then the shorter) is the record the dungeon list shows.
        var seconds = (int)(duration / 1000);
        if (!dailyRecords.TryGetValue(d.Id, out var best) || total > best.Point || total == best.Point && seconds < best.DurationS)
        {
            dailyRecords[d.Id] = new DailyRecord(d.Id, DailyGrade, seconds, total);
            dailyRecordsDirty = true;
            mapEventOut.Add(WaveDungeonInfoAtEntry());
        }
        if (room is not null)
        {
            var name = mapEvent ?? MapEventData.EventsOn(worldMap).FirstOrDefault()?.Name ?? room.Name;
            if (!clearedEvents.Contains(name)) clearedEvents.Add(name);
            SpendContentTickets(MapEventData.IdOf(room), "Clear", now);
            mapEventOut.AddRange(AchieveDungeonClear(room, name, 1));
            mapEventOut.AddRange(AchieveDungeonClearLimits(room, now - c.StartedMs, c.Deaths));
        }
    }
}
