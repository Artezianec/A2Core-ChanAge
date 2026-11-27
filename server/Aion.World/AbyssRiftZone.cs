using System.Globalization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Aion:AbyssRiftZone: the scheduled Abyss Rift Zone event (docs/ABYSS-RIFT-ZONE.md). Phases and the bosses of each come from the
/// client export (tools/data/abyss-rift-phases.json); the schedule, the entry limits and the wait are the KR patch notes of
/// 2026-06-17 (research/abyss-rifts-beritra). Everything marked GUESS is not in any client table or source.
/// </summary>
public sealed record AbyssRiftZoneOptions
{
    /// <summary>The event runs (default on). Off: no spawns, no notices, the rift's progress reply keeps its old static answer.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>The server clock's offset from UTC in hours for the schedule; null: Aion:FieldBosses:ServerUtcOffsetHours, else UTC+9 (a GUESS, as for the field bosses).</summary>
    public int? ServerUtcOffsetHours { get; init; }
    /// <summary>The rift whose phase table runs: 1 = AR1 (the client's only table; its spawner names AR1_RiftBoss_* and map 20 = AR1).</summary>
    public int RiftId { get; init; } = 1;
    /// <summary>The map the bosses are placed on: Reshanta Lower (AR1). The event's own map is not in the client data we have: a GUESS.</summary>
    public int MapId { get; init; } = 20;
    /// <summary>Entry limits (OFFICIAL: level 45+, item level 3,000+): who gets the notices; the entry request is not wired.</summary>
    public int MinLevel { get; init; } = 45;
    public int MinItemLevel { get; init; } = 3000;
    /// <summary>Seconds per unit of a Time phase end. The client says "5" for the 5-minute wait, so minutes (60); the 60 of phase 7 is then 60 minutes (a GUESS).</summary>
    public int TimeUnitSeconds { get; init; } = 60;
    /// <summary>The spawners have no coordinates in the client data: the bosses are placed on a ring of this radius (GUESS) around the arena centre.</summary>
    public double ArenaRadius { get; init; } = 4000;
    /// <summary>Arena centre (x, y, z); null: the map's start point (a GUESS).</summary>
    public double[]? ArenaCentre { get; init; }
    /// <summary>The arenas of groups A, B and C are this far apart along x (they are separate server pairs officially; GUESS).</summary>
    public double GroupSpacing { get; init; } = 20000;
    /// <summary>A server that comes up this long after a group's open does not start that group's run any more (a GUESS: the wait).</summary>
    public int LateStartMinutes { get; init; } = 5;

    public static AbyssRiftZoneOptions Default { get; } = new();

    public AbyssRiftZoneOptions Validated()
    {
        if (ServerUtcOffsetHours is < -12 or > 14) throw new InvalidOperationException("Aion:AbyssRiftZone:ServerUtcOffsetHours must be -12..14.");
        if (TimeUnitSeconds is < 1 or > 3600) throw new InvalidOperationException("Aion:AbyssRiftZone:TimeUnitSeconds must be 1..3600.");
        if (LateStartMinutes is < 0 or > 120) throw new InvalidOperationException("Aion:AbyssRiftZone:LateStartMinutes must be 0..120.");
        return this;
    }
}

/// <summary>The event's schedule: Tuesday and Thursday 22:00, matching groups A 22:00, B 22:05, C 22:10 (OFFICIAL, KR notes 2026-06-17).</summary>
public static class AbyssRiftZone
{
    static AbyssRiftZoneOptions options = AbyssRiftZoneOptions.Default;

    public static AbyssRiftZoneOptions Options => options;

    /// <summary>Aion:AbyssRiftZone for every session of this host (Program.cs; tests set it back with <see cref="AbyssRiftZoneOptions.Default"/>).</summary>
    public static void Configure(AbyssRiftZoneOptions value) => options = value.Validated();

    public static readonly (string Group, int OffsetMinutes)[] Groups = [("A", 0), ("B", 5), ("C", 10)];
    public static readonly DayOfWeek[] Days = [DayOfWeek.Tuesday, DayOfWeek.Thursday];
    public const int OpenHour = 22;

    public static int UtcOffsetHours => options.ServerUtcOffsetHours ?? FieldBosses.Options.ServerUtcOffsetHours ?? 9;

    /// <summary>The latest open of the group at or before <paramref name="now"/> (unix ms, server clock days), null when none in the last two days.</summary>
    public static long? LatestOpen(string group, long now)
    {
        var offset = Groups.First(g => g.Group == group).OffsetMinutes;
        var zone = UtcOffsetHours * 3_600_000L;
        var day = DateTimeOffset.FromUnixTimeMilliseconds(now + zone).UtcDateTime.Date;
        foreach (var back in new[] { 0, 1 })
        {
            var date = day.AddDays(-back);
            if (!Days.Contains(date.DayOfWeek)) continue;
            var open = new DateTimeOffset(date, TimeSpan.Zero).ToUnixTimeMilliseconds() - zone + (OpenHour * 60L + offset) * 60_000L;
            if (open <= now) return open;
        }
        return null;
    }

