using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Stuck reason codes stored in game.qa_stuck_reports.</summary>
public static class QaStuckReason
{
    public const string StepTimeout = "step_timeout";
    public const string PathStuck = "path_stuck";
    public const string NpcMissing = "npc_missing";
    public const string MobMissing = "mob_missing";
    public const string AcceptFailed = "accept_failed";
    public const string RewardFailed = "reward_failed";
    public const string UnsupportedObjective = "unsupported_objective";
    public const string NoQuestAvailable = "no_quest_available";
    public const string EnvMissing = "env_missing";
    public const string LoginFailed = "login_failed";
    public const string CreateFailed = "create_failed";
    public const string SelectFailed = "select_failed";
    public const string EnterFailed = "enter_failed";
    public const string LoadFailed = "load_failed";
    public const string Boot = "boot";
    public const string NoProgress = "no_progress";
    public const string DeathLoop = "death_loop";
    public const string ExploitBombRange = "exploit_bomb_range";
    public const string CarryStuck = "carry_stuck";
}

public sealed record QaThinkResult(IReadOnlyList<byte[]> Requests, QaStuckEvent? Stuck)
{
    public IReadOnlyList<byte[]> Outbound { get; init; } = [];
    public static QaThinkResult Empty { get; } = new([], null);
}

/// <summary>Quest-playing brain for a QA bot: pick quests, advance steps, detect stuck.</summary>
public sealed class QaQuestBrain
{
    readonly GameData data;
    readonly GameSession session;
    readonly QuestStepDriver driver;
    readonly QuestDriverState driverState = new();
    readonly int levelTo;
    readonly int race;
    readonly QaBotOptions options;
    readonly RingBuffer<string> actions;

    string? focusQuest;
    string lastProgressToken = "";
    long lastProgressAt;
    long lastMoveAt;
    Vector3d? lastPosition;
    long carrySince;
    long lastCarryMoveAt;
    Vector3d? lastCarryPosition;
    const int CarryStuckNoMoveMs = 45_000;
    int questsRewarded;
    bool rewardPending;
    readonly HashSet<string> blockedQuests = new(StringComparer.Ordinal);
    long softStuckUntil;
    long stallSuppressUntil;
    long mapLoadPendingSince;
    int softStuckFailures;
    string? actionStreakLabel;
    string actionStreakGoals = "";
    int actionStreakStep;
    long actionStreakStartedAt;
    Vector3d? actionStreakPosition;
    int deathsInWindow;
    long deathsWindowStart;
    string? deathFocusKey;

    public QaQuestBrain(GameData data, GameSession session, int race, int levelTo, QaBotOptions options)
    {
        this.data = data;
        this.session = session;
        this.race = race;
        this.levelTo = levelTo;
        this.options = options;
        driver = new QuestStepDriver(data, options);
        actions = new RingBuffer<string>(options.ActionRingSize);
        lastProgressAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lastMoveAt = lastProgressAt;
    }

    public int QuestsRewarded => questsRewarded;
    public string? FocusQuest => focusQuest;
    public QuestDriverState DriverState => driverState;
    public IReadOnlyList<string> RecentActions => actions.ToList();

    public bool SoftStuckPaused(long now) => softStuckUntil > 0 && now < softStuckUntil;

    public void NoteDeath(long now)
    {
        var code = focusQuest;
        var step = code is null || session.Quests?.Get(code) is not { } rec ? 0 : rec.Step;
        var key = $"{code}|{step}";
        if (deathFocusKey != key)
        {
            deathFocusKey = key;
            deathsInWindow = 0;
            deathsWindowStart = now;
        }
        if (now - deathsWindowStart > options.DeathLoopWindowMs)
        {
            deathsWindowStart = now;
            deathsInWindow = 0;
        }
        deathsInWindow++;
    }

    internal void PinQuestFocus(string code)
    {
        focusQuest = code;
        foreach (var hero in HeroChains.Embedded.ForRace(race))
            if (!hero.Equals(code, StringComparison.Ordinal)) blockedQuests.Add(hero);
    }

