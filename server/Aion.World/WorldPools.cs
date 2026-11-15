using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// The random pools (hidden cubes, the Altgard treasure boxes) and the Spacetime Rifts of the four field maps, from the client tables
/// (tools/data/world-pools.json). Their placements are appended to the maps' objects after world-objects.json (WorldObjects.ObjectsOn),
/// so every key of world-objects.json stays what it was and they use the ordinary object keys, visibility and use. A rift placement
/// that world-objects.json already has (the spacetime_rift objects of 1010 / 1110) is not added twice: the existing object gets its
/// spawner name instead.
/// <para>
/// What the data says (client tables 2026-11-09):
/// - 206 pool records on 1010 / 1011 / 1110 / 1111 (95 / 26 / 59 / 26), 3722 positions, 2 EnvObjData ids each: two cubes up per record
///   at random positions of the record (CANDIDATE reading of "fewer ids than positions").
/// - 10 rifts (1110: 3, 1010: 3, 1111: 2, 1011: 2), each a PeriodSpawn Cycle row: every 180 min from 02:00 KST, all week, open for
///   RiftData LifeTime 600 s, at one of its record's 5 positions; usable 400 times (InteractCount), use time 2.5 s, level 45-100;
///   the move: TeleportEffect 30 to one of the 5 exit points of Teleport.dat RiftPortal_X_to_Y_Teleport_n.
/// </para>
/// Ported from zaraza87/A2Core dell-merge-2026-11 (29934bea); the schedule here is self-contained (KST cycle rows).
/// </summary>
public static class WorldPools
{
    /// <summary>A PeriodSpawn Cycle row: first opening (seconds after KST midnight), the cycle (minutes) and the week days (Sunday first, CANDIDATE).</summary>
    public sealed record RiftSchedule(int StartSecond, int IntervalMinutes, bool[] Days);

    /// <summary>A rift spawner: where it opens, its RiftData row, its schedule and its exit points on the other faction's map.</summary>
    public sealed record RiftInfo(string Spawner, int Map, int Env, string Rift, int RiftId, int ArrivalMap, int MinLevel, int MaxLevel,
        int LifeSeconds, int Limit, int InteractMs, int Effect, RiftSchedule Schedule, string? Teleport, IReadOnlyList<Vector3d> Exits);

    /// <summary>A hidden cube kind: its faction (EnvObjData Category HiddenCubeLight 1 / Dark 2), whether it opens only with a key,
    /// the RewardData tables without (RewardName) and with a key (BasicKeyRewards), and the key items (RequireValues, any one of them).</summary>
    public sealed record CubeInfo(int Env, string Name, int Faction, bool KeyOnly, bool OptionalKey, string Normal, string Key,
        IReadOnlyList<int> Keys, int KeyCount, bool RewardData);

    sealed record RiftRow(
        [property: JsonPropertyName("map")] int Map, [property: JsonPropertyName("env")] int Env, [property: JsonPropertyName("rift")] string Rift,
        [property: JsonPropertyName("rift_id")] int RiftId, [property: JsonPropertyName("arrival_map")] int ArrivalMap,
        [property: JsonPropertyName("min_level")] int MinLevel, [property: JsonPropertyName("max_level")] int MaxLevel,
        [property: JsonPropertyName("life_s")] int LifeSeconds,
        [property: JsonPropertyName("limit")] int Limit, [property: JsonPropertyName("interact_ms")] int InteractMs,
        [property: JsonPropertyName("effect")] int Effect, [property: JsonPropertyName("period")] JsonElement[] Period,
        [property: JsonPropertyName("teleport")] string? Teleport, [property: JsonPropertyName("exits")] double[][] Exits);

    sealed record CubeRow(
        [property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("faction")] int Faction,
        [property: JsonPropertyName("key_only")] bool KeyOnly, [property: JsonPropertyName("optional_key")] bool OptionalKey,
        [property: JsonPropertyName("normal")] string Normal, [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("keys")] int[] Keys, [property: JsonPropertyName("key_count")] int KeyCount,
        [property: JsonPropertyName("reward_data")] bool RewardData);

    sealed record FileData(
        [property: JsonPropertyName("maps")] Dictionary<string, WorldObject[]> Maps,
        [property: JsonPropertyName("rifts")] Dictionary<string, RiftRow> Rifts,
        [property: JsonPropertyName("cubes")] Dictionary<string, CubeRow> Cubes);

    sealed record Tables(Dictionary<int, WorldObject[]> Maps, Dictionary<string, RiftInfo> Rifts, Dictionary<int, CubeInfo> Cubes);

