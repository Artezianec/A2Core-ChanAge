using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Kromede's Desire fire-wall grid (Fire Temple final, skills 1801185 / 1801191). tools/data/kromede-fire-walls.json.
/// Values marked CANDIDATE in that file and docs/BOSS-MECHANICS.md.
/// </summary>
public static class KromedeFireWalls
{
    public static readonly HashSet<int> KromedeBossNpcIds = [2300371, 2310371, 2320371];

    static readonly Lazy<Config> Configured = new(Load);

    public sealed record WallRect(double MinX, double MaxX, double MinY, double MaxY)
    {
        public bool Contains(Vector3d at) => at.X >= MinX && at.X <= MaxX && at.Y >= MinY && at.Y <= MaxY;
    }

    public sealed class Run
    {
        public required int BossKey { get; init; }
        public required int Skill { get; init; }
        public required Vector3d Centre { get; init; }
        public required long ActiveUntilMs { get; set; }
        public required long NextStepMs { get; set; }
        public required long NextDotMs { get; set; }
        public int PatternIndex { get; set; }
        public int PatternOffset { get; set; }
        public List<WallRect> Walls { get; } = [];
    }

    sealed record Config(
        [property: JsonPropertyName("arena_half_extent")] double ArenaHalf,
        [property: JsonPropertyName("cell_size")] double CellSize,
        [property: JsonPropertyName("wall_half_thickness")] double WallHalf,
        [property: JsonPropertyName("rows")] int Rows,
        [property: JsonPropertyName("cols")] int Cols,
        [property: JsonPropertyName("active_after_land_ms")] int ActiveMs,
        [property: JsonPropertyName("step_interval_ms")] int StepMs,
        [property: JsonPropertyName("dot_interval_ms")] int DotMs,
        [property: JsonPropertyName("dot_damage_min")] int DotMin,
        [property: JsonPropertyName("dot_damage_max")] int DotMax,
        [property: JsonPropertyName("gap_patterns")] int[][] GapPatterns,
        [property: JsonPropertyName("second_phase_pattern_offset")] int SecondOffset,
        [property: JsonPropertyName("skills")] int[] Skills,
        [property: JsonPropertyName("wall_env_obj_ids")] int[]? WallEnvIds = null);

    static Config Load()
    {
        using var stream = typeof(KromedeFireWalls).Assembly.GetManifestResourceStream("kromede-fire-walls.json")
            ?? throw new InvalidOperationException("Missing embedded kromede-fire-walls.json");
        return JsonSerializer.Deserialize<Config>(stream) ?? throw new InvalidOperationException("kromede-fire-walls.json invalid");
    }

    public static bool IsKromedeBoss(int npcId) => KromedeBossNpcIds.Contains(npcId);
    public static bool IsFireWallSkill(int skill) => Configured.Value.Skills.Contains(skill);
    public static int ActiveDurationMs => Configured.Value.ActiveMs;
    public static int DotIntervalMs => Configured.Value.DotMs;

    public static bool IsFireWallTelegraph(BossTelegraph slam) =>
        slam.Mechanic is "fire_wall_grid" or "kromede_fire_walls" || IsFireWallSkill(slam.Skill);

    public static Run Start(int bossKey, int skill, Vector3d centre, long now, int windUpMs, bool secondPhase)
    {
        var cfg = Configured.Value;
        var run = new Run
        {
            BossKey = bossKey,
            Skill = skill,
            Centre = centre,
            ActiveUntilMs = now + windUpMs + cfg.ActiveMs,
            NextStepMs = now + cfg.StepMs,
            NextDotMs = now + cfg.DotMs,
            PatternIndex = 0,
            PatternOffset = secondPhase ? cfg.SecondOffset : 0,
        };
        RebuildWalls(run);
        return run;
    }

    static void RebuildWalls(Run run)
    {
        var cfg = Configured.Value;
        run.Walls.Clear();
        var gapCols = GapColumns(run.PatternIndex + run.PatternOffset, cfg).ToHashSet();
        var extent = cfg.ArenaHalf;
        var cell = cfg.CellSize;
        var half = cfg.WallHalf;
        var pitch = cell * 2;
        var originX = run.Centre.X - extent;
        var originY = run.Centre.Y - extent;
        for (var col = 0; col < cfg.Cols - 1; col++)
        {
            if (gapCols.Contains(col) || gapCols.Contains(col + 1)) continue;
            var x0 = originX + (col + 1) * pitch - half;
            var x1 = x0 + half * 2;
            for (var row = 0; row < cfg.Rows; row++)
            {
                var y0 = originY + row * pitch;
                run.Walls.Add(new WallRect(x0, x1, y0, y0 + pitch));
            }
        }
        for (var col = 0; col < cfg.Cols; col++)
        {
            if (gapCols.Contains(col)) continue;
            var x0 = originX + col * pitch;
            var x1 = x0 + pitch;
            for (var row = 0; row < cfg.Rows - 1; row++)
            {
                var y0 = originY + (row + 1) * pitch - half;
                run.Walls.Add(new WallRect(x0, x1, y0, y0 + half * 2));
            }
        }
    }

    static IReadOnlyList<int> GapColumns(int patternKey, Config cfg)
    {
        var patterns = cfg.GapPatterns;
        if (patterns.Length == 0) return [cfg.Cols / 2];
        return patterns[Math.Abs(patternKey) % patterns.Length];
    }

    public static bool InWall(Run? run, Vector3d at)
    {
        if (run is null) return false;
        return run.Walls.Any(w => w.Contains(at));
    }

    public static bool InGap(Run? run, Vector3d at) => run is not null && !InWall(run, at)
        && Math.Abs(at.X - run.Centre.X) <= Configured.Value.ArenaHalf
        && Math.Abs(at.Y - run.Centre.Y) <= Configured.Value.ArenaHalf;

    public static int DotDamage(Random rng) =>
        rng.Next(Configured.Value.DotMin, Configured.Value.DotMax + 1);

    public static void Advance(Run run, long now)
    {
        var cfg = Configured.Value;
        if (now < run.NextStepMs) return;
        run.PatternIndex++;
        run.NextStepMs = now + cfg.StepMs;
        RebuildWalls(run);
    }

    public static int[] WallEnvObjIds => Configured.Value.WallEnvIds ?? [];

    public static BossMechanicTelegraphEventArgs TelegraphEvent(Run run, int bossNpcId, long now) => new(
        bossNpcId, run.BossKey, "fire_wall_grid", run.Skill, run.Centre, now, run.ActiveUntilMs, WallEnvObjIds);
}