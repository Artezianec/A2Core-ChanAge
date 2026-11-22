namespace Aion.World;

public sealed record QaAutoRunOptions
{
  public bool Enabled { get; init; } = true;
  public int Count { get; init; } = 5;
    public string[] Classes { get; init; } = ["Gladiator", "Templar", "Ranger", "Cleric", "Sorcerer"];
    public string LevelRange { get; init; } = "1-45";
    public int RestartDelayMs { get; init; } = 5000;
}

public sealed record QaCpuGuardOptions
{
    public bool Enabled { get; init; } = true;
    /// <summary>When world turn busy ratio exceeds this, QA ticks slow down.</summary>
    public double MaxWorldBusy { get; init; } = 0.85;
    public int SlowTickMs { get; init; } = 200;
}

public sealed record QaRetentionOptions
{
    public int SamplesPerBucket { get; init; } = 10;
    /// <summary>Minimum minutes between new stuck samples for the same bucket (repeats only bump hit_count).</summary>
    public int SampleDetailIntervalMinutes { get; init; } = 15;
    public int RunRetentionDays { get; init; } = 90;
    public int SampleRetentionDays { get; init; } = 30;
    /// <summary>How often to purge old QA rows (hours); 0 uses daily host cleanup only.</summary>
    public int CleanupIntervalHours { get; init; } = 24;
}

/// <summary>QA quest bots (docs/QA_BOTS.md). On by default on production hosts; set Enabled false to disable.</summary>
public sealed record QaBotOptions
{
    public bool Enabled { get; init; } = true;
    /// <summary>When true (default), QA bots appear as normal characters; when false, GM invisibility is applied.</summary>
    public bool Visible { get; init; } = true;
    public int StepTimeoutMs { get; init; } = 120_000;
    public int PathTimeoutMs { get; init; } = 60_000;
    /// <summary>Same qualifying action label with no goal/step/position reset this long triggers a stuck report.</summary>
    public int ActionLoopTimeoutMs { get; init; } = 120_000;
    /// <summary>Clear a stuck 0x3621 pending load and report after this long.</summary>
    public int MapLoadTimeoutMs { get; init; } = 15_000;
    /// <summary>Step / action-loop stall threshold while a map-event script is active (escort_wait, seek_mob, …).</summary>
    public int MapEventStallTimeoutMs { get; init; } = 240_000;
    public int DeathLoopThreshold { get; init; } = 3;
    public int DeathLoopWindowMs { get; init; } = 600_000;
    public int ActionRingSize { get; init; } = 20;
    /// <summary>Target pool size for auto-run (clamped by <see cref="MaxBots"/> and <see cref="HardMaxBots"/>).</summary>
    public int MaxBots { get; init; } = 5;
    public int HardMaxBots { get; init; } = 10;
    /// <summary>Do not attack quest mobs when a real player is within this radius (cm).</summary>
    public double PlayerProximityCm { get; init; } = 2500;
    public QaAutoRunOptions AutoRun { get; init; } = new();
    public QaCpuGuardOptions CpuGuard { get; init; } = new();
    public QaRetentionOptions Retention { get; init; } = new();

    public QaBotOptions Validated()
    {
        if (StepTimeoutMs < 5000) throw new InvalidOperationException("Aion:QaBots:StepTimeoutMs must be at least 5000.");
        if (MaxBots < 1 || HardMaxBots < MaxBots) throw new InvalidOperationException("Aion:QaBots:HardMaxBots must be >= MaxBots >= 1.");
        if (AutoRun.Count > HardMaxBots) throw new InvalidOperationException("Aion:QaBots:AutoRun:Count cannot exceed HardMaxBots.");
        return this;
    }

    public (int From, int To)? ParseLevelRange()
    {
        var parts = AutoRun.LevelRange.Split('-', StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out var from) && int.TryParse(parts[1], out var to) && from >= 1 && to >= from
            ? (from, to) : null;
    }

    public int EffectivePoolCount() => Math.Clamp(AutoRun.Count, 1, Math.Min(MaxBots, HardMaxBots));

    public (string Class, int Race) ProfileForSlot(int slot)
    {
        var classes = AutoRun.Classes;
        var cls = classes.Length > 0 ? classes[slot % classes.Length] : "Gladiator";
        return (cls, slot % 2 == 0 ? 1 : 2);
    }
}