    static readonly Lazy<Tables> tables = new(() =>
    {
        using var stream = typeof(WorldPools).Assembly.GetManifestResourceStream("world-pools.json")
            ?? throw new InvalidOperationException("world-pools.json is not embedded");
        var file = JsonSerializer.Deserialize<FileData>(stream) ?? throw new InvalidDataException("world-pools.json");
        static int Int(string s) => int.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        var rifts = file.Rifts.ToDictionary(kv => kv.Key, kv =>
        {
            var r = kv.Value;
            var p = r.Period;
            if (p.Length < 5 || p[1].GetString() != "Cycle" || p[4].GetString() is not { Length: 7 } days)
                throw new InvalidDataException($"world-pools.json: rift {kv.Key} period");
            var schedule = new RiftSchedule(p[2].GetInt32(), Math.Max(1, p[3].GetInt32()), days.Select(c => c == '1').ToArray());
            return new RiftInfo(kv.Key, r.Map, r.Env, r.Rift, r.RiftId, r.ArrivalMap, r.MinLevel, r.MaxLevel, r.LifeSeconds, r.Limit, r.InteractMs,
                r.Effect, schedule, r.Teleport, r.Exits.Where(e => e.Length >= 3).Select(e => new Vector3d(e[0], e[1], e[2])).ToList());
        }, StringComparer.OrdinalIgnoreCase);
        var cubes = file.Cubes.ToDictionary(kv => Int(kv.Key), kv => new CubeInfo(Int(kv.Key), kv.Value.Name, kv.Value.Faction, kv.Value.KeyOnly,
            kv.Value.OptionalKey, kv.Value.Normal, kv.Value.Key, kv.Value.Keys, Math.Max(1, kv.Value.KeyCount), kv.Value.RewardData));
        foreach (var (map, objects) in file.Maps)
            foreach (var o in objects)
                if (o.Rift is { } spawner && !rifts.ContainsKey(spawner)) throw new InvalidDataException($"world-pools.json: map {map} names unknown rift {spawner}");
        return new Tables(file.Maps.ToDictionary(kv => Int(kv.Key), kv => kv.Value), rifts, cubes);
    });

    /// <summary>
    /// Adds the map's pool and rift placements to its composed object list (WorldObjects): an existing spacetime_rift object at a
    /// rift placement's position takes that spawner (same key as before); every other placement is appended.
    /// </summary>
    internal static void Compose(int mapId, List<WorldObject> objects)
    {
        if (tables.Value.Maps.GetValueOrDefault(mapId) is not { Length: > 0 } extra) return;
        var existing = objects.Count;
        foreach (var o in extra)
        {
            if (o.Rift is not null)
            {
                var same = -1;
                for (var i = 0; i < existing; i++)
                    if (objects[i].SpacetimeRift && objects[i].Rift is null && objects[i].Env == o.Env && Same(objects[i].Position, o.Position)) { same = i; break; }
                if (same >= 0) { objects[same] = objects[same] with { Rift = o.Rift, InteractTime = o.InteractTime }; continue; }
            }
            objects.Add(o);
        }
    }

    static bool Same(double[] a, double[] b) => Math.Abs(a[0] - b[0]) < 1 && Math.Abs(a[1] - b[1]) < 1 && Math.Abs(a[2] - b[2]) < 1;

    public static IReadOnlyDictionary<string, RiftInfo> Rifts => tables.Value.Rifts;
    public static RiftInfo? RiftOf(string? spawner) => spawner is not null ? tables.Value.Rifts.GetValueOrDefault(spawner) : null;
    public static CubeInfo? CubeOf(int env) => tables.Value.Cubes.GetValueOrDefault(env);
    public static IReadOnlyCollection<CubeInfo> Cubes => tables.Value.Cubes.Values;

    /// <summary>The faction of a field map's own players (Light 1010 / 1011 = 1, Dark 1110 / 1111 = 2), 0 for any other map.</summary>
    public static int FieldRace(int mapId) => mapId switch { 1010 or 1011 => 1, 1110 or 1111 => 2, _ => 0 };

    // ---------- Rift schedule (KST) ----------

    public const long KstOffsetMs = 9 * 3_600_000L;
    const long DayMs = 86_400_000L;

    /// <summary>The openings (Unix ms) of a schedule on the KST day starting at <paramref name="kstDay"/> (KST-shifted ms).</summary>
    static IEnumerable<long> OpeningsOn(RiftSchedule s, long kstDay)
    {
        var weekday = (int)DateTimeOffset.FromUnixTimeMilliseconds(kstDay).DayOfWeek;
        if (s.Days.Length == 7 && !s.Days[weekday]) yield break;
        for (var t = s.StartSecond * 1000L; t < DayMs; t += s.IntervalMinutes * 60_000L) yield return kstDay + t - KstOffsetMs;
    }

    static long KstDay(long now) => Math.DivRem(now + KstOffsetMs, DayMs, out var rem) * DayMs - (rem < 0 ? DayMs : 0);