    public QaThinkResult Think(long now)
    {
        if (!session.InWorld || session.Dead) return new([], null);
        NoteProgress(now);

        if (now >= softStuckUntil && softStuckUntil > 0)
        {
            blockedQuests.Clear();
            softStuckUntil = 0;
        }
        if (now < softStuckUntil)
            return new([], null);

        if (BombRangeExploitStall(now) is { } bombExploit) return new([], bombExploit);
        if (QaStallEvent(now) is { } stall) return new([], stall);

        if (now < driverState.CombatBusyUntil)
        {
            lastMoveAt = now;
            return new([], null);
        }

        if (session.Level >= levelTo && focusQuest is null)
            return new([], null);

        focusQuest ??= PickNextQuest();
        RedirectClearQuestPrerequisite();
        RedirectMapEventGateQuest();
        if (focusQuest is null)
            return new([], Stuck(QaStuckReason.NoQuestAvailable, null, null, 0, "no quest for level"));

        var code = focusQuest;
        var def = session.Quests?.Book.Find(code);
        var record = session.Quests?.Get(code);

        if (record is null)
        {
            actions.Add("accept");
            var packets = TryAcceptPackets(code);
            if (packets.Count == 0)
                return new([], Stuck(QaStuckReason.AcceptFailed, code, def, 0, session.QuestAcceptBlockReason(code) ?? "cannot accept"));
            return new(packets, null);
        }

        if (record.Status == QuestLog.Complete && !rewardPending)
        {
            actions.Add("reward");
            rewardPending = true;
            return new([new PacketWriter().U16(0x8d0e).I32((int)record.Uid).I32(-1).ToArray()], null);
        }

        if (record.Status == QuestLog.Rewarded)
        {
            questsRewarded++;
            focusQuest = null;
            rewardPending = false;
            ClearMapEventState();
            driverState.MapEventsTried.Clear();
            driverState.ObjectiveIndex = 0;
            return QaThinkResult.Empty;
        }

        if (record.Status == QuestLog.Complete && rewardPending)
        {
            if (session.Quests!.Get(code)?.Status != QuestLog.Rewarded)
                return new([], Stuck(QaStuckReason.RewardFailed, code, def, record.Step, "reward failed"));
            rewardPending = false;
            questsRewarded++;
            focusQuest = null;
            return new([], null);
        }

        if (record.Status != QuestLog.Incomplete)
        {
            focusQuest = null;
            return new([], null);
        }

        var slice = driver.NextSlice(session, code, now, driverState);
        actions.Add(slice.ActionLabel);
        TrackActionStreak(slice.ActionLabel, now);

        switch (slice.Kind)
        {
            case QuestDriverKind.Stuck:
                var reason = slice.StuckReason switch
                {
                    "npc_not_spawned" or "entry_npc_missing" => QaStuckReason.NpcMissing,
                    "mob_not_spawned" or "boss_not_spawned" => QaStuckReason.MobMissing,
                    "env_not_found" => QaStuckReason.EnvMissing,
                    "dungeon_entry_refused" => QaStuckReason.StepTimeout,
                    QaStuckReason.ExploitBombRange => QaStuckReason.ExploitBombRange,
                    QaStuckReason.CarryStuck => QaStuckReason.CarryStuck,
                    _ => QaStuckReason.PathStuck,
                };
                return new([], Stuck(reason, code, def, record.Step, slice.StuckReason));
            case QuestDriverKind.Unsupported:
                return new([], Stuck(QaStuckReason.UnsupportedObjective, code, def, record.Step, slice.ActionLabel));
            case QuestDriverKind.Teleport when slice.TeleportMap is { } map && slice.TeleportAt is { } at:
                if (session.MapLoadPending)
                    return QaThinkResult.Empty;
                lastMoveAt = now;
                if (driverState.ActiveMapEvent is not null && map != session.MapId)
                {
                    var dungeonId = MapEventTables.Embedded.DungeonIdOfMap(map);
                    if (dungeonId > 0 && session.InstanceKey == 0)
                    {
                        var enter = session.EnterDungeonById(dungeonId, now);
                        if (enter.Count > 0)
                            return new([], null) { Outbound = enter };
                    }
                }
                return new([], null) { Outbound = session.Teleport(map, at, now) };
            case QuestDriverKind.Act when slice.Requests.Count > 0:
                lastMoveAt = now;
                var (outbound, client) = QaBotFeed.PartitionPackets(slice.Requests);
                return new(client, null) { Outbound = outbound };
            case QuestDriverKind.Wait:
            case QuestDriverKind.Idle:
                return new([], null);
        }

        return new([], null);
    }

