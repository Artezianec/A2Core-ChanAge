using System.Threading.Channels;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Official WorldMapFieldNamed respawn timers (docs/FIELD-BOSSES.md), ported from KiskEmu (AGPL-3.0, commit d54d69472d).
// The client's WorldMapFieldNamed bosses (tools/data/field-bosses.json, 85) respawn on their own timer instead of
// GameSession.RespawnMs, and the kill and the respawn time are stored in game.field_bosses (migration 126), so a
// restart keeps a dead boss dead until its time. OBSERVED (KiskEmu official field boss tables, captures of 2026-11-02
// to 11-05): the respawn time is fixed at the kill and kept to the millisecond; the periods vary from kill to kill
// (101008 and 101009 ~3.0 h, 101006 at most 1.8 h, others at least 5.9 h; the big bosses 101021 and 101024 at least
// 10.8 h). The client data has no period (PeriodSpawnId names a server table), so the default ranges are CANDIDATE
// and a stored range can change them per boss.

/// <summary>A field boss: its WorldMapFieldNamed code (_data_id), map, NPC, spawn name and default respawn range.</summary>
public sealed record FieldBossDefinition(int Code, int MapId, int NpcId, string Kind, string Name, int DefaultMinMinutes, int DefaultMaxMinutes);

/// <summary>The host's field boss timers: definitions, the last kill and respawn of each, stored ranges; thread-safe.</summary>
public sealed class FieldBossTimers
{
    /// <summary>
    /// CANDIDATE default ranges in minutes, from KiskEmu's official captures. Named monsters (grade 1-5 below level 65):
    /// 1.5-6 h, covering the observed ~1.8 h to 5.9 h. Field bosses (grade 6+, or level 65+: the Abyss and the level-80
    /// regions): 6-12 h (observed at least 10.8 h for Gartua and Lagta).
    /// </summary>
    public const int NamedMinMinutes = 90, NamedMaxMinutes = 360, BossMinMinutes = 360, BossMaxMinutes = 720;
    /// <summary>The longest range that may be stored (7 days).</summary>
    public const int MaxMinutes = 7 * 24 * 60;

    sealed class State
    {
        public long? KilledAt, RespawnAt;
        public int? MinMinutes, MaxMinutes;
    }

    readonly Lock gate = new();
    readonly Dictionary<int, State> states = [];
    readonly Random random;
    readonly Channel<Func<IFieldBossStore, Task>> writes = Channel.CreateUnbounded<Func<IFieldBossStore, Task>>(new() { SingleReader = true });
    IFieldBossStore? store;

    public FieldBossTimers(GameData data, Random? random = null)
    {
        this.random = random ?? new Random();
        var list = new List<FieldBossDefinition>();
        foreach (var (code, mapId, npcId) in data.FieldBosses)
        {
            var spawn = data.NpcPlaced(npcId, mapId);
            var boss = spawn is null || spawn.Grade >= 6 || spawn.Level >= 65;
            list.Add(new FieldBossDefinition(code, mapId, npcId, boss ? "boss" : "named", spawn?.Name ?? "",
                boss ? BossMinMinutes : NamedMinMinutes, boss ? BossMaxMinutes : NamedMaxMinutes));
        }
        Definitions = list;
        ByCode = list.ToDictionary(d => d.Code);
        ByNpcAndMap = list.ToDictionary(d => (d.MapId, d.NpcId));
    }

    public IReadOnlyList<FieldBossDefinition> Definitions { get; }
    public IReadOnlyDictionary<int, FieldBossDefinition> ByCode { get; }
    public IReadOnlyDictionary<(int MapId, int NpcId), FieldBossDefinition> ByNpcAndMap { get; }

    /// <summary>Whether the stored timers were read (until then a kill is kept and stored, the rest is the default).</summary>
    public bool Loaded { get; private set; }

    /// <summary>
    /// The respawn range in minutes: a stored custom range, else a fixed <see cref="FieldBossOptions.RespawnMinutesByName"/>
    /// match, else the configured named or boss window (which defaults to the official CANDIDATE ranges).
    /// </summary>
    public (int Min, int Max) Range(int code)
    {
        var d = ByCode[code];
        lock (gate)
            if (states.TryGetValue(code, out var s) && s.MinMinutes is { } min && s.MaxMinutes is { } max)
                return (min, max);
        foreach (var (part, minutes) in FieldBosses.Options.RespawnMinutesByName.OrderByDescending(p => p.Key.Length))
            if (d.Name.Contains(part, StringComparison.OrdinalIgnoreCase)) return (minutes, minutes);
        var o = FieldBosses.Options;
        return d.Kind == "boss"
            ? (o.BossRespawnMinMinutes, o.BossRespawnMaxMinutes)
            : (o.NamedRespawnMinMinutes, o.NamedRespawnMaxMinutes);
    }