    /// <summary>The last opening at or before <paramref name="now"/> (Unix ms), within the last week.</summary>
    public static long? LastOpening(RiftInfo rift, long now)
    {
        var day = KstDay(now);
        for (var d = 0; d <= 7; d++)
            if (OpeningsOn(rift.Schedule, day - d * DayMs).Where(t => t <= now).DefaultIfEmpty(long.MinValue).Max() is var last && last != long.MinValue)
                return last;
        return null;
    }

    /// <summary>The next opening after <paramref name="now"/> (Unix ms), within the next week.</summary>
    public static long? NextOpening(RiftInfo rift, long now)
    {
        var day = KstDay(now);
        for (var d = 0; d <= 7; d++)
            if (OpeningsOn(rift.Schedule, day + d * DayMs).Where(t => t > now).DefaultIfEmpty(long.MaxValue).Min() is var next && next != long.MaxValue)
                return next;
        return null;
    }

    /// <summary>When the rift's current opening began (Unix ms), or null while it is closed (RiftData LifeTime).</summary>
    public static long? OpenedAt(RiftInfo rift, long now) =>
        LastOpening(rift, now) is { } last && now < last + rift.LifeSeconds * 1000L ? last : null;

    /// <summary>
    /// Which of the spawner's positions an opening uses: one picked per opening (CANDIDATE: no rift was recorded), the same for every
    /// map copy and session (a stable hash of the spawner and the opening time, not a stored roll).
    /// </summary>
    public static int PointOf(RiftInfo rift, long openedAt, int count)
    {
        if (count <= 1) return 0;
        var h = 2166136261u;
        foreach (var c in rift.Spawner.ToLowerInvariant()) h = (h ^ c) * 16777619u;
        var t = (ulong)(openedAt / 60_000);
        for (var i = 0; i < 8; i++) h = (h ^ (byte)(t >> (8 * i))) * 16777619u;
        return (int)(h % (uint)count);
    }

    /// <summary>The placements (object indexes on the map) of each rift spawner, in their MapData order.</summary>
    public static IReadOnlyList<int> RiftPoints(int mapId, string spawner) => riftPoints.GetOrAdd((mapId, spawner.ToLowerInvariant()), key =>
        WorldObjects.ObjectsOn(key.Map).Select((o, i) => (o, i)).Where(x => string.Equals(x.o.Rift, spawner, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.i).ToArray());

    static readonly System.Collections.Concurrent.ConcurrentDictionary<(int Map, string Spawner), int[]> riftPoints = new();

    /// <summary>Whether a rift placement stands now: its spawner is open and this opening uses this position.</summary>
    public static bool RiftShown(int mapId, int index, WorldObject o, long now, out long openedAt)
    {
        openedAt = 0;
        if (RiftOf(o.Rift) is not { } rift || OpenedAt(rift, now) is not { } opened) return false;
        var points = RiftPoints(mapId, rift.Spawner);
        openedAt = opened;
        return points.Count > 0 && points[PointOf(rift, opened, points.Count)] == index;
    }

    // ---------- Pools ----------

    /// <summary>A pool record: the kinds of its slots and, per position, the object index of each kind there.</summary>
    public sealed record Pool(int Key, IReadOnlyList<int> Slots, IReadOnlyList<Dictionary<int, int>> Positions);

    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (Dictionary<int, Pool> Pools, Dictionary<int, (int Pool, int Position)> ByIndex)> pools = new();

    static (Dictionary<int, Pool> Pools, Dictionary<int, (int Pool, int Position)> ByIndex) PoolsOn(int mapId) => pools.GetOrAdd(mapId, id =>
    {
        var objects = WorldObjects.ObjectsOn(id);
        var byPool = new Dictionary<int, (int[] Slots, List<(double X, double Y, double Z)> Order, Dictionary<(double, double, double), Dictionary<int, int>> At)>();
        for (var i = 0; i < objects.Count; i++)
        {
            if (objects[i] is not { Pool: { } key } o) continue;
            if (!byPool.TryGetValue(key, out var p)) byPool[key] = p = (o.PoolSlots is { Length: > 0 } s ? s : [o.Env], [], []);
            var at = (o.Position[0], o.Position[1], o.Position[2]);
            if (!p.At.TryGetValue(at, out var kinds)) { p.At[at] = kinds = []; p.Order.Add(at); }
            kinds[o.Env] = i;
        }
        var result = new Dictionary<int, Pool>();
        var byIndex = new Dictionary<int, (int, int)>();
        foreach (var (key, p) in byPool)
        {
            var positions = p.Order.Select(at => p.At[at]).ToList();
            result[key] = new Pool(key, p.Slots, positions);
            for (var n = 0; n < positions.Count; n++)
                foreach (var index in positions[n].Values) byIndex[index] = (key, n);
        }
        return (result, byIndex);
    });

    public static IReadOnlyDictionary<int, Pool> PoolsOnMap(int mapId) => PoolsOn(mapId).Pools;
    public static (int Pool, int Position)? PoolPlace(int mapId, int index) => PoolsOn(mapId).ByIndex.TryGetValue(index, out var p) ? p : null;
}

/// <summary>
/// The random pools and rift uses of one copy of a field map (MapInstance.Pools; a session without a shared world keeps its own):
/// which position each pool slot stands on, since when, and when an opened slot comes back; how often each rift opening was used.
/// Thread-safe (the map's sessions may ask from their own loops).
/// </summary>
public sealed class PoolState(int mapId)
{
    readonly object gate = new();
    readonly Dictionary<int, (int Position, long Since, long Due)[]> slots = [];
    readonly Dictionary<(string Spawner, long Opened), int> riftUses = [];