    const long BombProbeExploitWindowMs = 12_000;

    QaStuckEvent? BombRangeExploitStall(long now)
    {
        if (driverState.BombProbeGoalsBefore is not { } before || driverState.BombProbeMapEventRunId is not { } runId) return null;
        if (driverState.BombProbeAttemptAt <= 0) return null;
        if (now - driverState.BombProbeAttemptAt > BombProbeExploitWindowMs)
        {
            driverState.BombProbeGoalsBefore = null;
            driverState.BombProbeMapEventRunId = null;
            driverState.BombProbeAttemptAt = 0;
            return null;
        }
        var run = session.MapEventRuns.FirstOrDefault(r => r.Id == runId);
        if (run is null)
        {
            driverState.BombProbeGoalsBefore = null;
            driverState.BombProbeMapEventRunId = null;
            driverState.BombProbeAttemptAt = 0;
            return null;
        }
        for (var i = 0; i < before.Length && i < run.Goals.Length; i++)
        {
            if (run.Goals[i] > before[i])
                return StallEvent(QaStuckReason.ExploitBombRange, "map event goal advanced after far bomb plant");
        }
        return null;
    }

    /// <summary>After a client cast packet is handled, drop targets that keep refusing.</summary>
    public void NoteClientCastOutcome(GameSession actor)
    {
        if (actor.ConsumeQaCastRefusal() is { } refusal)
        {
            if (refusal.Reason is "height" or "out_of_range")
            {
                driverState.CastHeightRepositionKey = refusal.TargetKey;
                driverState.CastRefusalStreak.Remove(refusal.TargetKey);
                driverState.CastRefusalBlacklisted.Remove(refusal.TargetKey);
                return;
            }
            var streak = driverState.CastRefusalStreak.GetValueOrDefault(refusal.TargetKey) + 1;
            driverState.CastRefusalStreak[refusal.TargetKey] = streak;
            if (streak >= QuestDriverState.CastRefusalBlacklistAfter)
            {
                driverState.CastRefusalBlacklisted.Add(refusal.TargetKey);
                if (driverState.CombatTargetKey == refusal.TargetKey)
                    driverState.CombatTargetKey = null;
            }
            return;
        }
        if (actor.ConsumeQaCastAcceptedTarget() is { } acceptedKey)
            driverState.CastRefusalStreak.Remove(acceptedKey);
    }