    /// <summary>The boss died now: its respawn time (Unix ms), drawn from its range, stored.</summary>
    public long Killed(int code, long now)
    {
        var (min, max) = Range(code);
        long respawn;
        lock (gate)
        {
            respawn = now + (long)((min + random.NextDouble() * (max - min)) * 60_000);
            var s = StateOf(code);
            s.KilledAt = now;
            s.RespawnAt = respawn;
        }
        Persist(code);
        return respawn;
    }

    /// <summary>When the boss died (Unix ms), while it waits for its respawn.</summary>
    public long? KilledAt(int code, long now)
    {
        lock (gate) return states.TryGetValue(code, out var s) && s.RespawnAt > now ? s.KilledAt : null;
    }

    /// <summary>The stored respawn time while it is still ahead.</summary>
    public long? DeadUntil(int code, long now)
    {
        lock (gate) return states.TryGetValue(code, out var s) && s.RespawnAt > now ? s.RespawnAt : null;
    }

    /// <summary>The boss is due now (the last kill stays on record). Used by staff "respawn now" / "respawn all".</summary>
    public void RespawnNow(int code, long now)
    {
        lock (gate)
        {
            var s = StateOf(code);
            if (s.RespawnAt is null || s.RespawnAt <= now) return;
            s.RespawnAt = now;
        }
        Persist(code);
    }

    /// <summary>A respawn range in minutes (null, null: the default again). False when out of bounds.</summary>
    public bool SetRange(int code, int? min, int? max)
    {
        if (min is null != max is null) return false;
        if (min is { } a && max is { } b && (a < 1 || b < a || b > MaxMinutes)) return false;
        lock (gate)
        {
            var s = StateOf(code);
            s.MinMinutes = min;
            s.MaxMinutes = max;
        }
        writes.Writer.TryWrite(store => store.SaveTimerAsync(code, min, max));
        return true;
    }

    /// <summary>Takes the stored rows (a kill this host made since it started and the ranges set here win).</summary>
    public void Apply(IReadOnlyList<FieldBossRecord> rows)
    {
        lock (gate)
        {
            foreach (var row in rows)
            {
                if (!ByCode.ContainsKey(row.Code)) continue;
                var s = StateOf(row.Code);
                var killed = row.KilledAt?.ToUnixTimeMilliseconds();
                if (s.KilledAt is null || killed > s.KilledAt)
                {
                    s.KilledAt = killed;
                    s.RespawnAt = row.RespawnAt?.ToUnixTimeMilliseconds();
                }
                if (s.MinMinutes is null && row.MinMinutes is { } min && row.MaxMinutes is { } max)
                {
                    s.MinMinutes = min;
                    s.MaxMinutes = max;
                }
            }
            Loaded = true;
        }
    }

    /// <summary>Stores the timers in <paramref name="target"/> from now on (one writer, in order).</summary>
    public void Attach(IFieldBossStore target, Action<string>? log)
    {
        store = target;
        _ = Task.Run(async () =>
        {
            await foreach (var write in writes.Reader.ReadAllAsync())
                try { await write(target); }
                catch (Exception error) { log?.Invoke($"Field boss timer not stored: {error.Message}"); }
        });
    }

    /// <summary>Waits until every write queued so far is stored (tests; a no-op without a store).</summary>
    public Task FlushAsync()
    {
        if (store is null) return Task.CompletedTask;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        writes.Writer.TryWrite(_ => { done.SetResult(); return Task.CompletedTask; });
        return done.Task;
    }

    State StateOf(int code)
    {
        if (!states.TryGetValue(code, out var s)) states[code] = s = new State();
        return s;
    }

    void Persist(int code)
    {
        long? killed, respawn;
        lock (gate) { killed = states[code].KilledAt; respawn = states[code].RespawnAt; }
        writes.Writer.TryWrite(store => store.SaveStateAsync(code,
            killed is { } k ? DateTimeOffset.FromUnixTimeMilliseconds(k) : null, respawn is { } r ? DateTimeOffset.FromUnixTimeMilliseconds(r) : null));
    }
}

