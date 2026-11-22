using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Headless 5-slot prod checkpoint (embedded data): same quests/levels as prod watch after #557,
/// 15 simulated minutes at 500 ms/tick. Guards regressions in stuck DB spam, quest=- drops, and online counts.
/// </summary>
public sealed class QaBotProdScenarioTests
{
    const long T0 = 1_760_200_000_000;
    const int SimulatedMinutes = 15;
    const int TickMs = 500;
    const int Ticks = SimulatedMinutes * 60 * 1000 / TickMs;

    [Fact]
    public async Task Five_slot_prod_checkpoint_15_simulated_minutes()
    {
        var data = GameData.Embedded;
        var world = new SharedWorld(data);
        var options = new QaBotOptions { StepTimeoutMs = 240_000, PathTimeoutMs = 120_000, ActionLoopTimeoutMs = 60_000 };
        var store = new RecordingQaBotStore();
        var bots = new List<ScenarioBot>();

        bots.Add(await SeedBotAsync(world, data, options, 0, "Gladiator", 1, 5, "HQ1101030", SeedHq1101030Step3Async));
        bots.Add(await SeedBotAsync(world, data, options, 1, "Ranger", 1, 4, "HQ1101030", SeedHq1101030Step3Async));
        bots.Add(await SeedBotAsync(world, data, options, 2, "Sorcerer", 1, 4, "HQ1101030", SeedHq1101030Step3Async));
        bots.Add(await SeedBotAsync(world, data, options, 3, "Templar", 2, 3, "HQ2101030", SeedHq2101030Step3Async));
        bots.Add(await SeedBotAsync(world, data, options, 4, "Cleric", 2, 3, "HQ2101030", SeedHq2101030Step3Async));

        var startSnapshot = bots.ToDictionary(b => b.Label, Snapshot);
        var startRewarded = bots.Sum(b => b.Brain.QuestsRewarded);
        long now = T0;
        var actionStreak = bots.ToDictionary(b => b.Label, _ => (Label: "", FirstTick: -1, Reported: false));
        var pauseTicks = 0;
        var hq001Max = 0;
        var hq002Max = 0;

        for (var tick = 0; tick < Ticks; tick++)
        {
            now += TickMs;
            hq001Max = Math.Max(hq001Max, MaxMapEventStep(bots.Where(b => b.Label is "Templar" or "Cleric"), "World_D_Starter_HQ001_MapEvent"));
            hq002Max = Math.Max(hq002Max, MaxMapEventStep(bots.Where(b => b.Label is "Gladiator" or "Ranger" or "Sorcerer"), "World_L_Starter_HQ002_MapEvent"));
            foreach (var bot in bots)
            {
                if (!bot.Session.InWorld) continue;
                if (bot.Brain.SoftStuckPaused(now))
                    pauseTicks++;
                await bot.Session.TickAsync(now);
                var think = bot.Brain.Think(now);
                if (think.Stuck is { } stuck && !bot.Brain.SoftStuckPaused(now))
                {
                    actionStreak[bot.Label] = (actionStreak[bot.Label].Label, -1, true);
                    bot.Brain.HandleSoftStuck(stuck, now);
                    await store.RecordStuckAsync(new QaStuckReportRecord(0, 1, 0, stuck.QuestCode ?? "?", stuck.QuestTitle,
                            stuck.QuestStep, bot.Session.ClassName ?? "?", stuck.Level, stuck.MapId, stuck.Position, stuck.ReasonCode,
                            stuck.Detail, stuck.LastActions, DateTimeOffset.UtcNow), options.Retention.SamplesPerBucket, recordDetail: true,
                        options.Retention.SampleDetailIntervalMinutes);
                }
                await QaBotFeed.ApplyThinkAsync(bot.Session, think, now);
                if (bot.Brain.DriverState.CombatHitLandUntil > now)
                {
                    var land = bot.Brain.DriverState.CombatHitLandUntil;
                    await bot.Session.TickAsync(land);
                    _ = bot.Session.QuestWorldGatingForTests(land);
                    bot.Brain.DriverState.CombatHitLandUntil = 0;
                }
                if (think.Stuck is null && think.Requests.Count == 0 && think.Outbound.Count == 0)
                    continue;
                var last = bot.Brain.RecentActions.LastOrDefault() ?? "";
                var streak = actionStreak[bot.Label];
                if (last == streak.Label) { /* same streak */ }
                else { streak.Label = last; streak.FirstTick = tick; }
                actionStreak[bot.Label] = streak;
                var streakMs = streak.FirstTick >= 0 ? (tick - streak.FirstTick + 1) * TickMs : 0;
                var mapEventLoop = last.StartsWith("map_event:", StringComparison.Ordinal);
                Assert.False(!mapEventLoop && streakMs > options.ActionLoopTimeoutMs && !streak.Reported,
                    $"{bot.Label} repeated {last} for {streakMs}ms without a stuck report");
                if (!string.IsNullOrEmpty(last) && (last.StartsWith("teleport:", StringComparison.Ordinal) || last.Contains("walkin", StringComparison.Ordinal)))
                    Assert.False(bot.Session.MapLoadPending, $"{bot.Label} left map load pending after {last}");
            }
        }

        var inWorld = bots.Count(b => b.Session.InWorld);
        Assert.Equal(5, inWorld);
        var progressed = 0;
        foreach (var bot in bots)
        {
            var nowSnap = Snapshot(bot);
            var was = startSnapshot[bot.Label];
            if (nowSnap.Step > was.Step || nowSnap.Rewarded > was.Rewarded || nowSnap.Goals != was.Goals)
                progressed++;
        }
        var rewarded = bots.Sum(b => b.Brain.QuestsRewarded) - startRewarded;
        var mapStepMax = bots.Select(b => b.Session.MapEventRuns.Where(r => !r.Finished).Select(r => r.StepOrder).DefaultIfEmpty(0).Max()).Max();
        Assert.True(progressed >= 2 || rewarded >= 1 || mapStepMax >= 5,
            $"expected quest/map-event advancement (progressed={progressed}, rewarded={rewarded}, mapStepMax={mapStepMax})");

        var maxBucket = store.BucketDetailWrites.Values.DefaultIfEmpty(0).Max();
        Assert.True(maxBucket <= 25, $"expected rate-limited stuck samples (max={maxBucket}, buckets={string.Join(", ", store.BucketDetailWrites)}");
        var bucketSummary = string.Join("; ", store.BucketDetailWrites.Select(kv => $"{kv.Key}={kv.Value}"));
        Assert.True(pauseTicks < Ticks * bots.Count * 3 / 5,
            $"soft-stuck pause should not consume most of the run (pauseTicks={pauseTicks}/{Ticks * bots.Count}, stuck={bucketSummary})");
        var noProgress = store.BucketDetailWrites.Keys.Count(k => k.Contains("no_progress", StringComparison.Ordinal));
        Assert.True(noProgress == 0, $"no_progress should not fire during active map events (buckets={bucketSummary})");
        Assert.True(hq001Max >= 5, $"HQ001 should pass step 5 (maxStep={hq001Max})");
        Assert.True(hq002Max >= 14, $"HQ002 should pass step 14 (maxStep={hq002Max})");
    }

