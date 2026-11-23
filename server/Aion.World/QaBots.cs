using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

public sealed class QaRunStatus
{
    public long RunId { get; init; }
    public string ClassName { get; init; } = "";
    public int LevelFrom { get; init; }
    public int LevelTo { get; init; }
    public int BotCount { get; init; }
    public int ActiveBots { get; init; }
    public int QuestsRewarded { get; init; }
    public int StuckCount { get; init; }
    public bool Running { get; init; }
    public string? Note { get; init; }
}

sealed class QaBotEntry
{
    public required GameSession Session { get; init; }
    public required QaQuestBrain Brain { get; init; }
    public required WorldTurn Turn { get; init; }
    public required int Index { get; init; }
    public required int Race { get; init; }
    public required string SlotClass { get; init; }
    public required int LevelFrom { get; init; }
    public required int LevelTo { get; init; }
    public IAsyncDisposable? Lifetime { get; init; }
    public PlayerBot.Phase State { get; set; } = PlayerBot.Phase.Pending;
    public SemaphoreSlim Busy { get; } = new(1, 1);
    public string? LastStuckSignature;
    public long LastStuckAt;
    public int SyncedQuestsRewarded;
    public bool WasDead;
}

/// <summary>QA quest bots (docs/QA_BOTS.md): isolated sessions that play quests and record stuck points.</summary>
public sealed class QaBots(
    GameData data,
    QaBotOptions options,
    SharedWorld world,
    IQaBotStore? store = null,
    int? worldId = null,
    Func<QaBotSpawnContext, QaBotSpawnBinding?>? spawnPersisted = null,
    IQaBotDiagnostics? diagnostics = null,
    TerrainStore? terrain = null,
    NavMeshStore? nav = null)
{
    public const int TickMs = 50;
    const int BootFailureBackoffMs = 60_000;
    readonly List<QaBotEntry> bots = [];
    readonly Dictionary<int, long> restartAfter = [];
    readonly Dictionary<int, int> bootFailures = [];
    readonly Lock gate = new();
    long runId;
    string className = "Gladiator";
    int levelFrom = 1, levelTo = 10;
    bool running;
    bool manualRun;
    bool autoRunPaused;
    bool accountsReady;
    int stuckTotal, questsTotal;
    IReadOnlyList<Guid>? internalAccounts;

    public QaBotOptions Options => options;
    public bool IsRunning => running;

    public int ActiveInWorldCount
    {
        get { lock (gate) return bots.Count(b => b.State != PlayerBot.Phase.Gone && b.Session.InWorld); }
    }

    public QaRunStatus Status()
    {
        lock (gate)
        {
            var live = bots.Count(b => b.State != PlayerBot.Phase.Gone && b.Session.InWorld);
            return new QaRunStatus
            {
                RunId = runId, ClassName = className, LevelFrom = levelFrom, LevelTo = levelTo,
                BotCount = bots.Count, ActiveBots = live, QuestsRewarded = questsTotal, StuckCount = stuckTotal,
                Running = running,
                Note = options.AutoRun.Enabled && !manualRun ? (autoRunPaused ? "auto paused" : "auto") : null,
            };
        }
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        if (store is null || accountsReady) return;
        await store.FinishStaleRunsAsync(worldId, ct);
        await store.EnsureInternalAccountsAsync(options.HardMaxBots, ct);
        internalAccounts = await store.InternalAccountIdsAsync(ct);
        accountsReady = true;
    }

    public async Task<string> CommandAsync(string line, string? staffName, CancellationToken ct)
    {
        if (!options.Enabled) return "QA bots are off (Aion:QaBots:Enabled).";
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var verb = words.Length > 0 ? words[0].ToLowerInvariant() : "help";
        switch (verb)
        {
            case "run" when words.Length >= 3:
                return await StartRunAsync(words[1], words[2], words.Length >= 4 && int.TryParse(words[3], out var c) ? c : 1, staffName, ct);
            case "stop":
                return await StopCommandAsync(ct);
            case "status":
                var s = Status();
                return s.Running
                    ? $"QA run #{s.RunId}: {s.ClassName} L{s.LevelFrom}-{s.LevelTo}, {s.ActiveBots}/{s.BotCount} active, {s.QuestsRewarded} quests, {s.StuckCount} stuck.{(s.Note is { } n ? $" ({n})" : "")}"
                    : options.AutoRun.Enabled && !autoRunPaused
                        ? "Auto pool idle (waiting for world)."
                        : "No QA run active.";
            default:
                return "//qa run <class> <from>-<to> [count] | status | stop";
        }
    }

    public async Task TickAutoRunAsync(long now, CancellationToken ct)
    {
        if (!options.Enabled || !options.AutoRun.Enabled || autoRunPaused || manualRun) return;
        if (store is not null && !accountsReady)
            await InitializeAsync(ct);
        await EnsureAutoRunStartedAsync(ct);
        await MaintainPoolAsync(now, ct);
    }

    async Task EnsureAutoRunStartedAsync(CancellationToken ct)
    {
        if (running) return;
        var parsed = options.ParseLevelRange();
        var from = parsed?.From ?? 1;
        var to = parsed?.To ?? 45;
        var count = options.EffectivePoolCount();
        stuckTotal = questsTotal = 0;
        className = "auto";
        levelFrom = from;
        levelTo = to;
        if (store is not null)
            runId = await store.StartRunAsync("auto", from, to, count, worldId, "auto", ct);
        running = true;
        manualRun = false;
    }

    async Task MaintainPoolAsync(long now, CancellationToken ct)
    {
        var want = options.EffectivePoolCount();
        var toSpawn = new List<(int Slot, string Cls, int Race, int From, int To)>();
        lock (gate)
        {
            if (bots.Count(b => b.State != PlayerBot.Phase.Gone) >= options.HardMaxBots) return;
            var occupied = bots.Where(b => b.State != PlayerBot.Phase.Gone).Select(b => b.Index).ToHashSet();
            for (var slot = 0; slot < want; slot++)
            {
                if (occupied.Contains(slot)) continue;
                if (restartAfter.TryGetValue(slot, out var after) && now < after) continue;
                var (cls, race) = options.ProfileForSlot(slot);
                toSpawn.Add((slot, cls, race, levelFrom, levelTo));
            }
        }
        foreach (var spec in toSpawn)
            await SpawnSlotAsync(spec.Slot, spec.Cls, spec.Race, spec.From, spec.To, ct);
    }

    async Task SpawnSlotAsync(int slot, string cls, int race, int from, int to, CancellationToken ct)
    {
        lock (gate)
        {
            if (bots.Any(b => b.Index == slot && b.State != PlayerBot.Phase.Gone)) return;
            if (bots.Count(b => b.State != PlayerBot.Phase.Gone) >= options.HardMaxBots) return;
        }
        var turn = new WorldTurn(world);
        var sessionOptions = new GameSessionOptions { World = world, CharacterLevel = from, Terrain = terrain, Nav = nav };
        QaBotSpawnBinding? binding = null;
        if (spawnPersisted is not null && internalAccounts is { Count: > 0 } ids && slot < ids.Count)
            binding = spawnPersisted(new QaBotSpawnContext(slot, ids[slot], turn, from));
        if (binding is not null)
            sessionOptions = sessionOptions with
            {
                WorldId = binding.SessionOptions.WorldId,
                CharacterLevel = binding.SessionOptions.CharacterLevel,
                StaffRole = binding.SessionOptions.StaffRole,
                WorldRace = binding.SessionOptions.WorldRace,
                WireServerId = binding.SessionOptions.WireServerId,
                Terrain = binding.SessionOptions.Terrain ?? terrain,
                Nav = binding.SessionOptions.Nav ?? nav,
            };
        var session = new GameSession(data, sessionOptions, new Random(9000 + slot))
        {
            Bot = true,
            QaBot = true,
        };
        if (binding is not null)
            session.Store = binding.Store;
        var brain = new QaQuestBrain(data, session, race, to, options);
        var entry = new QaBotEntry
        {
            Session = session, Brain = brain, Turn = turn, Index = slot, Race = race,
            SlotClass = cls, LevelFrom = from, LevelTo = to, Lifetime = binding?.Lifetime,
        };
        lock (gate) bots.Add(entry);
        await Task.CompletedTask;
    }

    async Task<string> StartRunAsync(string cls, string levelRange, int count, string? staff, CancellationToken ct)
    {
        if (running) return "A QA run is already active. //qa stop first.";
        if (store is not null && !accountsReady)
            await InitializeAsync(ct);
        var parts = levelRange.Split('-');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var from) || !int.TryParse(parts[1], out var to) || from < 1 || to < from)
            return "Level range must be like 1-10.";
        count = Math.Clamp(count, 1, options.MaxBots);
        var creation = CreationFor(cls, 1);
        if (creation == 0) return $"{cls} is not a class.";
        autoRunPaused = true;
        manualRun = true;
        className = data.Profile(creation, 1)!.Class;
        levelFrom = from;
        levelTo = to;
        stuckTotal = questsTotal = 0;
        if (store is not null)
            runId = await store.StartRunAsync(className, from, to, count, worldId, staff, ct);
        running = true;
        lock (gate) restartAfter.Clear();
        for (var i = 0; i < count; i++)
        {
            var race = i % 2 == 0 ? 1 : 2;
            await SpawnSlotAsync(i, className, race, from, to, ct);
        }
        return $"QA run #{runId} started: {count} {className}(s) levels {from}-{to}.";
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (!running && !options.AutoRun.Enabled) return;
        autoRunPaused = true;
        manualRun = false;
        if (running)
        {
            running = false;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var bot in Snapshot())
            {
                var acquired = false;
                try
                {
                    await bot.Busy.WaitAsync(CancellationToken.None);
                    acquired = true;
                    bot.State = PlayerBot.Phase.Leaving;
                    await LeaveAsync(bot, now, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    diagnostics?.BootFailure(bot.Index, "shutdown", "stop_failed", ex.Message, ex);
                }
                finally
                {
                    if (acquired) bot.Busy.Release();
                }
            }
            if (store is not null && runId > 0)
                await store.FinishRunAsync(runId, "stopped", questsTotal, stuckTotal, null, CancellationToken.None);
            lock (gate)
            {
                bots.Clear();
                restartAfter.Clear();
                bootFailures.Clear();
            }
        }
        if (store is not null)
            await store.FinishStaleRunsAsync(worldId, CancellationToken.None);
    }

    async Task<string> StopCommandAsync(CancellationToken ct)
    {
        await StopAsync(ct);
        return $"QA run #{runId} stopped ({questsTotal} quests, {stuckTotal} stuck). Auto pool paused.";
    }

    List<QaBotEntry> Snapshot() { lock (gate) return [.. bots]; }

    public async Task TickAsync(long now, CancellationToken ct = default)
    {
        if (!running) return;
        foreach (var bot in Snapshot())
        {
            await bot.Busy.WaitAsync(ct);
            try
            {
                if (bot.State == PlayerBot.Phase.Gone) continue;
                await bot.Turn.EnterWorldAsync(ct);
                try { await StepAsync(bot, now, ct); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    await ReportBootFailureAsync(bot, "tick", QaStuckReason.EnterFailed, DetailFrom(error), error, ct);
                    bot.State = PlayerBot.Phase.Leaving;
                }
                finally { bot.Turn.Exit(); }
                if (bot.State == PlayerBot.Phase.Leaving) await LeaveAsync(bot, now, ct);
            }
            finally { bot.Busy.Release(); }
        }
        if (running && manualRun && bots.Count > 0 && bots.All(b => b.State == PlayerBot.Phase.Gone))
            await CompleteManualRunAsync(ct);
    }

    async Task CompleteManualRunAsync(CancellationToken ct)
    {
        running = false;
        manualRun = false;
        if (store is not null && runId > 0)
            await store.FinishRunAsync(runId, "completed", questsTotal, stuckTotal, null, CancellationToken.None);
        lock (gate) bots.Clear();
    }

    void ScheduleRestart(int slot, long now)
    {
        if (!options.AutoRun.Enabled || manualRun) return;
        var failures = bootFailures.GetValueOrDefault(slot);
        var delay = failures > 2 ? BootFailureBackoffMs : options.AutoRun.RestartDelayMs;
        lock (gate) restartAfter[slot] = now + delay;
    }

    async Task StepAsync(QaBotEntry bot, long now, CancellationToken ct)
    {
        if (bot.State == PlayerBot.Phase.Pending)
        {
            await EnterAsync(bot, now, ct);
            return;
        }
        if (bot.State != PlayerBot.Phase.InWorld) return;
        await Feed(bot, await bot.Session.TickAsync(now, ct), now, ct);
        if (bot.Session.Dead)
        {
            if (!bot.WasDead)
                bot.Brain.NoteDeath(now);
            bot.WasDead = true;
            return;
        }
        bot.WasDead = false;
        var think = bot.Brain.Think(now);
        if (think.Stuck is { } stuck && !bot.Brain.SoftStuckPaused(now))
        {
            var signature = StuckSignature(stuck);
            var repeated = bot.LastStuckSignature == signature && now - bot.LastStuckAt < 120_000;
            var mapEventStuck = stuck.ReasonCode is QaStuckReason.PathStuck
                && stuck.Detail?.StartsWith("map_event:", StringComparison.Ordinal) == true;
            var stayOnline = stuck.ReasonCode is QaStuckReason.UnsupportedObjective or QaStuckReason.NoProgress or QaStuckReason.StepTimeout
                or QaStuckReason.NpcMissing or QaStuckReason.MobMissing or QaStuckReason.EnvMissing or QaStuckReason.DeathLoop
                or QaStuckReason.BossNotSpawned or QaStuckReason.BossPhaseStall or QaStuckReason.BossUnkillable
                || mapEventStuck || repeated;
            if (stayOnline)
            {
                bot.Brain.HandleSoftStuck(stuck, now);
                bot.LastStuckSignature = signature;
                bot.LastStuckAt = now;
                if (store is not null && runId > 0)
                {
                    if (await store.RecordStuckAsync(new QaStuckReportRecord(0, runId, bot.Index, stuck.QuestCode ?? "?", stuck.QuestTitle,
                            stuck.QuestStep, bot.SlotClass, stuck.Level, stuck.MapId, stuck.Position, stuck.ReasonCode, stuck.Detail,
                            stuck.LastActions, DateTimeOffset.UtcNow), options.Retention.SamplesPerBucket,
                        recordDetail: !repeated, options.Retention.SampleDetailIntervalMinutes, ct))
                    {
                        stuckTotal++;
                        await store.UpdateRunCountersAsync(runId, questsTotal, stuckTotal, ct);
                    }
                }
            }
            else
            {
                bot.Brain.AbandonCurrent(now);
                if (store is not null && runId > 0)
                {
                    if (await store.RecordStuckAsync(new QaStuckReportRecord(0, runId, bot.Index, stuck.QuestCode ?? "?", stuck.QuestTitle,
                            stuck.QuestStep, bot.SlotClass, stuck.Level, stuck.MapId, stuck.Position, stuck.ReasonCode, stuck.Detail,
                            stuck.LastActions, DateTimeOffset.UtcNow), options.Retention.SamplesPerBucket,
                        recordDetail: true, options.Retention.SampleDetailIntervalMinutes, ct))
                    {
                        stuckTotal++;
                        await store.UpdateRunCountersAsync(runId, questsTotal, stuckTotal, ct);
                    }
                }
                bot.LastStuckSignature = signature;
                bot.LastStuckAt = now;
                if (!manualRun) bot.State = PlayerBot.Phase.Leaving;
            }
        }
        if (bot.State == PlayerBot.Phase.InWorld && bot.Session.Level >= bot.LevelTo && bot.Brain.FocusQuest is null)
            bot.State = PlayerBot.Phase.Leaving;
        await QaBotFeed.ApplyOutboundAsync(bot.Session, think.Outbound, now, ct);
        foreach (var request in think.Requests)
        {
            await QaBotFeed.ApplyClientRequestAsync(bot.Session, request, now, ct);
            bot.Brain.NoteClientCastOutcome(bot.Session);
        }
        if (bot.Brain.DriverState.CombatHitLandUntil > now)
        {
            var land = bot.Brain.DriverState.CombatHitLandUntil;
            await Feed(bot, await bot.Session.TickAsync(land, ct), land, ct);
            _ = bot.Session.QuestWorldGatingForTests(land);
            bot.Brain.DriverState.CombatHitLandUntil = 0;
        }
        await SyncRunCountersAsync(bot, ct);
        bot.Brain.EmitHeartbeat(diagnostics, bot.Index, data);
    }

    async Task SyncRunCountersAsync(QaBotEntry bot, CancellationToken ct)
    {
        var rewarded = bot.Brain.QuestsRewarded;
        if (rewarded <= bot.SyncedQuestsRewarded) return;
        lock (gate)
        {
            questsTotal += rewarded - bot.SyncedQuestsRewarded;
            bot.SyncedQuestsRewarded = rewarded;
        }
        if (store is not null && runId > 0)
            await store.UpdateRunCountersAsync(runId, questsTotal, stuckTotal, ct);
    }

    async Task EnterAsync(QaBotEntry bot, long now, CancellationToken ct)
    {
        var session = bot.Session;
        var cls = bot.SlotClass;
        var stableName = $"QA{bot.Index:D2}_{cls}"[..Math.Min(16, $"QA{bot.Index:D2}_{cls}".Length)];
        var creation = CreationFor(cls, bot.Race);
        var login = internalAccounts is { Count: > 0 } ids && bot.Index < ids.Count
            ? $"qa-bot:{ids[bot.Index]:N}"
            : $"qa-bot:{bot.Index}";
        try
        {
            await session.HandleAsync(BotRequests.GameLogin(login), now, ct);
            if (await FailEnterAsync(bot, "login", QaStuckReason.LoginFailed, PersistenceDetail(session), ct))
                return;

            var wireId = 0;
            if (session.Store is not null)
            {
                var native = TurnStore.Unwrap(session.Store);
                var lobby = await native.LobbyAsync(ct);
                var existing = lobby.FirstOrDefault(c => c.Summary.Name.Equals(stableName, StringComparison.Ordinal));
                if (existing is not null)
                    wireId = existing.WireId;
                else
                {
                    await session.HandleAsync(BotRequests.Create(stableName, creation), now, ct);
                    if (await FailEnterAsync(bot, "create", QaStuckReason.CreateFailed, PersistenceDetail(session), ct))
                        return;
                    lobby = await native.LobbyAsync(ct);
                    existing = lobby.FirstOrDefault(c => c.Summary.Name.Equals(stableName, StringComparison.Ordinal));
                    wireId = existing?.WireId ?? 0;
                }
            }
            else
            {
                await session.HandleAsync(BotRequests.Create(stableName, creation), now, ct);
                if (await FailEnterAsync(bot, "create", QaStuckReason.CreateFailed, PersistenceDetail(session), ct))
                    return;
                wireId = 1;
            }

            if (wireId <= 0)
            {
                await ReportBootFailureAsync(bot, "create", QaStuckReason.CreateFailed, "no wire id after create", null, ct);
                bot.State = PlayerBot.Phase.Leaving;
                return;
            }

            var selectKey = session.Store is not null ? (uint)wireId << 16 : 65536u;
            var selected = await session.HandleAsync(BotRequests.Select(selectKey), now, ct);
            await Feed(bot, selected, now, ct);
            if (await FailEnterAsync(bot, "select", QaStuckReason.SelectFailed, PersistenceDetail(session), ct))
                return;
            if (!session.InWorld)
            {
                await ReportBootFailureAsync(bot, "enter", QaStuckReason.EnterFailed, PersistenceDetail(session), null, ct);
                bot.State = PlayerBot.Phase.Leaving;
                return;
            }
            if (!options.Visible) session.SetInvisible(true);
            bot.State = PlayerBot.Phase.InWorld;
            lock (gate) bootFailures.Remove(bot.Index);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await ReportBootFailureAsync(bot, "enter", QaStuckReason.EnterFailed, DetailFrom(error), error, ct);
            bot.State = PlayerBot.Phase.Leaving;
        }
    }

    async Task<bool> FailEnterAsync(QaBotEntry bot, string phase, string reason, string? detail, CancellationToken ct)
    {
        if (bot.Session.PersistenceErrors.Count == 0) return false;
        await ReportBootFailureAsync(bot, phase, reason, detail ?? PersistenceDetail(bot.Session), null, ct);
        bot.State = PlayerBot.Phase.Leaving;
        return true;
    }

    static string PersistenceDetail(GameSession session) =>
        session.PersistenceErrors.Count > 0 ? string.Join("; ", session.PersistenceErrors) : "session not in world";

    static string DetailFrom(Exception error) => error.Message;

    async Task ReportBootFailureAsync(QaBotEntry bot, string phase, string reason, string? detail, Exception? error, CancellationToken ct)
    {
        lock (gate) bootFailures[bot.Index] = bootFailures.GetValueOrDefault(bot.Index) + 1;
        diagnostics?.BootFailure(bot.Index, phase, reason, detail, error);
        if (store is not null && runId > 0)
        {
            if (await store.RecordStuckAsync(new QaStuckReportRecord(0, runId, bot.Index, QaStuckReason.Boot, null, 0,
                    bot.SlotClass, bot.Session.Level, null, null, reason, detail ?? phase, ["boot"], DateTimeOffset.UtcNow),
                options.Retention.SamplesPerBucket, recordDetail: true, options.Retention.SampleDetailIntervalMinutes, ct))
            {
                stuckTotal++;
                await store.UpdateRunCountersAsync(runId, questsTotal, stuckTotal, ct);
            }
        }
    }

    async Task Feed(QaBotEntry bot, List<byte[]> packets, long now, CancellationToken ct) =>
        await QaBotFeed.ApplyOutboundAsync(bot.Session, packets, now, ct);

    async Task LeaveAsync(QaBotEntry bot, long now, CancellationToken ct)
    {
        var slot = bot.Index;
        try
        {
            await bot.Turn.EnterWorldAsync(CancellationToken.None);
            try
            {
                if (!options.Visible) bot.Session.SetInvisible(false);
                bot.Session.ExitSharedWorld();
                bot.Session.Abandon();
            }
            finally { bot.Turn.Exit(); }
        }
        finally
        {
            bot.State = PlayerBot.Phase.Gone;
            if (bot.Lifetime is not null)
            {
                try { await bot.Lifetime.DisposeAsync(); }
                catch (Exception) { }
            }
            lock (gate) bots.Remove(bot);
            ScheduleRestart(slot, now);
        }
    }

    static string StuckSignature(QaStuckEvent stuck) =>
        $"{stuck.QuestCode}|{stuck.QuestStep}|{stuck.ReasonCode}|{stuck.Detail}";

    int CreationFor(string cls, int race)
    {
        foreach (var c in Enumerable.Range(1, 64))
        {
            if (data.Profile(c, 1)?.Class.Equals(cls, StringComparison.OrdinalIgnoreCase) != true) continue;
            var prof = data.Profile(c, 1);
            if (prof is null) continue;
            var profRace = prof.Race == 2 ? 2 : 1;
            if (profRace == race) return c;
        }
        return Enumerable.Range(1, 64).FirstOrDefault(c => data.Profile(c, 1)?.Class.Equals(cls, StringComparison.OrdinalIgnoreCase) == true);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickMs));
        while (await timer.WaitForNextTickAsync(ct))
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await TickAutoRunAsync(now, ct);
            await TickAsync(now, ct);
        }
    }
}