    QaStuckEvent? QaStallEvent(long now)
    {
        if (session.CarriedGroup is not null)
        {
            if (carrySince == 0)
            {
                carrySince = now;
                lastCarryMoveAt = now;
            }
            if (now < driverState.CombatBusyUntil) lastCarryMoveAt = now;
            if (session.WorldPosition is { } carryAt)
            {
                if (lastCarryPosition is not { } prevCarry || Flat(carryAt, prevCarry) > 40)
                {
                    lastCarryPosition = carryAt;
                    lastCarryMoveAt = now;
                }
            }
            if (now - carrySince > CarryStuckNoMoveMs && now - lastCarryMoveAt > CarryStuckNoMoveMs)
                return StallEvent(QaStuckReason.CarryStuck, $"carrying group {session.CarriedGroup} without move/cast for {CarryStuckNoMoveMs}ms");
        }
        else
        {
            carrySince = 0;
            lastCarryMoveAt = 0;
            lastCarryPosition = null;
        }
        if (now < stallSuppressUntil) return null;
        if (now < driverState.CombatBusyUntil)
        {
            lastMoveAt = now;
            return null;
        }
        if (DeathLoopStall(now) is { } deathStall) return deathStall;
        if (actionStreakLabel is { } loopLabel && ActionLoopStall(now, loopLabel))
        {
            var ms = now - actionStreakStartedAt;
            return StallEvent(QaStuckReason.StepTimeout, $"action loop {loopLabel} for {ms}ms");
        }
        if (session.MapLoadPending)
        {
            mapLoadPendingSince = mapLoadPendingSince == 0 ? now : mapLoadPendingSince;
            var loadLimit = OnClearMapEventObjective() ? options.MapLoadTimeoutMs * 8 : options.MapLoadTimeoutMs;
            if (now - mapLoadPendingSince > loadLimit)
            {
                session.AbandonPendingMapLoadForQa(now);
                mapLoadPendingSince = 0;
                return StallEvent(QaStuckReason.StepTimeout, $"map load timeout after {loadLimit}ms");
            }
            lastMoveAt = now;
            return null;
        }
        else mapLoadPendingSince = 0;
        var mapEventBusy = HasActiveMapEventRun();
        var progressLimit = mapEventBusy ? options.MapEventStallTimeoutMs : options.StepTimeoutMs;
        if (now - lastProgressAt > progressLimit)
            return StallEvent(QaStuckReason.NoProgress, "no quest/xp/goals progress");
        var pathLimit = mapEventBusy ? options.MapEventStallTimeoutMs : options.PathTimeoutMs;
        if (focusQuest is not null && now - lastMoveAt > pathLimit && session.WorldPosition is { } me && lastPosition is { } prev
            && Flat(me, prev) < 50)
            return StallEvent(QaStuckReason.StepTimeout, "no movement on quest step");
        return null;
    }

    QaStuckEvent? DeathLoopStall(long now)
    {
        if (deathsInWindow < options.DeathLoopThreshold) return null;
        if (now - deathsWindowStart > options.DeathLoopWindowMs) return null;
        return StallEvent(QaStuckReason.DeathLoop, $"deaths={deathsInWindow} on {deathFocusKey}");
    }

    bool HasActiveMapEventRun() => session.MapEventRuns.Any(r => !r.Finished);

    bool OnClearMapEventObjective()
    {
        if (focusQuest is null || session.Quests?.Get(focusQuest) is not { } rec) return false;
        return session.Quests.CurrentStep(rec)?.Objectives.Any(o => o.Type == "ClearMapEvent") == true;
    }

    void ClearMapEventState()
    {
        driverState.ActiveMapEvent = null;
        driverState.MapEventPhase = 0;
        driverState.MapEventSubPhase = 0;
        driverState.MapEventPhaseUntil = 0;
        driverState.ActiveMapEventRunId = null;
        driverState.PendingEnvKey = null;
        driverState.PendingEnvUntil = 0;
        driverState.FailedEnvKeys.Clear();
        driverState.KickSpawnersAttempted.Clear();
        driverState.BombRangeProbeRunsAttempted.Clear();
        driverState.BombProbeGoalsBefore = null;
        driverState.BombProbeMapEventRunId = null;
        driverState.BombProbeAttemptAt = 0;
        driverState.HostileClearCount = 0;
        driverState.HostileClearWindowSince = 0;
    }

    QaStuckEvent StallEvent(string reason, string detail)
    {
        var code = focusQuest;
        var def = code is null ? null : session.Quests?.Book.Find(code);
        var step = code is null || session.Quests?.Get(code) is not { } rec ? 0 : rec.Step;
        return Stuck(reason, code, def, step, detail);
    }

    void TrackActionStreak(string label, long now)
    {
        label = ActionStreakKey(label);
        var goals = focusQuest is null || session.Quests?.Get(focusQuest) is not { } rec
            ? ""
            : string.Join(',', rec.Goals);
        var step = focusQuest is null || session.Quests?.Get(focusQuest) is not { } qrec ? 0 : qrec.Step;
        if (goals != actionStreakGoals || step != actionStreakStep)
            BeginActionStreak(label, now, goals, step);
        else if (session.WorldPosition is { } p && actionStreakPosition is { } anchor && Flat(p, anchor) > 100)
            BeginActionStreak(label, now, goals, step);
        else if (label != actionStreakLabel)
            BeginActionStreak(label, now, goals, step);
        actionStreakGoals = goals;
        actionStreakStep = step;
    }