/// <summary>Reads the stored field boss timers once the world server starts (retried until it works) and stores changes.</summary>
public static class FieldBossWorld
{
    public static void Configure(IFieldBossStore? store, SharedWorld? world, Action<string>? log)
    {
        if (world is null || store is null) return;
        world.FieldBossTimers.Attach(store, log);
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await LoadAsync(store, world);
                    log?.Invoke($"Field bosses: {world.FieldBossTimers.Definitions.Count} timers loaded");
                    return;
                }
                catch (Exception error)
                {
                    log?.Invoke($"Field boss timers unavailable: {error.Message}");
                }
                await Task.Delay(TimeSpan.FromSeconds(10));
            }
        });
    }

    /// <summary>Reads the stored timers and applies them to the maps already loaded.</summary>
    public static async Task LoadAsync(IFieldBossStore store, SharedWorld world, CancellationToken ct = default)
    {
        var rows = await store.LoadAsync(ct);
        await world.Lock.EnterExclusiveAsync(ct);
        try
        {
            world.FieldBossTimers.Apply(rows);
            var now = world.Clock();
            foreach (var map in world.Maps) map.SyncFieldBosses(world.FieldBossTimers, now);
        }
        finally { world.Lock.ExitExclusive(); }
    }
}

/// <summary>A shared map's official field bosses (the first spawn of each boss's NPC on the map; later copies are ordinary monsters).</summary>
public sealed partial class MapInstance
{
    Dictionary<int, int> fieldBossCodes = [];   // target key → code
    Dictionary<int, int> fieldBossKeys = [];    // code → target key
    FieldBossTimers? fieldBossTimers;

    /// <summary>Finds this map's official field bosses and keeps the stored dead ones dead until their time (shared maps only).</summary>
    public void SyncFieldBosses(FieldBossTimers timers, long now)
    {
        if (Instance != 0) return;
        fieldBossTimers = timers;
        if (fieldBossKeys.Count == 0)
            foreach (var d in timers.Definitions.Where(d => d.MapId == MapId))
                if (Targets.Values.Where(t => t.NpcId == d.NpcId && t.OwnerKey == 0).MinBy(t => t.Key) is { } t)
                {
                    fieldBossCodes[t.Key] = d.Code;
                    fieldBossKeys[d.Code] = t.Key;
                }
        if (!FieldBosses.Options.Enabled) return;
        var packets = new List<byte[]>();
        foreach (var (code, key) in fieldBossKeys)
        {
            if (timers.DeadUntil(code, now) is not { } until || !Targets.TryGetValue(key, out var t)) continue;
            if (!t.Dead)
            {
                t.Hp = 0;
                packets.AddRange(Ai.Killed(t));
            }
            if (timers.KilledAt(code, now) is { } killed) t.KilledAtMs = killed;
            if (!t.CorpseRemoved) packets.Add(GamePackets.Death(key));
            t.CorpseRemoved = true;
            RespawnAt[key] = until;
            var rank = timers.ByCode[code].Kind == "boss" ? FieldRank.Boss : FieldRank.Named;
            FieldBosses.NoteKilled(new FieldBossState(MapId, t.Key, t.NpcId, t.Name, t.Level, rank,
                t.Position.X, t.Position.Y, t.Position.Z, Spawned: false, RespawnAtMs: until));
        }
        BroadcastAll(packets);
    }

    /// <summary>The official boss's target on this map (null when the map has none).</summary>
    public Target? FieldBossTarget(int code) => fieldBossKeys.TryGetValue(code, out var key) ? Targets.GetValueOrDefault(key) : null;

    /// <summary>
    /// The target died now: its respawn time when it is an official field boss (stored), else null (the caller's delay).
    /// Also records the clock for <see cref="FieldBosses.StatesOnMap"/>.
    /// </summary>
    public long? RecordFieldBossKill(Target target, long now)
    {
        if (!FieldBosses.Options.Enabled || fieldBossTimers is not { } timers || !fieldBossCodes.TryGetValue(target.Key, out var code))
            return null;
        target.KilledAtMs = now;
        var until = timers.Killed(code, now);
        var rank = timers.ByCode[code].Kind == "boss" ? FieldRank.Boss : FieldRank.Named;
        FieldBosses.NoteKilled(new FieldBossState(MapId, target.Key, target.NpcId, target.Name, target.Level, rank,
            target.Position.X, target.Position.Y, target.Position.Z, Spawned: false, RespawnAtMs: until));
        return until;
    }

    /// <summary>Staff: the boss appears at the next tick of a player on the map. False when it is not dead.</summary>
    public bool RespawnFieldBoss(int code, long now)
    {
        if (FieldBossTarget(code) is not { Dead: true } t) return false;
        fieldBossTimers?.RespawnNow(code, now);
        RespawnAt[t.Key] = now;
        return true;
    }

    /// <summary>The official field bosses among the dead that respawn now (staff "respawn all"): persist the early respawn.</summary>
    void RespawnFieldBosses(long now)
    {
        if (fieldBossTimers is not { } timers) return;
        foreach (var (code, key) in fieldBossKeys)
            if (Targets.TryGetValue(key, out var t) && t.Dead) timers.RespawnNow(code, now);
    }
}
