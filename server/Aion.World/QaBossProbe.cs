namespace Aion.World;

/// <summary>
/// QA boss / instance stalls (docs/QA_GAMEPLAY_COVERAGE.md): spawn, phase progress, killable boss HP.
/// </summary>
public static class QaBossProbe
{
    public const string BossNotSpawned = "boss_not_spawned";
    public const string BossPhaseStall = "boss_phase_stall";
    public const string BossUnkillable = "boss_unkillable";

    const long BossEngageStallMs = 180_000;
    const long BossHpStallMs = 240_000;
    const int MinBossHpMax = 50_000;

    public sealed class State
    {
        public long LastTickMs;
        public long ProgressAt;
        public string Token = "";
    }

    public static QaStuckEvent? Tick(
        GameSession session,
        State state,
        QaBossProbeOptions options,
        long now,
        Func<string, string, QaStuckEvent> stuck)
    {
        if (!options.Enabled || !session.QaInInstance) return null;
        if (now - state.LastTickMs < options.IntervalMs) return null;
        state.LastTickMs = now;

        var run = session.MapEventRuns.FirstOrDefault(r => r.Status < MapEventRun.Complete && !r.Finished);
        if (run is null) return null;

        var bossObjective = run.Open.FirstOrDefault(x =>
            x.Objective.Type is "KillSpawner" or "HpRemaining" or "KillNpc" && run.Goals[x.Index] < x.Objective.Count);
        if (bossObjective is not { Objective: { } objective }) return null;

        var spawner = objective.Type == "KillSpawner" ? objective.Value(0) : null;
        var boss = session.Targets.Values.FirstOrDefault(t =>
            !t.Dead && t.SubType == "HeroMonster" && t.HpMax >= MinBossHpMax
            && (spawner is null || MapEventRun.NameMatches(spawner, t.Name)));

        if (boss is null)
        {
            if (now - run.StartedMs > options.SpawnTimeoutMs)
                return stuck(BossNotSpawned, $"map_event:{run.Name}:no_hero_boss");
            return null;
        }

        var fight = session.SessionAi.FightOf(boss);
        var token = $"{boss.Key}:{boss.Hp}:{fight?.Reached ?? -1}:{fight?.Slams ?? 0}";
        if (token != state.Token)
        {
            state.Token = token;
            state.ProgressAt = now;
            return null;
        }

        if (fight is not null && now - state.ProgressAt > BossEngageStallMs && fight.Reached == 0 && boss.Hp == boss.HpMax)
            return stuck(BossPhaseStall, $"boss={boss.NpcId}:phases=0:hp_full");

        if (boss.Hp > 1 && boss.HpMax > MinBossHpMax && now - state.ProgressAt > BossHpStallMs)
            return stuck(BossUnkillable, $"boss={boss.NpcId}:hp={boss.Hp}/{boss.HpMax}");

        return null;
    }
}

public sealed record QaBossProbeOptions
{
    public bool Enabled { get; init; } = true;
    public int IntervalMs { get; init; } = 15_000;
    public int SpawnTimeoutMs { get; init; } = 240_000;
}