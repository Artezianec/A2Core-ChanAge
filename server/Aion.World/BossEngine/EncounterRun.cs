using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>One engine log line: what fired, how many players it reached, how it went (also sent to PlaytestDebug "boss_mechanic").</summary>
public sealed record MechanicLogEntry(long AtMs, string Rule, string Action, int Targets, string Result);

/// <summary>A cast the engine queued into the boss's slam pipeline (NpcAi.Bosses): its definition and the rule that queued it.</summary>
public sealed record EngineCast(ActionDef Def, string Rule);

/// <summary>A persistent hazard: puddles, whirlpools, an outer-arena DoT, a moving wall grid.</summary>
public sealed class MechanicZone
{
    public required string Id { get; init; }
    public required string Rule { get; init; }
    public required ActionDef Def { get; init; }
    public required IReadOnlyList<(Vector3d Centre, double Yaw)> Centres { get; init; }
    public long StartedMs { get; init; }
    public long EndsMs { get; set; }
    public long NextTickMs { get; set; }
    public long TickMs { get; init; }
    public bool BlocksMovement { get; init; }
    /// <summary>The wall grid's current walls (grid zones only).</summary>
    public List<GridWalls.Rect> Walls { get; } = [];
    public int PatternIndex { get; set; }
    public long NextStepMs { get; set; }
}

/// <summary>A cover object (a pillar): players behind it, seen from the boss, are not hit by casts with "cover": true.</summary>
public sealed record MechanicCover(string Id, Vector3d At, double Depth, double HalfWidth, long EndsMs, int EnvSlot);

public sealed class MechanicShield
{
    public required string Id { get; init; }
    public required string Rule { get; init; }
    public required ActionDef Def { get; init; }
    public bool Invuln { get; init; }
    public bool WasImmune { get; init; }
    public double Pool { get; set; }
    public int LockedHp { get; set; }
    public long EndsMs { get; init; }
}

public sealed class MechanicOrb
{
    public required string Id { get; init; }
    public required string Rule { get; init; }
    public required ActionDef Def { get; init; }
    public List<(Vector3d At, int Slot, bool Alive)> Orbs { get; } = [];
    public HashSet<int> Touched { get; } = [];
    public int Hits { get; set; }
    public int Required { get; init; }
    public double Radius { get; init; }
    public long EndsMs { get; init; }
    public bool Done { get; set; }
}

public sealed class MechanicPart
{
    public required PartDef Def { get; init; }
    public required string Rule { get; init; }
    public double Hp { get; set; }
    public bool Broken { get; set; }
}

public sealed record MechanicStaggerWatch(string Id, string Rule, ActionDef Def, long EndsMs);

/// <summary>
/// The engine's state for one boss fight (docs/BOSS-MECHANICS-ENGINE.md): phase, rule fires and timers, marks, counters, player
/// debuff stacks, active zones / covers / shields / orbs / parts, delayed actions. The slam pipeline's state (multipliers, the
/// telegraph being wound up, adds) stays on the <see cref="BossFight"/> it belongs to.
/// </summary>
public sealed class EncounterRun(ResolvedEncounter encounter, int bossKey, long startedMs)
{
    public ResolvedEncounter Encounter { get; } = encounter;
    public int BossKey { get; } = bossKey;
    public long StartedMs { get; } = startedMs;
    public string Id => Encounter.Id;
    public string Difficulty => Encounter.Difficulty;

    public bool Started { get; set; }
    public string? Phase { get; set; }
    public long PhaseStartedMs { get; set; }
    public int PhaseGeneration { get; set; }
    public Vector3d ArenaCentre { get; set; }