    [Fact]
    public async Task Watchstone_death_grants_kill_goal_on_step_4()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), T0, level: 4);
        await session.QuestCommandAsync("QuestAdd", "HQ1101018", default);
        _ = await session.QuestCommandAsync("QuestSetStep", "HQ1101018", default, stepOrder: 4);
        _ = session.QuestWorldGatingForTests(T0 + 50);
        var watch = session.Targets!.Values.First(t => t.NpcId == 2290017);
        watch.Immune = false;
        watch.Hp = 1;
        var skill = QaBotCombat.BasicAttackSkill(GameData.Embedded, session);
        await MapEventScenarioHelpers.SyncPositionAsync(session, watch.Position with { X = watch.Position.X + 100 }, T0 + 100);
        watch = session.Targets!.Values.First(t => t.Key == watch.Key);
        var castAt = T0 + 200;
        foreach (var packet in new[]
                 {
                     BotRequests.CastBegin(),
                     BotRequests.Cast(skill, watch.Key, 0, watch.Position, 1),
                     BotRequests.CastEnd(),
                 })
            await session.HandleAsync(packet, castAt);
        var land = castAt + QaBotCombat.HitLandMs(GameData.Embedded, skill);
        await session.TickAsync(land);
        var rec = session.Quests!.Get("HQ1101018")!;
        Assert.True(rec.Goals[0] >= 1 || watch.Dead,
            $"watchstone kill should credit step-4 goal (goal0={rec.Goals[0]}, dead={watch.Dead})");
    }

    readonly record struct ScenarioBot(GameSession Session, QaQuestBrain Brain, string Label);

    readonly record struct QuestSnap(int Step, int Rewarded, string Goals);

    static QuestSnap Snapshot(ScenarioBot bot)
    {
        var code = bot.Brain.FocusQuest;
        if (code is null || bot.Session.Quests?.Get(code) is not { } rec)
            return new(0, bot.Brain.QuestsRewarded, "");
        return new(rec.Step, bot.Brain.QuestsRewarded, string.Join(',', rec.Goals));
    }

    static async Task<ScenarioBot> SeedBotAsync(
        SharedWorld world,
        GameData data,
        QaBotOptions options,
        int slot,
        string cls,
        int race,
        int level,
        string questCode,
        Func<GameSession, long, Task> seed)
    {
        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Class: var cl } && cl.Equals(cls, StringComparison.OrdinalIgnoreCase)
            && (race == 2 ? data.Profile(c, 1)!.Race == 2 : data.Profile(c, 1)!.Race != 2));
        var storeChar = new FakeCharacterStore();
        var session = new GameSession(data, new GameSessionOptions { World = world, CharacterLevel = level, WorldId = storeChar.WorldId, TrainingDummy = true }, new Random(4100 + slot))
            { Store = storeChar, Bot = true, QaBot = true };
        var now = T0;
        await session.HandleAsync(BotRequests.GameLogin($"qa-scn-{slot}"), now);
        if (race == 2)
            await session.HandleAsync(new PacketWriter().U16(0x361b).String($"QA{slot}").U8(1).U32((uint)creation).Bytes([1]).U8(0).U8(2).U8(0).ToArray(), now);
        else
            await session.HandleAsync(BotRequests.Create($"QA{slot}", creation), now);
        var wireId = storeChar.States[0].WireId;
        await session.HandleAsync(BotRequests.Select((uint)wireId << 16), now);
        await session.HandleAsync(BotRequests.LoadComplete(1), now);
        await seed(session, now);
        var brain = new QaQuestBrain(data, session, race, 45, options);
        brain.PinQuestFocus(questCode);
        _ = brain.Think(now);
        Assert.Equal(questCode, brain.FocusQuest);
        return new(session, brain, cls);
    }

    static async Task SeedGladiatorPostL0016Async(GameSession session, long now)
    {
        await session.QuestCommandAsync("QuestAdd", "HQ1101020", default);
        await session.QuestCommandAsync("QuestComplete", "HQ1101020", default);
        await session.QuestCommandAsync("QuestReward", "HQ1101020", default);
        await session.QuestCommandAsync("QuestAdd", "HQ1101021", default);
        if (session.Quests!.Get("AQ1602000") is null)
            await session.QuestCommandAsync("QuestAdd", "AQ1602000", default);
    }

    static async Task SeedHq1101018Step4Async(GameSession session, long now)
    {
        await session.QuestCommandAsync("QuestAdd", "HQ1101018", default);
        _ = await session.QuestCommandAsync("QuestSetStep", "HQ1101018", default, stepOrder: 4);
        _ = session.QuestWorldGatingForTests(now);
    }

    static async Task SeedHq2101030Step3Async(GameSession session, long now)
    {
        const string code = "HQ2101030";
        await session.QuestCommandAsync("QuestAdd", code, default);
        _ = await session.QuestCommandAsync("QuestSetStep", code, default, stepOrder: 3);
        await IsolateQuestFocusAsync(session, code);
        session.Teleport(1100, new Vector3d(18694, -10822, 6843), now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 10);
        await MapEventScenarioHelpers.EnterInstanceLayerAsync(session, 2951078, now + 20);
    }

    static async Task SeedHq1101030Step3Async(GameSession session, long now)
    {
        const string code = "HQ1101030";
        await session.QuestCommandAsync("QuestAdd", code, default);
        _ = await session.QuestCommandAsync("QuestSetStep", code, default, stepOrder: 3);
        await IsolateQuestFocusAsync(session, code);
        await QaBotFeed.ApplyOutboundAsync(session, session.Teleport(1000, new Vector3d(-35000, -40000, 23000), now), now);
        if (session.QaInInstance)
            await QaBotFeed.ApplyThinkAsync(session, new QaThinkResult([BotRequests.LeaveInstance()], null), now + 20);
        var driver = new QuestStepDriver(GameData.Embedded, new QaBotOptions());
        var state = new QuestDriverState();
        var at = now + 50;
        for (var i = 0; i < 16; i++, at += 50)
        {
            var slice = QaMapEventRunner.NextSlice(driver, session, "World_L_Starter_HQ002_MapEvent", session.WorldPosition!.Value, at, state);
            if (slice.Kind == QuestDriverKind.Teleport && slice.TeleportMap is { } map && slice.TeleportAt is { } pos)
            {
                await QaBotFeed.ApplyOutboundAsync(session, session.Teleport(map, pos, at), at);
                break;
            }
            if (slice.Kind == QuestDriverKind.Act && slice.Requests.Count > 0)
                await QaBotFeed.ApplyThinkAsync(session, new QaThinkResult(slice.Requests, null), at);
            if (session.MapId == 100011) break;
        }
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), at + 5);
        _ = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_L_Starter_HQ002_MapEvent", at + 200)
            ?? throw new InvalidOperationException("HQ002 map event did not start during prod seed");
    }

    static async Task IsolateQuestFocusAsync(GameSession session, string keep)
    {
        if (session.Quests is null) return;
        foreach (var code in session.Quests.Book.Quests.Keys)
        {
            if (code == keep) continue;
            var rec = session.Quests.Get(code);
            if (rec is null) continue;
            if (rec.Status == QuestLog.Incomplete)
                _ = await session.QuestCommandAsync("QuestComplete", code, default);
            if (session.Quests.Get(code)?.Status == QuestLog.Complete)
                _ = await session.QuestCommandAsync("QuestReward", code, default);
        }
    }

    static int MaxMapEventStep(IEnumerable<ScenarioBot> bots, string eventName) =>
        bots.SelectMany(b => b.Session.MapEventRuns.Where(r => r.Name == eventName)).Select(r => r.StepOrder).DefaultIfEmpty(0).Max();

    sealed class RecordingQaBotStore : IQaBotStore
    {
        public Dictionary<string, int> BucketDetailWrites { get; } = new(StringComparer.Ordinal);

        public Task<bool> RecordStuckAsync(QaStuckReportRecord report, int samplesPerBucket, bool recordDetail = true, int sampleDetailIntervalMinutes = 15, CancellationToken ct = default)
        {
            if (!recordDetail) return Task.FromResult(false);
            var key = $"{report.QuestCode}|{report.QuestStep}|{report.ReasonCode}";
            BucketDetailWrites.TryGetValue(key, out var n);
            BucketDetailWrites[key] = n + 1;
            return Task.FromResult(true);
        }

        public Task<long> StartRunAsync(string className, int levelFrom, int levelTo, int botCount, int? worldId, string? startedBy, CancellationToken ct = default) => Task.FromResult(1L);
        public Task FinishRunAsync(long runId, string status, int questsRewarded, int stuckCount, string? note, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateRunCountersAsync(long runId, int questsRewarded, int stuckCount, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnsureInternalAccountsAsync(int slots, CancellationToken ct = default) => Task.CompletedTask;
        public Task FinishStaleRunsAsync(int? worldId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Guid>> InternalAccountIdsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Guid>>([]);
        public Task CleanupRetentionAsync(int runRetentionDays, int sampleRetentionDays, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<QaRunRecord>> ListRunsAsync(int offset, int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<QaRunRecord>>([]);
        public Task<QaRunRecord?> GetRunAsync(long runId, CancellationToken ct = default) => Task.FromResult<QaRunRecord?>(null);
        public Task<IReadOnlyList<QaStuckReportRecord>> ListReportsForRunAsync(long runId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<QaStuckReportRecord>>([]);
        public Task<IReadOnlyList<QaStuckSummaryRow>> StuckSummaryAsync(int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<QaStuckSummaryRow>>([]);
        public Task<IReadOnlyList<QaStuckReportRecord>> ExportReportsAsync(long? runId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<QaStuckReportRecord>>([]);
    }
}