    void BeginActionStreak(string label, long now, string goals, int step)
    {
        actionStreakLabel = label;
        actionStreakStartedAt = now;
        actionStreakGoals = goals;
        actionStreakStep = step;
        actionStreakPosition = session.WorldPosition;
    }

    bool ActionLoopStall(long now, string label)
    {
        var limit = options.ActionLoopTimeoutMs;
        if (label.StartsWith("map_event:", StringComparison.Ordinal))
        {
            limit = MapEventIdleActionLabel(label) ? options.MapEventStallTimeoutMs : options.ActionLoopTimeoutMs;
            if (!MapEventIdleActionLabel(label))
            {
                if (now - actionStreakStartedAt < limit) return false;
                if (IsMovementLoopLabel(label))
                {
                    if (session.WorldPosition is { } p && actionStreakPosition is { } anchor)
                        return Flat(p, anchor) < 100;
                    return true;
                }
                return false;
            }
        }
        else if (OnClearMapEventObjective())
            limit = options.MapEventStallTimeoutMs;
        if (now - actionStreakStartedAt < limit) return false;
        if (IsNoOpLoopLabel(label)) return true;
        if (IsMovementLoopLabel(label))
        {
            if (session.WorldPosition is { } p && actionStreakPosition is { } anchor)
                return Flat(p, anchor) < 100;
            return true;
        }
        return false;
    }

    static bool MapEventIdleActionLabel(string label) =>
        label.Contains("escort_wait", StringComparison.Ordinal)
        || label.Contains("script_idle", StringComparison.Ordinal)
        || label.Contains("seek_mob", StringComparison.Ordinal)
        || label.Contains("seek_trigger", StringComparison.Ordinal)
        || label.Contains("seek_npc", StringComparison.Ordinal)
        || label.Contains("seek_env", StringComparison.Ordinal)
        || label.Contains("hp_remaining_wait", StringComparison.Ordinal)
        || label.Contains("portal_channel", StringComparison.Ordinal)
        || label.Contains("cutscene_wait", StringComparison.Ordinal)
        || label.Contains("spawn_kick", StringComparison.Ordinal)
        || label.Contains("hp_remaining_seek", StringComparison.Ordinal);

    static bool IsNoOpLoopLabel(string label) =>
        label.Contains("seek_", StringComparison.Ordinal)
        || label.Contains("script_idle", StringComparison.Ordinal)
        || label.Contains("leave_for_entry", StringComparison.Ordinal)
        || label.StartsWith("teleport:", StringComparison.Ordinal)
        || label.Contains("load_pending", StringComparison.Ordinal)
        || MapEventIdleActionLabel(label);

    static bool IsMovementLoopLabel(string label) =>
        label.StartsWith("walk:", StringComparison.Ordinal)
        || label.Contains(":walk", StringComparison.Ordinal)
        || label.Contains("walkin", StringComparison.OrdinalIgnoreCase);

    static string ActionStreakKey(string label) =>
        label.EndsWith(":interval", StringComparison.Ordinal) ? label[..^":interval".Length] : label;

    void NoteProgress(long now)
    {
        var token = QuestProgressToken();
        if (token != lastProgressToken)
        {
            lastProgressToken = token;
            lastProgressAt = now;
            ClearActionStreak();
            driverState.PendingEnvKey = null;
        }
        if (session.WorldPosition is { } p)
        {
            if (lastPosition is null || Flat(p, lastPosition.Value) > 30) lastMoveAt = now;
            lastPosition = p;
        }
    }

    string QuestProgressToken()
    {
        var questPart = focusQuest is null ? "idle" : QuestStepDriver.ProgressFingerprint(session, focusQuest);
        return $"L{session.Level}|Xp{session.Experience}|{questPart}|{MapEventProgressToken()}";
    }