    public int MapId { get; } = mapId;

    /// <summary>A placement shown on the map: the pool slot standing there (since when), or null. Settles due slots first.</summary>
    public (int Slot, long Since)? Standing(int index, Random rng, long now)
    {
        if (WorldPools.PoolPlace(MapId, index) is not { } place || !WorldPools.PoolsOnMap(MapId).TryGetValue(place.Pool, out var pool)) return null;
        lock (gate)
        {
            var state = Settle(pool, rng, now);
            for (var s = 0; s < state.Length; s++)
                if (state[s].Position == place.Position && pool.Positions[place.Position].GetValueOrDefault(pool.Slots[s], -1) == index) return (s, state[s].Since);
            return null;
        }
    }

    /// <summary>Opens (takes) the slot standing on this placement: it waits <paramref name="respawnMs"/>, then comes back at a random free
    /// position of its pool. False when nothing stands there (already opened by someone else).</summary>
    public bool Take(int index, Random rng, long now, long respawnMs)
    {
        if (WorldPools.PoolPlace(MapId, index) is not { } place || !WorldPools.PoolsOnMap(MapId).TryGetValue(place.Pool, out var pool)) return false;
        lock (gate)
        {
            var state = Settle(pool, rng, now);
            for (var s = 0; s < state.Length; s++)
                if (state[s].Position == place.Position && pool.Positions[place.Position].GetValueOrDefault(pool.Slots[s], -1) == index)
                {
                    state[s] = (-1, 0, now + Math.Max(0, respawnMs));
                    return true;
                }
            return false;
        }
    }

    /// <summary>The positions the pool's slots stand on now (tests and staff).</summary>
    public IReadOnlyList<int> Positions(int poolKey, Random rng, long now)
    {
        if (!WorldPools.PoolsOnMap(MapId).TryGetValue(poolKey, out var pool)) return [];
        lock (gate) return Settle(pool, rng, now).Select(s => s.Position).ToList();
    }

    (int Position, long Since, long Due)[] Settle(WorldPools.Pool pool, Random rng, long now)
    {
        if (!slots.TryGetValue(pool.Key, out var state))
        {
            slots[pool.Key] = state = new (int, long, long)[pool.Slots.Count];
            for (var s = 0; s < state.Length; s++) state[s] = (-1, 0, long.MinValue);
        }
        for (var s = 0; s < state.Length; s++)
        {
            if (state[s].Position >= 0 || state[s].Due > now) continue;
            var free = Enumerable.Range(0, pool.Positions.Count)
                .Where(p => pool.Positions[p].ContainsKey(pool.Slots[s]) && state.All(x => x.Position != p)).ToList();
            if (free.Count == 0) continue;
            var since = state[s].Due == long.MinValue ? 0 : state[s].Due;
            state[s] = (free[rng.Next(free.Count)], since, 0);
        }
        return state;
    }

    /// <summary>Counts one use of a rift opening; false when its InteractCount limit is reached.</summary>
    public bool UseRift(string spawner, long openedAt, int limit)
    {
        lock (gate)
        {
            var used = riftUses.GetValueOrDefault((spawner, openedAt));
            if (limit > 0 && used >= limit) return false;
            riftUses[(spawner, openedAt)] = used + 1;
            foreach (var old in riftUses.Keys.Where(k => k.Opened < openedAt - 86_400_000L).ToList()) riftUses.Remove(old);
            return true;
        }
    }

    public int RiftUses(string spawner, long openedAt)
    {
        lock (gate) return riftUses.GetValueOrDefault((spawner, openedAt));
    }
}

public sealed partial class MapInstance
{
    PoolState? pools;
    /// <summary>The random pools and rift uses of this copy of the map (WorldPools).</summary>
    public PoolState Pools => LazyInitializer.EnsureInitialized(ref pools, () => new PoolState(MapId));
}
