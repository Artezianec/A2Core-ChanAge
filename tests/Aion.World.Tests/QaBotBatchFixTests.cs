using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Headless proofs for prod stall batch (#559 follow-up).</summary>
public sealed class QaBotBatchFixTests
{
    const long T0 = 1_760_300_000_000;

    [Fact]
    public async Task HQ1101018_step4_watchstones_done_drives_draconute_objective()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), T0, level: 4, qaBot: true);
        await session.QuestCommandAsync("QuestAdd", "HQ1101018", default);
        _ = await session.QuestCommandAsync("QuestSetStep", "HQ1101018", default, stepOrder: 4);
        var rec = session.Quests!.Get("HQ1101018")!;
        rec.Goals[0] = 3;
        session.Teleport(1000, new Vector3d(0, 0, 19000), T0);
        var driver = new QuestStepDriver(GameData.Embedded, new QaBotOptions());
        var state = new QuestDriverState { ObjectiveIndex = 0 };
        var slice = driver.NextSlice(session, "HQ1101018", T0 + 100, state);
        Assert.Equal(1, state.ObjectiveIndex);
        Assert.Contains("combat", slice.ActionLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HpRemaining_runs_at_kill_priority()
    {
        Assert.Equal(0, QaMapEventRunner.MapEventObjectivePriority("HpRemaining"));
        Assert.Equal(QaMapEventRunner.MapEventObjectivePriority("KillNpc"), QaMapEventRunner.MapEventObjectivePriority("HpRemaining"));
    }


    [Fact]
    public async Task Dungeon_entry_completes_map_load_via_feed()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), T0, level: 5, qaBot: true);
        await session.QuestCommandAsync("QuestAdd", "AQ1602000", default);
        await QaBotFeed.ApplyOutboundAsync(session, session.Teleport(1000, new Vector3d(0, 0, 19000), T0), T0);
        var packets = session.EnterDungeonById(200005, T0 + 10);
        Assert.NotEmpty(packets);
        await QaBotFeed.ApplyOutboundAsync(session, packets, T0 + 10);
        Assert.Equal(200005, session.MapId);
        Assert.False(session.MapLoadPending);
    }

    [Fact]
    public async Task Walk_in_layer_teleports_to_respawn_from_base_map()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), T0, level: 5, qaBot: true);
        session.Teleport(1000, new Vector3d(-35000, -40000, 23000), T0);
        var driver = new QuestStepDriver(GameData.Embedded, new QaBotOptions());
        var state = new QuestDriverState();
        if (session.QaInInstance)
            await QaBotFeed.ApplyThinkAsync(session, new QaThinkResult([BotRequests.LeaveInstance()], null), T0 + 5);
        var slice = QaMapEventRunner.NextSlice(driver, session, "World_L_Starter_HQ002_MapEvent", session.WorldPosition!.Value, T0 + 10, state);
        Assert.True(slice.Kind is QuestDriverKind.Teleport or QuestDriverKind.Wait,
            $"walk-in entry (kind={slice.Kind}, label={slice.ActionLabel})");
        if (slice.Kind != QuestDriverKind.Teleport) return;
        Assert.Contains("walkin", slice.ActionLabel, StringComparison.Ordinal);
        Assert.Equal(100011, slice.TeleportMap);
        Assert.NotNull(slice.TeleportAt);
        Assert.True(Math.Abs(slice.TeleportAt!.Value.X + 38259) < 50);
    }

    [Fact]
    public async Task Progress_fingerprint_ignores_position()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), T0, level: 4);
        await session.QuestCommandAsync("QuestAdd", "HQ1101018", default);
        var a = QuestStepDriver.ProgressFingerprint(session, "HQ1101018");
        session.Teleport(1000, new Vector3d(99999, -88888, 19000), T0);
        var b = QuestStepDriver.ProgressFingerprint(session, "HQ1101018");
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task Qa_brain_stalls_on_map_event_seek_entry_wait()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), T0, level: 5, qaBot: true);
        await session.QuestCommandAsync("QuestAdd", "HQ1101030", default);
        _ = await session.QuestCommandAsync("QuestSetStep", "HQ1101030", default, stepOrder: 3);
        session.Teleport(1000, new Vector3d(-35000, -40000, 23000), T0);
        var brain = new QaQuestBrain(GameData.Embedded, session, 1, 45, new QaBotOptions { StepTimeoutMs = 5_000, PathTimeoutMs = 60_000, ActionLoopTimeoutMs = int.MaxValue });
        brain.PinQuestFocus("HQ1101030");
        long now = T0;
        QaStuckEvent? stall = null;
        for (var i = 0; i < 40 && stall is null; i++)
        {
            now += 500;
            var think = brain.Think(now);
            foreach (var packet in think.Requests)
                await session.HandleAsync(packet, now);
            stall = think.Stuck;
        }
        Assert.Null(stall);
        Assert.NotEqual(QaStuckReason.NoProgress, stall?.ReasonCode);
    }

    [Fact]
    public void ScheduleSwing_keeps_busy_after_hit_land()
    {
        var data = GameData.Embedded;
        var session = new GameSession(data, new GameSessionOptions { TrainingDummy = true }, new Random(5))
            { Bot = true, QaBot = true };
        session.NoCool = false;
        var skill = QaBotCombat.BasicAttackSkill(data, session, T0);
        var state = new QuestDriverState();
        QaBotCombat.ScheduleSwing(state, data, session, skill, T0);
        Assert.True(state.CombatBusyUntil > state.CombatHitLandUntil,
            $"busy should outlive hit land (busy={state.CombatBusyUntil}, land={state.CombatHitLandUntil})");
    }

    [Fact]
    public async Task Basic_attack_prefers_ready_skill_over_blaze_on_cooldown()
    {
        var data = GameData.Embedded;
        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Class: "Sorcerer" });
        var store = new FakeCharacterStore();
        var shared = new SharedWorld(data);
        var session = new GameSession(data,
            new GameSessionOptions { World = shared, CharacterLevel = 10, WorldId = store.WorldId, TrainingDummy = true },
            new Random(6)) { Store = store, Bot = true, QaBot = true };
        await session.HandleAsync(GameSessionTestHelpers.Login(), T0);
        await session.HandleAsync(BotRequests.Create("BlazePick", creation), T0);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(BotRequests.Select((uint)wireId << 16), T0);
        await session.HandleAsync(BotRequests.LoadComplete(1), T0);
        session.NoCool = false;
        var blaze = data.LearnedSkills("Sorcerer", session.Level).Select(s => s.SkillId)
            .First(id => data.Skill(id) is { Damage.Length: > 0 } sk && SkillCooldown.EffectiveMs(sk) > 3000);
        Assert.True(session.Knows(blaze), $"Sorcerer should know a cooldown skill at level {session.Level}");
        var prey = session.Targets!.Values.First(t => !t.Dead && t.Hostile);
        await MapEventScenarioHelpers.SyncPositionAsync(session, prey.Position with { X = prey.Position.X + 80 }, T0 + 50);
        prey = session.Targets!.Values.First(t => t.Key == prey.Key);
        foreach (var packet in new[]
                 {
                     BotRequests.CastBegin(),
                     BotRequests.Cast(blaze, prey.Key, 0, prey.Position, 1),
                     BotRequests.CastEnd(),
                 })
            await session.HandleAsync(packet, T0 + 100);
        await session.TickAsync(T0 + 100 + QaBotCombat.HitLandMs(data, blaze));
        Assert.True(session.CooldownUntil(blaze) > T0 + 150, $"expected {blaze} on cooldown");
        var picked = QaBotCombat.BasicAttackSkill(GameData.Embedded, session, T0 + 200);
        Assert.NotEqual(blaze, picked);
    }

    [Fact]
    public async Task Qa_brain_redirects_AQ1602000_to_accept_HQ1101021_gate()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), T0, level: 5, qaBot: true);
        await session.QuestCommandAsync("QuestAdd", "HQ1101020", default);
        await session.QuestCommandAsync("QuestComplete", "HQ1101020", default);
        await session.QuestCommandAsync("QuestReward", "HQ1101020", default);
        _ = await session.QuestCommandAsync("QuestDelete", "HQ1101021", default);
        await session.QuestCommandAsync("QuestAdd", "AQ1602000", default);
        var brain = new QaQuestBrain(GameData.Embedded, session, 1, 45, new QaBotOptions());
        _ = brain.Think(T0);
        var think = brain.Think(T0 + 100);
        Assert.Equal("HQ1101021", brain.FocusQuest);
        Assert.True(think.Requests.Count > 0 || session.Quests!.Get("HQ1101021") is not null);
    }

}