    string MapEventProgressToken()
    {
        if (session.MapEventRuns.Count == 0) return "ME:none";
        var active = session.MapEventRuns.Where(r => !r.Finished).ToList();
        if (active.Count == 0) return "ME:done";
        return string.Join('|', active.OrderBy(r => r.Id).Select(r =>
            $"{r.Name}#{r.Id}:s{r.StepOrder}:[{string.Join(',', r.Goals)}]:open={r.Open.Count()}"));
    }

    string ProgressToken()
    {
        var pos = session.WorldPosition is { } p ? $"{p.X:F0},{p.Y:F0},{p.Z:F0}" : "?";
        return $"{QuestProgressToken()}|{pos}";
    }

    public void EmitHeartbeat(IQaBotDiagnostics? diagnostics, int slot, GameData gameData)
    {
        if (diagnostics is null || !session.InWorld) return;
        var code = focusQuest;
        var step = 0;
        if (code is not null && session.Quests?.Get(code) is { } rec) step = rec.Step;
        var pos = session.WorldPosition is { } p ? $"{p.X:F0},{p.Y:F0},{p.Z:F0}" : null;
        var last = actions.ToList() is { Count: > 0 } list ? list[^1] : null;
        var need = session.Level < gameData.MaxLevel ? gameData.NeedExp(session.Level) : 0L;
        diagnostics.Heartbeat(slot, code, step, session.Level, session.Experience, need, session.MapId, pos, last, softStuckUntil);
    }

    void RedirectClearQuestPrerequisite()
    {
        if (focusQuest is null || session.Quests?.Get(focusQuest) is not { Status: QuestLog.Incomplete } record) return;
        if (session.Quests.CurrentStep(record) is not { } step) return;
        foreach (var o in step.Objectives)
        {
            if (o.Type != "ClearQuest" || o.Target is not { } gate || session.Quests.Get(gate) is { Status: >= QuestLog.Complete }) continue;
            if (session.Quests.Get(gate) is { Status: QuestLog.Incomplete or QuestLog.Complete })
            {
                focusQuest = gate;
                return;
            }
            if (session.Quests.CanAccept(gate, session.Level))
            {
                focusQuest = gate;
                return;
            }
        }
    }

    /// <summary>Dungeon layer scripts (e.g. Dungeon_0005) need HQ1101021 step 1 active before EnterDungeon succeeds.</summary>
    void RedirectMapEventGateQuest()
    {
        const string gate = "HQ1101021";
        const string dream = "AQ1602000";
        if (session.Quests?.Get(dream) is { Status: QuestLog.Incomplete }
            && session.Quests.Get(gate) is null
            && session.Quests.CanAccept(gate, session.Level))
        {
            focusQuest = gate;
            return;
        }
        if (focusQuest is not (dream or gate)) return;
        var gateRec = session.Quests?.Get(gate);
        if (gateRec is { Status: QuestLog.Incomplete, Step: 1 })
        {
            if (focusQuest == gate && session.Quests!.CurrentStep(gateRec) is { Objectives: [{ Type: "ClearQuest", Target: "AQ1602000" }] })
                focusQuest = "AQ1602000";
            return;
        }
        if (gateRec is null && session.Quests?.CanAccept(gate, session.Level) == true)
        {
            focusQuest = gate;
            return;
        }
        if (gateRec is { Status: QuestLog.Incomplete })
            focusQuest = gate;
    }