    /// <summary>The next open of the group after <paramref name="now"/>.</summary>
    public static long NextOpen(string group, long now)
    {
        var offset = Groups.First(g => g.Group == group).OffsetMinutes;
        var zone = UtcOffsetHours * 3_600_000L;
        var day = DateTimeOffset.FromUnixTimeMilliseconds(now + zone).UtcDateTime.Date;
        for (var ahead = 0; ahead <= 7; ahead++)
        {
            var date = day.AddDays(ahead);
            if (!Days.Contains(date.DayOfWeek)) continue;
            var open = new DateTimeOffset(date, TimeSpan.Zero).ToUnixTimeMilliseconds() - zone + (OpenHour * 60L + offset) * 60_000L;
            if (open > now) return open;
        }
        throw new InvalidOperationException("no open within a week");
    }

    /// <summary>OFFICIAL entry limits: character level 45+ and item level 3,000+.</summary>
    public static bool Eligible(int level, int itemLevel) => level >= options.MinLevel && itemLevel >= options.MinItemLevel;
}

/// <summary>
/// One run of the event: a phase machine over the client's phase table. A phase ends when ANY of its ends is met: Time (units of
/// <see cref="AbyssRiftZoneOptions.TimeUnitSeconds"/> since the phase began), KillNpcCount (that many of its monsters killed),
/// KillNpc (every listed monster killed). Pure state: no world access.
/// </summary>
public sealed class AbyssRiftZoneRun(AbyssRiftPhases.Phase[] phases, long openMs, int timeUnitSeconds)
{
    /// <summary>A phase beginning (<see cref="Spawn"/>: the monsters to place), or the end of the run.</summary>
    public sealed record Step(int Phase, int[] Spawn, bool Finished);

    readonly HashSet<int> killedIds = [];

    public long OpenMs => openMs;
    /// <summary>1-based phase index; 0 before the first <see cref="Advance"/>.</summary>
    public int Phase { get; private set; }
    public long PhaseStartMs { get; private set; }
    public bool Finished { get; private set; }
    /// <summary>Monsters killed in the current phase.</summary>
    public int Kills { get; private set; }
    public AbyssRiftPhases.Phase? Current => Phase is > 0 && Phase <= phases.Length ? phases[Phase - 1] : null;

    public void NoteKill(int npcId)
    {
        if (Finished || Current is null) return;
        Kills++;
        killedIds.Add(npcId);
    }

    /// <summary>The steps due at <paramref name="now"/> (a phase change, or the end), oldest first; none while the phase goes on.</summary>
    public List<Step> Advance(long now)
    {
        var steps = new List<Step>();
        if (Finished) return steps;
        if (Phase == 0) Begin(1, openMs, steps);
        else if (Ended(now)) Begin(Phase + 1, now, steps);
        return steps;
    }

    void Begin(int phase, long at, List<Step> steps)
    {
        if (phase > phases.Length)
        {
            Finished = true;
            steps.Add(new Step(Phase, [], true));
            return;
        }
        Phase = phase;
        PhaseStartMs = at;
        Kills = 0;
        killedIds.Clear();
        steps.Add(new Step(phase, phases[phase - 1].Npcs, false));
    }

    bool Ended(long now)
    {
        var ends = Current?.Ends ?? [];
        foreach (var end in ends)
        {
            switch (end.Type)
            {
                case "Time" when double.TryParse(end.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var units):
                    if (now - PhaseStartMs >= (long)(units * timeUnitSeconds * 1000)) return true;
                    break;
                case "KillNpcCount" when int.TryParse(end.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count):
                    if (Kills >= count) return true;
                    break;
                case "KillNpc":
                    var ids = end.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToList();
                    if (ids.Count > 0 && ids.All(killedIds.Contains)) return true;
                    break;
            }
        }
        return false;
    }
}

/// <summary>
/// The event on the shared world (one run per group at a time). Ticked by the sessions under the exclusive world lock like the
/// fortress siege: it spawns each phase's monsters on the map, notices their deaths and advances the run.
/// </summary>
public sealed class AbyssRiftZoneEvents
{
    sealed class Live(string group, AbyssRiftZoneRun run, Vector3d centre)
    {
        public string Group { get; } = group;
        public AbyssRiftZoneRun Run { get; } = run;
        public Vector3d Centre { get; } = centre;
        public Dictionary<int, int> Keys { get; } = [];   // target key → npc id, the live monsters of the phase
    }

    readonly Dictionary<string, Live> live = [];
    readonly HashSet<(string Group, long Open)> started = [];
    long nextTickMs;
    (int RiftId, byte WinRace)? finished;

    /// <summary>The result of a run that just ended, consumed once by the world tick (2.0.13 result packet).</summary>
    public (int RiftId, byte WinRace)? TakeFinished()
    {
        var value = finished;
        finished = null;
        return value;
    }

    const long TickEveryMs = 1000;

    public bool NeedsTick(long now) => AbyssRiftZone.Options.Enabled && now >= nextTickMs;

