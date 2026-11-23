using System.Reflection;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class MapEventBombTowerReconcileTests
{
    const long Now = 1_761_050_000_000;
    const string Event = "World_L_Starter_InstanceLayer_2_MapEvent";

    static void MarkAllBombTowersSpent(GameSession session, long now)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var used = (HashSet<int>)typeof(GameSession).GetField("usedObjects", flags)!.GetValue(session)!;
        var broken = (Dictionary<int, long>)typeof(GameSession).GetField("brokenEnvObjs", flags)!.GetValue(session)!;
        foreach (var (o, i) in WorldObjects.ObjectsOn(100002).Select((o, idx) => (o, idx)).Where(x => x.o.Env == 1400824))
        {
            used.Add(i);
            broken[i] = now + GameSession.BrokenEnvObjRespawnMs;
        }
    }

    static void ForceScriptStage(MapEventRun run, int stageIndex, int stepIndex, int[] goals)
    {
        var type = typeof(MapEventRun);
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        type.GetProperty("Goals", flags)!.GetSetMethod(true)!.Invoke(run, [goals]);
        type.GetProperty("StageIndex", flags)!.GetSetMethod(true)!.Invoke(run, [stageIndex]);
        type.GetProperty("StepIndex", flags)!.GetSetMethod(true)!.Invoke(run, [stepIndex]);
        type.GetField("stepObjectivesDone", flags)!.SetValue(run, true);
    }

    static async Task<(GameSession session, MapEventRun run)> EnterBrigadeLayerAsync()
    {
        var store = new FakeCharacterStore();
        store.Quests.Add(new QuestRecord("HQ1101040", 1101040, 100, QuestLog.Rewarded, 1, new int[6]));
        store.Quests.Add(new QuestRecord("HQ1101050", 1101050, 101, QuestLog.Rewarded, 1, new int[6]));
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 1, level: 10);
        await session.QuestCommandAsync("QuestAdd", "HQ1101070", default);
        var inst = InstanceTables.Embedded.Of(100002)!;
        var center = new Vector3d(inst.Entry.Center[0], inst.Entry.Center[1], inst.Entry.Center[2]);
        session.Teleport(1000, center with { Y = center.Y - 300 }, Now + 10);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 20);
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(center), Now + 5000);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 5100);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, Event, Now + 5200, attempts: 16);
        Assert.NotNull(run);
        return (session, run!);
    }

    [Fact]
    public async Task Fresh_instance_entry_reports_zero_spent_towers_before_any_plant()
    {
        var (session, _) = await EnterBrigadeLayerAsync();
        Assert.Equal(0, session.CountSpentBombTowers(Now + 6000));
        foreach (var (o, i) in WorldObjects.ObjectsOn(100002).Select((o, i) => (o, i)).Where(x => x.o.Env == 1400824))
            Assert.True(session.EnvObjBombTowerRemaining(i, o, Now + 6000));
    }

    [Fact]
    public async Task Fresh_towers_with_stale_stage2_script_restart_to_tower_step()
    {
        var (session, run) = await EnterBrigadeLayerAsync();
        Assert.Equal(1, run.StageOrder);
        ForceScriptStage(run, stageIndex: 1, stepIndex: 0, goals: [1, 1]);
        Assert.Equal(2, run.StageOrder);
        Assert.Equal(0, session.CountSpentBombTowers(Now + 6000));
        session.ReconcileMapEventBombRuns(Now + 6000);
        Assert.Equal(1, run.StageOrder);
        Assert.Equal(1, run.StepOrder);
        Assert.Contains(run.Open, x => x.Objective.Type == "UseEnvObj");
        Assert.All(run.Goals, g => Assert.Equal(0, g));
    }

    [Fact]
    public async Task Dangling_join_with_stale_goals_reconciles_on_restore()
    {
        var (session, run) = await EnterBrigadeLayerAsync();
        ForceScriptStage(run, stageIndex: 1, stepIndex: 0, goals: [1, 3]);
        Assert.Equal(2, run.StageOrder);
        var inst = InstanceTables.Embedded.Of(100002)!;
        var field = MapEventTables.Embedded.Volume(100002)!.Base;
        var fieldAt = new Vector3d(inst.Entry.Center[0], inst.Entry.Center[1] - 800, inst.Entry.Center[2]);
        var t = Now + 30_000;
        var leave = session.Teleport(field, fieldAt, t);
        await session.HandleAsync(leave[^1], t + 50);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t + 100);
        var dungeonId = MapEventTables.Embedded.DungeonIdOfMap(100002);
        var reenter = session.EnterDungeonById(dungeonId, t + 500);
        await session.HandleAsync(reenter[^1], t + 600);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t + 700);
        var restored = session.MapEventRuns.First(r => r.Name == Event);
        Assert.Equal(1, restored.StageOrder);
        Assert.Equal(1, restored.StepOrder);
        Assert.Contains(restored.Open, x => x.Objective.Type == "UseEnvObj");
        Assert.Equal(0, session.CountSpentBombTowers(t + 800));
    }

    static void MarkPartialBombTowers(GameSession session, int count, long now)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var used = (HashSet<int>)typeof(GameSession).GetField("usedObjects", flags)!.GetValue(session)!;
        foreach (var (_, i) in WorldObjects.ObjectsOn(100002).Select((o, idx) => (o, idx)).Where(x => x.o.Env == 1400824).Take(count))
            used.Add(i);
        session.ReconcileMapEventBombRuns(now, "test");
    }

    [Fact]
    public async Task Reconcile_does_not_restart_legitimate_stage2_after_tower_peak_recorded()
    {
        var (session, run) = await EnterBrigadeLayerAsync();
        Assert.Equal(1, run.StageOrder);
        MarkPartialBombTowers(session, 2, Now + 6000);
        ForceScriptStage(run, stageIndex: 1, stepIndex: 0, goals: [0, 0]);
        Assert.Equal(2, run.StageOrder);
        session.ReconcileMapEventBombRuns(Now + 6500, "test");
        Assert.Equal(2, run.StageOrder);
    }

    [Fact]
    public async Task Reconcile_restart_survives_respawn_reload_and_stale_tower_env()
    {
        var (session, run) = await EnterBrigadeLayerAsync();
        ForceScriptStage(run, stageIndex: 2, stepIndex: 0, goals: [3, 3]);
        MarkAllBombTowersSpent(session, Now + 6000);
        Assert.Equal(3, run.StageOrder);
        Assert.Equal(3, session.CountSpentBombTowers(Now + 6000));
        session.ReconcileMapEventBombRuns(Now + 6000);
        Assert.Equal(1, run.StageOrder);
        Assert.Equal(0, session.CountSpentBombTowers(Now + 6000));

        MarkAllBombTowersSpent(session, Now + 6500);
        var t = Now + 7000;
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t);
        var restored = session.MapEventRuns.First(r => r.Name == Event);
        Assert.Equal(1, restored.StageOrder);
        Assert.Equal(0, session.CountSpentBombTowers(t + 100));

        session.NpcHitsPlayer(session.Resource.HpMax * 10, t + 200, killerKey: 2001);
        Assert.True(session.Dead);
        var rez = await session.HandleAsync(new PacketWriter().U16(RidePackets.RespawnRq).U8(RidePackets.RespawnAuto).I64(0).ToArray(), t + 300);
        Assert.Contains(rez, p => p.SequenceEqual(RidePackets.RespawnReply(0)));
        Assert.False(session.Dead);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), t + 400);
        restored = session.MapEventRuns.First(r => r.Name == Event);
        Assert.Equal(1, restored.StageOrder);
        Assert.All(restored.Goals, g => Assert.Equal(0, g));
        Assert.Equal(0, session.CountSpentBombTowers(t + 500));
    }
}