    string? PickNextQuest()
    {
        var log = session.Quests!;
        foreach (var code in HeroChains.Embedded.ForRace(race))
        {
            if (log.Book.Find(code) is not { } def || def.Unavailable) continue;
            if (session.Level < def.Level || def.Level > levelTo + 3) continue;
            if (blockedQuests.Contains(code)) continue;
            if (log.Get(code) is { Status: QuestLog.Rewarded }) continue;
            if (log.Get(code) is { Status: QuestLog.Incomplete or QuestLog.Complete }) return code;
            if (log.CanAccept(code, session.Level)) return code;
        }
        foreach (var (code, def) in log.Book.Quests.OrderBy(q => q.Value.Level))
        {
            if (blockedQuests.Contains(code)) continue;
            if (!QuestBook.RaceMatches(def, race) || def.Unavailable || def.Type == "Duty") continue;
            if (session.Level < def.Level || def.Level > levelTo + 2) continue;
            if (log.Get(code) is { Status: >= QuestLog.Rewarded }) continue;
            if (log.Get(code) is { Status: QuestLog.Incomplete or QuestLog.Complete }) return code;
            if (def.Acquire is "Auto" or "Npc" && log.CanAccept(code, session.Level)) return code;
        }
        return null;
    }

    IReadOnlyList<byte[]> TryAcceptPackets(string code)
    {
        if (session.Quests?.CanAccept(code, session.Level) != true) return [];
        var def = session.Quests.Book.Find(code)!;
        return [new PacketWriter().U16(0x8d0a).I32(def.Id).I32(FindOfferNpc(code) ?? 0).ToArray()];
    }

    int? FindOfferNpc(string code) =>
        session.Targets.Values.FirstOrDefault(n => session.Quests!.OfferedBy(n.NpcId, session.Level).Contains(code))?.Key;

    QaStuckEvent Stuck(string reason, string? code, QuestDefinition? def, int step, string? detail)
    {
        var pos = session.WorldPosition;
        return new(reason, code, def?.Title, step, session.Level, session.MapId, pos is { } p ? [p.X, p.Y, p.Z] : null, detail, actions.ToList());
    }

    public void AbandonCurrent(long now)
    {
        if (focusQuest is null || session.Quests?.Get(focusQuest) is not { Status: QuestLog.Incomplete, Uid: var uid }) return;
        _ = new PacketWriter().U16(0x8d0c).I32((int)uid); // queued next tick via Think if needed
        focusQuest = null;
        rewardPending = false;
        ClearMapEventState();
    }

    /// <summary>Stay in-world: skip the quest temporarily and back off before retrying the same step.</summary>
    public void HandleSoftStuck(QaStuckEvent stuck, long now)
    {
        if (now < softStuckUntil) return;
        if (stuck.QuestCode is { } q) blockedQuests.Add(q);
        rewardPending = false;
        var preserveEvent = driverState.ActiveMapEvent;
        var preserveRunId = driverState.ActiveMapEventRunId;
        ClearMapEventState();
        driverState.ActiveMapEvent = preserveEvent;
        driverState.ActiveMapEventRunId = preserveRunId;
        driverState.MapEventsTried.Clear();
        driverState.ObjectiveIndex = 0;
        softStuckFailures++;
        var minutes = Math.Min(30, 1 << Math.Min(softStuckFailures, 5));
        softStuckUntil = now + minutes * 60_000L;
        stallSuppressUntil = now + Math.Min(options.StepTimeoutMs / 2, 60_000L);
        lastProgressAt = now;
        lastMoveAt = now;
        ClearActionStreak();
    }

    void ClearActionStreak()
    {
        actionStreakLabel = null;
        actionStreakGoals = "";
        actionStreakStep = 0;
        actionStreakStartedAt = 0;
        actionStreakPosition = null;
    }

    static double Flat(Vector3d a, Vector3d b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}

public sealed record QaStuckEvent(
    string ReasonCode,
    string? QuestCode,
    string? QuestTitle,
    int QuestStep,
    int Level,
    int? MapId,
    double[]? Position,
    string? Detail,
    IReadOnlyList<string> LastActions);

sealed class RingBuffer<T>(int capacity)
{
    readonly T[] items = new T[capacity];
    int count, head;

    public void Add(T item) { items[head] = item; head = (head + 1) % items.Length; if (count < items.Length) count++; }

    public IReadOnlyList<T> ToList()
    {
        var list = new List<T>(count);
        for (var i = 0; i < count; i++) list.Add(items[(head - count + i + items.Length) % items.Length]);
        return list;
    }
}