    /// <summary>The run of the rift that is on now (the earliest group when several are), null when none.</summary>
    public (string Group, int Phase, long PhaseStartMs, int Kills)? Current(int riftId)
    {
        if (!AbyssRiftZone.Options.Enabled || riftId != AbyssRiftZone.Options.RiftId) return null;
        var first = live.Values.OrderBy(l => l.Run.OpenMs).FirstOrDefault();
        return first is null ? null : (first.Group, first.Run.Phase, first.Run.PhaseStartMs, first.Run.Kills);
    }

    public IReadOnlyCollection<string> ActiveGroups => live.Keys;

    /// <summary>Runs one tick: starts a group's run at its open, advances the live ones. Returns the notices (English, Russian) to show to the eligible players.</summary>
    public List<(string English, string Russian)> Tick(SharedWorld world, long now)
    {
        var notices = new List<(string, string)>();
        nextTickMs = now + TickEveryMs;
        var options = AbyssRiftZone.Options;
        if (!options.Enabled || AbyssRiftPhases.Embedded.PhasesOf(options.RiftId) is not { Length: > 0 } phases) return notices;
        foreach (var (group, _) in AbyssRiftZone.Groups)
        {
            if (live.ContainsKey(group) || AbyssRiftZone.LatestOpen(group, now) is not { } open || started.Contains((group, open))) continue;
            started.Add((group, open));
            if (now - open > options.LateStartMinutes * 60_000L) continue;
            live[group] = new Live(group, new AbyssRiftZoneRun(phases, open, options.TimeUnitSeconds), ArenaCentre(world, group));
        }
        foreach (var run in live.Values.ToList())
        {
            var map = world.Map(options.MapId);
            foreach (var (key, npc) in run.Keys.ToList())
            {
                if (!map.Targets.TryGetValue(key, out var target) || !target.Dead) continue;
                run.Keys.Remove(key);
                map.RespawnAt.Remove(key);
                run.Run.NoteKill(npc);
            }
            foreach (var step in run.Run.Advance(now))
            {
                if (step.Finished)
                {
                    finished = (options.RiftId, 0);
                    Clear(map, run);
                    live.Remove(run.Group);
                    notices.Add(($"Abyss Rift Zone (group {run.Group}) is over.", $"Зона Бездны-разлома (группа {run.Group}) завершена."));
                    continue;
                }
                Place(world, map, run, step);
                notices.Add(PhaseNotice(run.Group, step.Phase, phases.Length, run.Run.Current!));
            }
        }
        return notices;
    }

    static (string, string) PhaseNotice(string group, int phase, int count, AbyssRiftPhases.Phase p) => phase == 1
        ? ($"Abyss Rift Zone (group {group}) opens: {p.Ends.FirstOrDefault()?.Value ?? "5"} minutes of combat wait.",
           $"Зона Бездны-разлома (группа {group}) открыта: ожидание боя {p.Ends.FirstOrDefault()?.Value ?? "5"} мин.")
        : ($"Abyss Rift Zone (group {group}): phase {phase}/{count}, {p.Npcs.Length} rift bosses appear.",
           $"Зона Бездны-разлома (группа {group}): фаза {phase}/{count}, появляются боссы разлома: {p.Npcs.Length}.");

    static Vector3d ArenaCentre(SharedWorld world, string group)
    {
        var options = AbyssRiftZone.Options;
        var index = Array.FindIndex(AbyssRiftZone.Groups, g => g.Group == group);
        var centre = options.ArenaCentre is { Length: >= 3 } c ? new Vector3d(c[0], c[1], c[2])
            : world.Data.MapStart(options.MapId)?.Position ?? Vector3d.Zero;
        return centre with { X = centre.X + index * options.GroupSpacing };
    }

    static void Place(SharedWorld world, MapInstance map, Live run, AbyssRiftZoneRun.Step step)
    {
        var radius = AbyssRiftZone.Options.ArenaRadius;
        for (var i = 0; i < step.Spawn.Length; i++)
        {
            var npc = step.Spawn[i];
            var angle = 2 * Math.PI * i / Math.Max(1, step.Spawn.Length);
            var at = run.Centre with { X = run.Centre.X + Math.Cos(angle) * radius, Y = run.Centre.Y + Math.Sin(angle) * radius };
            if (world.Data.NpcFromTemplate(npc, $"Rift {npc}", [at.X, at.Y, at.Z], 0) is not { } template) continue;
            foreach (var key in map.Spawn(template, at, 1)) run.Keys[key] = npc;
        }
    }

    static void Clear(MapInstance map, Live run)
    {
        var packets = new List<byte[]>();
        foreach (var key in run.Keys.Keys)
        {
            if (!map.Targets.TryGetValue(key, out var t) || t.CorpseRemoved && t.Dead) continue;
            t.Hp = 0;
            t.CorpseRemoved = true;
            map.RespawnAt.Remove(key);
            packets.AddRange(map.Ai.Killed(t));
            packets.Add(GamePackets.Death(key));
        }
        run.Keys.Clear();
        map.BroadcastAll(packets);
    }
}