    public Dictionary<string, int> Fires { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> NextTimerMs { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> TimerGeneration { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> TimerCount { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> HpSteps { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> GroupDeadAt { get; } = new(StringComparer.Ordinal);
    public HashSet<int> DeadSeen { get; } = [];
    public Dictionary<string, bool> AreaWasMet { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> AreaMetSinceMs { get; } = new(StringComparer.Ordinal);
    public bool WasStaggered { get; set; }

    public Dictionary<string, int> Marks { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> Counters { get; } = new(StringComparer.Ordinal);
    /// <summary>Player debuff stacks by debuff id: player key to (stacks, expiry ms).</summary>
    public Dictionary<string, Dictionary<int, (int Stacks, long UntilMs)>> Debuffs { get; } = new(StringComparer.Ordinal);

    public List<MechanicZone> Zones { get; } = [];
    public List<MechanicCover> Covers { get; } = [];
    public Dictionary<string, MechanicShield> Shields { get; } = new(StringComparer.Ordinal);
    public List<MechanicOrb> Orbs { get; } = [];
    public List<MechanicPart> Parts { get; } = [];
    public List<MechanicStaggerWatch> StaggerWatches { get; } = [];
    public int LastHp { get; set; } = -1;

    /// <summary>Delayed work (telegraph resolutions, delays, repeats), run when due.</summary>
    public List<(long DueMs, long Seq, Func<long, List<byte[]>> Run)> Pending { get; } = [];
    public long PendingSeq { get; set; }
    /// <summary>Events raised this tick ("shield_broken:id", "phase_enter:p2", "&lt;action id&gt;.success", ...), matched to rules.</summary>
    public Queue<(string Kind, string Key, Vector3d? At, int? Player, double Value, double Old)> Events { get; } = new();
    /// <summary>Once-only casts waiting while another once-only cast is wound up.</summary>
    public Queue<BossTelegraph> CastQueue { get; } = new();

    /// <summary>The soft enrage's multipliers, applied on top of later phase multipliers (legacy frenzy semantics).</summary>
    public double EnrageDamage { get; set; } = 1.0;
    public double EnrageHaste { get; set; } = 1.0;
    public bool Enraged { get; set; }
    public int EnvSlots { get; set; }

    public List<MechanicLogEntry> Log { get; } = [];
    public const int LogCap = 256;

    public int FireCount(string ruleId) => Fires.GetValueOrDefault(ruleId);
    public int? Mark(string name) => Marks.TryGetValue(name, out var key) ? key : null;
    public int Stacks(string debuff, int player) => Debuffs.TryGetValue(debuff, out var map) && map.TryGetValue(player, out var s) ? s.Stacks : 0;
    public bool ZoneActive(string id) => Zones.Any(z => z.Id == id);

    internal double N(Num? n, double fallback = 0) => Encounter.N(n, fallback);
    internal long Ms(Num? n, double fallback = 0) => Encounter.Ms(n, fallback);

    internal void Record(long now, string rule, string action, int targets, string result)
    {
        Log.Add(new MechanicLogEntry(now, rule, action, targets, result));
        if (Log.Count > LogCap) Log.RemoveRange(0, Log.Count - LogCap);
    }
}

/// <summary>The moving wall grid (Kromede's fire walls, generalised from KromedeFireWalls with its values in the encounter file).</summary>
public static class GridWalls
{
    public sealed record Rect(double MinX, double MaxX, double MinY, double MaxY)
    {
        public bool Contains(Vector3d at) => at.X >= MinX && at.X <= MaxX && at.Y >= MinY && at.Y <= MaxY;
    }

    /// <summary>Walls between the cells of a rows x cols grid centred on <paramref name="centre"/>, leaving the pattern's gap columns open.</summary>
    public static List<Rect> Build(GridDef grid, IReadOnlyDictionary<string, double> vars, Vector3d centre, int patternKey)
    {
        var walls = new List<Rect>();
        var patterns = grid.GapPatterns;
        var gaps = (patterns.Length == 0 ? [grid.Cols / 2] : patterns[Math.Abs(patternKey) % patterns.Length]).ToHashSet();
        var extent = grid.ArenaHalfExtent.Resolve(vars);
        var half = grid.WallHalfThickness.Resolve(vars);
        var pitch = grid.CellSize.Resolve(vars) * 2;
        var originX = centre.X - extent;
        var originY = centre.Y - extent;
        for (var col = 0; col < grid.Cols - 1; col++)
        {
            if (gaps.Contains(col) || gaps.Contains(col + 1)) continue;
            var x0 = originX + (col + 1) * pitch - half;
            for (var row = 0; row < grid.Rows; row++)
            {
                var y0 = originY + row * pitch;
                walls.Add(new Rect(x0, x0 + half * 2, y0, y0 + pitch));
            }
        }
        for (var col = 0; col < grid.Cols; col++)
        {
            if (gaps.Contains(col)) continue;
            var x0 = originX + col * pitch;
            for (var row = 0; row < grid.Rows - 1; row++)
            {
                var y0 = originY + (row + 1) * pitch - half;
                walls.Add(new Rect(x0, x0 + pitch, y0, y0 + half * 2));
            }
        }
        return walls;
    }
}
