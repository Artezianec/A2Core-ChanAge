using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QaMapEventCombatEnvTests
{
    const long T0 = 1_760_020_000_000;

    [Fact]
    public void Opportunistic_prey_skips_bombs_and_balaur_devices()
    {
        var bomb = new Target { Key = 1, Name = "M_L0_Starter_HQ004_Bomb_Muti_up_2", NpcId = 2290013, Hp = 100, HpMax = 100, Position = Vector3d.Zero, Relationship = 6 };
        var device = new Target { Key = 2, Name = "M_L0_Starter_HQ004_BalaurDevice_01", NpcId = 1, Hp = 100, HpMax = 100, Position = Vector3d.Zero, Relationship = 6 };
        var draco = new Target { Key = 3, Name = "M_L0_Starter_HQ005_DracoWar_05_1", NpcId = 2, Hp = 100, HpMax = 100, Position = Vector3d.Zero, Relationship = 6 };
        Assert.False(QaMapEventRunner.IsQaOpportunisticPrey(bomb));
        Assert.False(QaMapEventRunner.IsQaOpportunisticPrey(device));
        Assert.True(QaMapEventRunner.IsQaOpportunisticPrey(draco));
    }

    [Fact]
    public async Task HpRemaining_slice_casts_when_boss_in_range()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(store, T0, level: 8);
        await session.QuestCommandAsync("QuestAdd", "HQ2101030", default);
        for (var i = 0; i < 2; i++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ2101030", default);
        session.Teleport(1100, new Vector3d(18694, -10822, 6843), T0 + 100);
        await session.HandleAsync(BotRequests.LoadComplete(1), T0 + 110);
        await MapEventScenarioHelpers.EnterInstanceLayerAsync(session, 2951078, T0 + 120);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_D_Starter_HQ001_MapEvent", T0 + 200);
        Assert.NotNull(run);
        var t = T0 + 300;
        for (var i = 0; i < 120 && run!.StepOrder < 6; i++)
        {
            await MapEventScenarioHelpers.PlayMapEventScriptAsync(session, run, t, maxSeconds: 1, liveAskNpc: true);
            t += 1500;
        }
        Assert.True(run!.StepOrder >= 6);
        var elvida = session.Targets!.Values.FirstOrDefault(n => n.NpcId == 2950867);
        Assert.NotNull(elvida);
        elvida!.Hp = Math.Max(1, (int)(elvida.HpMax * 0.55));
        await MapEventScenarioHelpers.SyncPositionAsync(session, elvida.Position with { X = elvida.Position.X + 150 }, t + 50);
        var driver = new QuestStepDriver(GameData.Embedded, new QaBotOptions());
        var state = new QuestDriverState { ActiveMapEventRunId = run.Id };
        var slice = QaMapEventRunner.NextSlice(driver, session, run.Name, session.WorldPosition!.Value, t + 100, state);
        Assert.Contains("hp_remaining", slice.ActionLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("hp_remaining_wait", slice.ActionLabel, StringComparison.Ordinal);
        Assert.NotEqual(QuestDriverKind.Wait, slice.Kind);
    }

    [Fact]
    public async Task Hq1101070_bomb_and_tower_use_credits_server_goals()
    {
        var store = new FakeCharacterStore();
        store.Quests.Add(new QuestRecord("HQ1101040", 1101040, 100, QuestLog.Rewarded, 1, new int[6]));
        store.Quests.Add(new QuestRecord("HQ1101050", 1101050, 101, QuestLog.Rewarded, 1, new int[6]));
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, T0, race: 1, level: 9, trainingDummy: false);
        await session.QuestCommandAsync("QuestAdd", "HQ1101070", default);
        var inst = InstanceTables.Embedded.Of(100002)!;
        var center = new Vector3d(inst.Entry.Center[0], inst.Entry.Center[1], inst.Entry.Center[2]);
        session.Teleport(100002, center, T0 + 50);
        await session.HandleAsync(BotRequests.LoadComplete(1), T0 + 60);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "World_L_Starter_InstanceLayer_2_MapEvent", T0 + 500, attempts: 24);
        Assert.NotNull(run);
        Assert.Equal(1, run!.StepOrder);
        var objects = WorldObjects.ObjectsOn(100002);
        var tower = objects.Select((o, i) => (o, i)).First(x => x.o.Env == 1400824);
        var box = objects.Select((o, i) => (o, i)).Where(x => x.o.Carry is not null)
            .OrderBy(x => x.o.At.DistanceSquared(tower.o.At)).First();
        var t = T0 + 1000;
        await MapEventScenarioHelpers.SyncPositionAsync(session, box.o.At, t);
        await session.HandleAsync(BotRequests.UseObject(WorldObjects.FirstKey + box.i, box.o.Env), t + 50);
        await session.TickAsync(t + 50 + GameSession.CarryPickupMs + 100);
        Assert.NotNull(session.CarriedGroup);
        await MapEventScenarioHelpers.SyncPositionAsync(session, tower.o.At, t + 2000);
        var before = string.Join(',', run.Goals);
        await session.HandleAsync(BotRequests.UseObject(WorldObjects.FirstKey + tower.i, tower.o.Env), t + 2100);
        await session.TickAsync(t + 2500);
        Assert.NotEqual(before, string.Join(',', run.Goals));
        Assert.True(run.Goals[0] >= 1, $"tower goals unchanged: [{string.Join(',', run.Goals)}]");
    }

    [Fact]
    public async Task Instance_layer_env_walks_toward_tower_not_seek_env()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, T0, level: 10, qaBot: true);
        await session.QuestCommandAsync("QuestAdd", "HQ1101070", default);
        var inst = InstanceTables.Embedded.Of(100002)!;
        var entry = new Vector3d(inst.Entry.Center[0], inst.Entry.Center[1], inst.Entry.Center[2]);
        session.Teleport(inst.Entry.Map, entry with { Y = entry.Y - 400 }, T0 + 50);
        await session.HandleAsync(BotRequests.LoadComplete(1), T0 + 60);
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(entry), T0 + 200);
        await session.HandleAsync(BotRequests.LoadComplete(1), T0 + 210);
        for (var i = 0; i < 30; i++)
            await session.TickAsync(T0 + 300 + i * 200);
        var run = session.MapEventRuns.FirstOrDefault(r => r.Name == "World_L_Starter_InstanceLayer_2_MapEvent");
        if (run is null)
        {
            session.Teleport(100002, entry, T0 + 400);
            await session.HandleAsync(BotRequests.LoadComplete(1), T0 + 410);
            for (var i = 0; i < 20; i++)
                await session.TickAsync(T0 + 500 + i * 200);
            run = session.MapEventRuns.FirstOrDefault(r => r.Name == "World_L_Starter_InstanceLayer_2_MapEvent");
        }
        Assert.NotNull(run);
        var driver = new QuestStepDriver(GameData.Embedded, new QaBotOptions());
        var state = new QuestDriverState { ActiveMapEventRunId = run!.Id };
        var slice = QaMapEventRunner.NextSlice(driver, session, run.Name, session.WorldPosition!.Value, T0 + 800, state);
        Assert.True(
            slice.ActionLabel.Contains("walk_env", StringComparison.Ordinal)
            || slice.ActionLabel.Contains("walk_bomb", StringComparison.Ordinal)
            || slice.ActionLabel.Contains("pickup_bomb", StringComparison.Ordinal)
            || slice.ActionLabel.Contains("bomb_plant", StringComparison.Ordinal)
            || slice.ActionLabel.Contains("env_approach", StringComparison.Ordinal)
            || slice.ActionLabel.Contains("use_env", StringComparison.Ordinal)
            || slice.ActionLabel.Contains("env_channel", StringComparison.Ordinal)
            || slice.ActionLabel.Contains("self_defense", StringComparison.Ordinal),
            slice.ActionLabel);
        Assert.DoesNotContain("env_destroy", slice.ActionLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("seek_env", slice.ActionLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void HpRemaining_qa_matches_boss_spawn_name_when_objective_lists_npc_id()
    {
        var step13 = MapEventTables.Embedded.Event("Dungeon_D_0018_MapEvent")!.Stages.SelectMany(s => s.Steps).First(s => s.Id == 13);
        var hp = step13.Objectives.First(o => o.Type == "HpRemaining");
        var boss = new Target { Key = 9001, NpcId = 2701401, Name = "Dungeon_D_0018_Spawn_Boss_1", Hp = 5000, HpMax = 10_000 };
        Assert.True(QaMapEventRunner.MatchesHpRemainingTarget(hp, boss));
        Assert.DoesNotContain("M_Dungeon_D_0018_Boss_01", boss.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dungeon_D_0018_boss_step_ensure_spawns_deferred_boss()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(store, T0);
        await session.QuestCommandAsync("QuestAdd", "HQ2101050", default);
        for (var guard = 0; guard < 12 && session.Quests!.Get("HQ2101050") is { Status: QuestLog.Incomplete, Step: < 3 }; guard++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ2101050", default);
        await MapEventScenarioHelpers.EnterDungeonByIdAsync(session, 220018, T0 + 100);
        await session.HandleAsync(BotRequests.LoadComplete(1), T0 + 120);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "Dungeon_D_0018_MapEvent", T0 + 200);
        Assert.NotNull(run);
        var bossBefore = session.Targets!.Values.FirstOrDefault(t =>
            t.NpcId == 2701401
            || string.Equals(t.Name, "Dungeon_D_0018_Spawn_Boss_1", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(bossBefore);
        Assert.True(bossBefore.Dead || bossBefore.CorpseRemoved || bossBefore.Hp <= 0);
        var step13 = run!.Definition.Stages.SelectMany(s => s.Steps).First(s => s.Id == 13);
        ((IMapEventHost)session).EnsureStep(step13, new int[step13.Objectives.Length]);
        var bossAfter = session.Targets!.Values.First(t => t.NpcId == 2701401);
        Assert.False(bossAfter.Dead);
        Assert.True(bossAfter.Hp > 0);
    }

    [Fact]
    public async Task Dungeon_D_0018_map_event_tick_revives_boss_on_open_kill_step()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(store, T0);
        await session.QuestCommandAsync("QuestAdd", "HQ2101050", default);
        for (var guard = 0; guard < 12 && session.Quests!.Get("HQ2101050") is { Status: QuestLog.Incomplete, Step: < 3 }; guard++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ2101050", default);
        await MapEventScenarioHelpers.EnterDungeonByIdAsync(session, 220018, T0 + 100);
        await session.HandleAsync(BotRequests.LoadComplete(1), T0 + 120);
        var run = await MapEventScenarioHelpers.WaitForMapEventRunAsync(session, "Dungeon_D_0018_MapEvent", T0 + 200);
        Assert.NotNull(run);
        var step13 = run!.Definition.Stages.SelectMany(s => s.Steps).First(s => s.Id == 13);
        ((IMapEventHost)session).EnsureStep(step13, new int[step13.Objectives.Length]);
        var boss = session.Targets!.Values.First(t => t.NpcId == 2701401);
        Assert.True(boss.Hp > 0);
        boss.Hp = 0;
        boss.CorpseRemoved = true;
        session.QaStartMapEventSpawner("Dungeon_D_0018_Spawn_Boss_1");
        await session.TickAsync(T0 + 500);
        boss = session.Targets!.Values.First(t => t.NpcId == 2701401);
        Assert.True(boss.Hp > 0 && !boss.CorpseRemoved);
    }
}