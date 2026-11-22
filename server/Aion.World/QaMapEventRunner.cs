using System.Globalization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>QA automation for hand-entered ClearMapEvent layers (starter HQ001 Robin / Maard scripts).</summary>
public static class QaMapEventRunner
{
    public static QuestDriverSlice NextSlice(QuestStepDriver driver, GameSession session, string eventName, Vector3d me, long now, QuestDriverState state)
    {
        state.ActiveMapEvent ??= eventName;
        if (MapEventTables.Embedded.Event(eventName) is not { Map: var layerMap }) 
            return QuestDriverSlice.Stuck("no_map_event", eventName);

        if (session.MapId != layerMap)
            return EnterLayerSlice(driver, session, eventName, me, now, state, layerMap);

        var run = ResolveMapEventRun(session, eventName, state);
        if (run is null)
            return QuestDriverSlice.Act([LoadComplete(1)], "map_event:load");

        if (run.Status >= MapEventRun.Complete || run.Finished)
        {
            state.ActiveMapEvent = null;
            state.MapEventPhase = 0;
            state.MapEventSubPhase = 0;
            return QuestDriverSlice.Wait("map_event:cleared");
        }

        return eventName switch
        {
            "World_L_Starter_HQ001_MapEvent" => LightHq001Slice(driver, session, run, me, now, state),
            "World_D_Starter_HQ001_MapEvent" => DarkHq001Slice(driver, session, run, me, now, state),
            _ => GenericScriptSlice(driver, session, run, me, now, state),
        };
    }

    static QuestDriverSlice EnterLayerSlice(QuestStepDriver driver, GameSession session, string eventName, Vector3d me, long now, QuestDriverState state, int layerMap)
    {
        if (session.MapLoadPending)
            return QuestDriverSlice.Wait("map_event:load_pending");

        if (session.MapId != layerMap && session.QaInInstance && !session.MapLoadPending)
            return QuestDriverSlice.Act([BotRequests.LeaveInstance()], "map_event:leave_for_entry");

        var volume = MapEventTables.Embedded.Volume(layerMap);
        if (session.MapId != layerMap && volume is not null && session.MapId == volume.Base)
        {
            var dungeonId = MapEventTables.Embedded.DungeonIdOfMap(layerMap);
            if (dungeonId > 0)
            {
                var reason = session.MapId == layerMap && session.InstanceKey != 0 ? "already_in_copy"
                    : session.LayerCopyHasDanglingRuns(layerMap) ? "reuse_dangling_run"
                    : state.ActiveMapEventRunId is not null ? "resume_active_run"
                    : "field_volume_entry";
                var packets = session.EnterDungeonById(dungeonId, now);
                if (packets.Count > 0)
                    return QuestDriverSlice.Act(packets, $"map_event:reenter_dungeon:{dungeonId}:reason={reason}");
                return QuestDriverSlice.Wait($"map_event:reenter_dungeon:{dungeonId}:refused:{reason}");
            }
        }

        var fieldEntry = InstanceTables.Embedded.EntryOfEvent(eventName);
        var fieldMap = fieldEntry?.Map ?? session.MapId ?? 0;
        if (session.MapId != fieldMap && fieldMap > 0)
        {
            var at = fieldEntry?.At ?? me;
            return QuestDriverSlice.Teleport(fieldMap, at, $"map_event:field:{eventName}");
        }

        var npcId = EntryNpc(eventName);
        var npc = npcId is { } id && id > 0 ? session.Targets.Values.FirstOrDefault(n => n.NpcId == id) : null;
        if (npc is null)
        {
            var dungeonId = MapEventTables.Embedded.DungeonIdOfMap(layerMap);
            if (dungeonId > 0 && WorldObjects.PortalEntryOf(dungeonId) is { } portal)
            {
                if (session.MapId != portal.Map)
                    return QuestDriverSlice.Teleport(portal.Map, portal.At, $"map_event:portal:{eventName}");
                var objects = WorldObjects.ObjectsOn(portal.Map).Select((o, i) => (o, i)).ToList();
                var match = objects.FirstOrDefault(x => x.o.Portal is { } p && (p.Contains(layerMap) || p.Contains(dungeonId)));
                if (match.o is not null)
                {
                    var (obj, idx) = match;
                    var key = WorldObjects.FirstKey + idx;
                    var interactMs = obj.InteractTime > 0 ? obj.InteractTime : 2500;
                    if (Flat(me, obj.At) > obj.InteractRange + WorldObjects.InteractSlack)
                        return driver.MoveTowardPublic(session, me, obj.At, obj.InteractRange, now, state, "map_event:walk_portal");
                    if (state.PendingEnvKey == key && now < state.PendingEnvUntil)
                        return QuestDriverSlice.Wait("map_event:portal_channel");
                    state.PendingEnvKey = key;
                    state.PendingEnvUntil = now + interactMs + 400;
                    return QuestDriverSlice.Act([BotRequests.UseObject(key, obj.Env)], "map_event:use_portal");
                }
            }
            if (dungeonId > 0 && session.MapId != layerMap)
            {
                var packets = session.EnterDungeonById(dungeonId, now);
                if (packets.Count > 0)
                    return QuestDriverSlice.Act(packets, $"map_event:enter_dungeon:{dungeonId}");
                return QuestDriverSlice.Stuck("dungeon_entry_refused", $"map_event:{eventName}");
            }
            return QuestDriverSlice.Wait($"map_event:seek_entry:{npcId ?? 0}");
        }

        if (Flat(me, npc.Position) > QuestStepDriver.TalkReach * 3)
            return driver.MoveTowardPublic(session, me, npc.Position, QuestStepDriver.TalkReach, now, state, "walk:layer_entry");

        return QuestDriverSlice.Act(
        [
            LoadComplete(1),
            BotRequests.NpcConversation(npc.Key),
            LoadComplete(1),
        ], $"map_event:enter:{npcId}");
    }

    static int? EntryNpc(string eventName) => eventName switch
    {
        "World_L_Starter_HQ001_MapEvent" => 2930710,
        "World_D_Starter_HQ001_MapEvent" => 2951078,
        _ => MapEventTables.Embedded.FieldEntryNpcIds(eventName).FirstOrDefault(),
    };

    static QuestDriverSlice LightHq001Slice(QuestStepDriver driver, GameSession session, MapEventRun run, Vector3d me, long now, QuestDriverState state)
    {
        if (run.StepOrder >= 2)
            state.MapEventSubPhase = Math.Max(state.MapEventSubPhase, 4);
        switch (state.MapEventSubPhase)
        {
            case 0:
                state.MapEventSubPhase = 1;
                state.MapEventPhaseUntil = now + 3400;
                return QuestDriverSlice.Wait("map_event:hq001_light:intro");
            case 1 when now < state.MapEventPhaseUntil:
                return QuestDriverSlice.Wait("map_event:hq001_light:intro");
            case 1:
            {
                var trigger = new Vector3d(-21417.5, -25923.7, 19328.8);
                if (Flat(me, trigger) > 400)
                    return driver.MoveTowardPublic(session, me, trigger, 200, now, state, "map_event:hq001_trigger");
                state.MapEventSubPhase = 2;
                state.MapEventKillIndex = 0;
                state.MapEventPhaseUntil = now;
                return QuestDriverSlice.Act([MoveUpdate(trigger)], "map_event:hq001_trigger");
            }
            case 2:
            {
                var cherubs = LivingMonsters(session, [2290000], "M_L0_Starter_HQ001_Cherubim", "Cherubim");
                if (cherubs.Count == 0)
                {
                    if (now < state.MapEventPhaseUntil + 120_000)
                        return QuestDriverSlice.Wait("map_event:hq001_light:spawn_wait");
                    return GenericScriptSlice(driver, session, run, me, now, state);
                }
                if (state.MapEventKillIndex >= cherubs.Count)
                {
                    if (run.Open.Any(x => x.Objective.Type is "KillNpc" or "PCCombatStart"))
                        return GenericScriptSlice(driver, session, run, me, now, state);
                    if (run.StepOrder < 2 && now < state.MapEventPhaseUntil + 60_000)
                        return QuestDriverSlice.Wait("map_event:hq001_light:step_wait");
                    state.MapEventSubPhase = 3;
                    return QuestDriverSlice.Wait("map_event:hq001_light:elvan");
                }
                var prey = cherubs[state.MapEventKillIndex];
                var skill = QaBotCombat.BasicAttackSkill(driver.Data, session, now);
                var reach = session.QaBot ? QaBotCombat.CastReach(driver.Data, skill) : QuestStepDriver.KillReach;
                if (Flat(me, prey.Position) > reach)
                    return driver.MoveTowardPublic(session, me, prey.Position, reach * 0.9, now, state, "map_event:hq001_kill");
                QaBotCombat.ScheduleSwing(state, driver.Data, session, skill, now);
                var yaw = Math.Atan2(prey.Position.Y - me.Y, prey.Position.X - me.X) * 180 / Math.PI;
                state.MapEventKillIndex++;
                return QuestDriverSlice.Act(
                [
                    BotRequests.CastBegin(),
                    BotRequests.Cast(skill, prey.Key, yaw, prey.Position, 1),
                    BotRequests.CastEnd(),
                ], $"map_event:hq001_kill:{prey.NpcId}");
            }
            case 3:
            {
                var elvan = session.Targets.Values.FirstOrDefault(n => n.NpcId == 2931059);
                if (elvan is null) return QuestDriverSlice.Wait("map_event:hq001_light:elvan_wait");
                state.MapEventSubPhase = 4;
                return QuestDriverSlice.Act(
                    [new PacketWriter().U16(0x8d2b).U8(2).I32(run.Id).I32(elvan.Key).I32(0).ToArray()],
                    "map_event:hq001_elvan_talk");
            }
            default:
                return GenericScriptSlice(driver, session, run, me, now, state);
        }
    }

    static QuestDriverSlice DarkHq001Slice(QuestStepDriver driver, GameSession session, MapEventRun run, Vector3d me, long now, QuestDriverState state)
    {
        var triggers = new[]
        {
            "World_D_Starter_HQ001_TriggerActor_2",
            "World_D_Starter_HQ001_TriggerActor_3",
            "World_D_Starter_HQ001_TriggerActor_4",
            "World_D_Starter_HQ001_TriggerActor_1",
        };
        if (!DarkHq001HandTriggers(run))
            state.MapEventSubPhase = triggers.Length;
        if (state.MapEventSubPhase < triggers.Length)
        {
            if (now < state.MapEventPhaseUntil)
                return QuestDriverSlice.Wait("map_event:hq001_dark:warmup");
            if (state.MapEventPhaseUntil == 0)
                state.MapEventPhaseUntil = now + 8000;
            if (!TryTriggerOrSubzonePosition(session.MapId ?? 110002, triggers[state.MapEventSubPhase], out var at))
                return QuestDriverSlice.Wait("map_event:hq001_dark:trigger_wait");
            if (Flat(me, at) > 400)
                return driver.MoveTowardPublic(session, me, at, 200, now, state, "map_event:hq001_dark:trigger");
            state.MapEventSubPhase++;
            state.MapEventPhaseUntil = now + 4000;
            if (KillHostilesOnce(driver, session, me, now, state, "M_D_Starter_HQ001_SkurvWar", "M_D_Starter_HQ001_Cherubim") is { } kill)
                return kill;
            return QuestDriverSlice.Act([MoveUpdate(at)], $"map_event:hq001_dark:trigger_{state.MapEventSubPhase}");
        }
        return GenericScriptSlice(driver, session, run, me, now, state, liveAskNpc: true);
    }

    internal static bool BombMissionActive(GameSession session, MapEventRun run, int mapId) =>
        WorldObjects.HasCarryBoxes(mapId)
        && run.Open.Any(x => x.Objective.Type == "UseEnvObj");

    internal static bool BombPickupPending(GameSession session, MapEventRun run, int mapId) =>
        BombMissionActive(session, run, mapId) && session.CarriedGroup is null;

    static bool DeferMapEventCombat(GameSession session, MapEventRun run, int mapId) =>
        BombMissionActive(session, run, mapId);

    internal static int MapEventObjectivePriority(string type, int mapId = 0, bool deferCombat = false)
    {
        if (WorldObjects.HasCarryBoxes(mapId))
        {
            return type switch
            {
                "UseEnvObj" => 0,
                "PCCombatStart" or "KillNpc" or "KillSpawner" or "KillNpcRelationshipEntity" or "KillNpcSubtype" or "KillNpcCreatureType" => deferCombat ? 9 : 2,
                "HpRemaining" => deferCombat ? 9 : 1,
                "EnterVolumePC" or "EnterSubZone" => deferCombat ? 8 : 4,
                "NpcArriveWaypoint" => 3,
                "AskNpc" or "CloseNpc" => 5,
                _ => 6,
            };
        }
        return type switch
        {
            "PCCombatStart" or "KillNpc" or "KillSpawner" or "KillNpcRelationshipEntity" or "KillNpcSubtype" or "KillNpcCreatureType" or "HpRemaining" => 0,
            "UseEnvObj" => 1,
            "NpcArriveWaypoint" => 2,
            "EnterVolumePC" or "EnterSubZone" => 3,
            "AskNpc" or "CloseNpc" => 4,
            _ => 5,
        };
    }

    static QuestDriverSlice GenericScriptSlice(QuestStepDriver driver, GameSession session, MapEventRun run, Vector3d me, long now, QuestDriverState state, bool liveAskNpc = false)
    {
        var mapId = session.MapId ?? 0;
        var deferCombat = DeferMapEventCombat(session, run, mapId);
        if (TryBombMissionSlice(driver, session, run, me, now, state, mapId) is { } bombOnly)
            return bombOnly;
        if (driver.TryHeightRepositionSlice(session, state, me, now, "map_event") is { } heightFix)
            return heightFix;
        if (TrySelfDefense(driver, session, me, now, state, deferCombat) is { } selfDefense)
            return selfDefense;
        foreach (var (_, objective) in run.Open.ToList().OrderBy(x => MapEventObjectivePriority(x.Objective.Type, mapId, deferCombat)))
        {
            if (deferCombat && objective.Type is not ("UseEnvObj" or "AskNpc" or "CloseNpc" or "CompleteCutscene" or "NpcArriveWaypoint"))
                continue;
            switch (objective.Type)
            {
                case "NpcArriveWaypoint":
                    return EscortWaitSlice(driver, session, me, now, state, objective);
                case "CompleteCutscene":
                {
                    var index = ObjectiveIndex(run, objective);
                    var cutscene = objective.Value(0);
                    if (string.IsNullOrEmpty(cutscene))
                        return QuestDriverSlice.Wait("map_event:cutscene_wait");
                    return QuestDriverSlice.Act(
                    [
                        new PacketWriter().U16(0xe334).String(cutscene).I64(0).ToArray(),
                        new PacketWriter().U16(0x9230).U8(2).I32(run.Id).I32(index).ToArray(),
                    ], $"map_event:cutscene:{cutscene}");
                }
                case "EnterVolumePC" or "EnterSubZone":
                {
                    if (!TryTriggerOrSubzonePosition(session.MapId ?? 0, objective.Value(0), out var at))
                        return QuestDriverSlice.Wait("map_event:seek_trigger");
                    if (Flat(me, at) > 300)
                        return driver.MoveTowardPublic(session, me, at, 150, now, state, "map_event:enter_vol");
                    return QuestDriverSlice.Act([MoveUpdate(at)], "map_event:enter_vol");
                }
                case "HpRemaining":
                {
                    var threshold = int.TryParse(objective.Value(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pct) ? pct : 40;
                    var names = objective.Names;
                    bool MatchesHpTarget(Target t) => MatchesHpRemainingTarget(objective, t);
                    var target = session.Targets.Values.Where(MatchesHpTarget)
                        .Where(t => t.HpMax > 0 && (int)Math.Ceiling(t.Hp * 100.0 / t.HpMax) > threshold)
                        .OrderBy(t => Flat(me, t.Position))
                        .FirstOrDefault()
                        ?? session.Targets.Values.Where(MatchesHpTarget).OrderBy(t => Flat(me, t.Position)).FirstOrDefault();
                    if (target is null)
                    {
                        if (TryClearNearbyHostiles(driver, session, me, now, state, bossApproach: true) is { } walkHostile)
                            return walkHostile;
                        if (objective.Npcs is { Length: > 0 } ids && driver.Data.NpcPlaced(ids[0], session.MapId ?? 0) is { } placed)
                        {
                            var spawnAt = new Vector3d(placed.Position[0], placed.Position[1], placed.Position[2]);
                            if (Flat(me, spawnAt) > 500)
                                return driver.MoveTowardPublic(session, me, spawnAt, 400, now, state, "map_event:walk_boss_spawn");
                        }
                        var bossSpawner = objective.Npcs is { Length: > 0 } bossIds
                            ? driver.Data.Spawns(session.MapId ?? 0).FirstOrDefault(s => bossIds.Contains(s.NpcId))?.Name
                            : null;
                        if (bossSpawner is not null && !state.KickSpawnersAttempted.Contains(bossSpawner))
                        {
                            state.KickSpawnersAttempted.Add(bossSpawner);
                            state.MapEventBossSeekSince = now;
                            session.QaStartMapEventSpawner(bossSpawner);
                            return QuestDriverSlice.Wait("map_event:spawn_kick");
                        }
                        if (state.MapEventBossSeekSince == 0)
                            state.MapEventBossSeekSince = now;
                        if (now - state.MapEventBossSeekSince > 120_000)
                            return QuestDriverSlice.Stuck("boss_not_spawned", "map_event:hp_remaining_seek");
                        return QuestDriverSlice.Wait("map_event:hp_remaining_seek");
                    }
                    state.MapEventBossSeekSince = 0;
                    var skillHp = QaBotCombat.BasicAttackSkill(driver.Data, session, now);
                    var reachHp = session.QaBot ? QaBotCombat.CastReach(driver.Data, skillHp) : QuestStepDriver.KillReach;
                    if (Flat(me, target.Position) > reachHp)
                        return driver.MoveTowardPublic(session, me, target.Position, reachHp * 0.9, now, state, "map_event:hp_remaining_walk");
                    QaBotCombat.ScheduleSwing(state, driver.Data, session, skillHp, now);
                    var yawHp = Math.Atan2(target.Position.Y - me.Y, target.Position.X - me.X) * 180 / Math.PI;
                    return QuestDriverSlice.Act(
                    [
                        BotRequests.CastBegin(),
                        BotRequests.Cast(skillHp, target.Key, yawHp, target.Position, 1),
                        BotRequests.CastEnd(),
                    ], $"map_event:hp_remaining:{target.NpcId}");
                }
                case "KillSpawner":
                {
                    var spawner = objective.Value(0);
                    var prey = session.Targets.Values.Where(t => !t.Dead && !t.CorpseRemoved)
                        .Where(t => state.CastHeightRepositionKey == t.Key || !state.CastRefusalBlacklisted.Contains(t.Key))
                        .Where(t => string.Equals(t.Name, spawner, StringComparison.OrdinalIgnoreCase)
                            || (objective.Npcs is { Length: > 0 } spawnerNpcIds && spawnerNpcIds.Contains(t.NpcId)))
                        .OrderBy(t => Flat(me, t.Position)).FirstOrDefault();
                    if (prey is null && string.IsNullOrEmpty(spawner) == false
                        && driver.Data.Spawns(session.MapId ?? 0).FirstOrDefault(s => string.Equals(s.Name, spawner, StringComparison.OrdinalIgnoreCase)) is { } spot)
                    {
                        var spawnAt = new Vector3d(spot.Position[0], spot.Position[1], spot.Position[2]);
                        if (Flat(me, spawnAt) > 500)
                            return driver.MoveTowardPublic(session, me, spawnAt, 400, now, state, "map_event:walk_boss_spawn");
                    }
                    if (prey is null && string.IsNullOrEmpty(spawner) == false)
                        prey = session.Targets.Values.Where(t => !t.Dead && t.Hostile && !MapEventSpawnerLabel(t.Name))
                            .OrderBy(t => Flat(me, t.Position)).FirstOrDefault();
                    if (prey is null)
                    {
                        if (!state.KickSpawnersAttempted.Contains(spawner))
                        {
                            state.KickSpawnersAttempted.Add(spawner);
                            state.MapEventBossSeekSince = now;
                            session.QaStartMapEventSpawner(spawner);
                            return QuestDriverSlice.Wait("map_event:spawn_kick");
                        }
                        if (state.MapEventBossSeekSince == 0)
                            state.MapEventBossSeekSince = now;
                        if (now - state.MapEventBossSeekSince > 120_000)
                            return QuestDriverSlice.Stuck("boss_not_spawned", "map_event:seek_mob");
                        if (TryTriggerOrSubzonePosition(session.MapId ?? 0, spawner, out var spawn) && Flat(me, spawn) > 600)
                            return driver.MoveTowardPublic(session, me, spawn, 200, now, state, "map_event:walk_spawn");
                        return QuestDriverSlice.Wait("map_event:seek_mob");
                    }
                    state.MapEventBossSeekSince = 0;
                    var skill = QaBotCombat.BasicAttackSkill(driver.Data, session, now);
                    var reach = session.QaBot ? QaBotCombat.CastReach(driver.Data, skill) : QuestStepDriver.KillReach;
                    var killGoal = ClampLayerMovementGoal(session.MapId ?? 0, prey.Position);
                    if (Flat(me, killGoal) > reach)
                        return driver.MoveTowardPublic(session, me, killGoal, reach * 0.9, now, state, "map_event:walk_kill");
                    QaBotCombat.ScheduleSwing(state, driver.Data, session, skill, now);
                    var yaw = Math.Atan2(prey.Position.Y - me.Y, prey.Position.X - me.X) * 180 / Math.PI;
                    return QuestDriverSlice.Act(
                    [
                        BotRequests.CastBegin(),
                        BotRequests.Cast(skill, prey.Key, yaw, prey.Position, 1),
                        BotRequests.CastEnd(),
                    ], $"map_event:kill:{prey.NpcId}");
                }
                case "PCCombatStart" or "KillNpc" or "KillNpcRelationshipEntity" or "KillNpcSubtype" or "KillNpcCreatureType":
                {
                    var spawners = objective.Value(0).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var names = spawners.Length > 0 ? spawners : objective.Names;
                    var attackNames = names.Where(n => !MapEventSpawnerLabel(n)).ToArray();
                    if (attackNames.Length > 0 && KillHostilesOnce(driver, session, me, now, state, attackNames) is { } opportunisticKill)
                        return opportunisticKill;
                    var candidates = LivingMonsters(session, objective.Npcs, names.Where(n => !MapEventSpawnerLabel(n)).ToArray())
                        .Where(t => state.CastHeightRepositionKey == t.Key || !state.CastRefusalBlacklisted.Contains(t.Key))
                        .ToList();
                    var prey = state.CombatTargetKey is { } lockKey && candidates.FirstOrDefault(t => t.Key == lockKey) is { } locked
                        ? locked
                        : candidates.OrderBy(t => Flat(me, t.Position)).FirstOrDefault();
                    if (prey is null && run.StepOrder is 7 or 8 && (objective.Npcs ?? []).Any(id => id is 2590109 or 2590110))
                    {
                        var wave = run.StepOrder == 7 ? "_Multi_1" : "_Multi_2";
                        prey = LivingMonsters(session, objective.Npcs, objective.Names.ToArray())
                            .FirstOrDefault(t => t.Name.Contains(wave, StringComparison.OrdinalIgnoreCase));
                    }
                    if (prey is null)
                    {
                        if (KickSpawnersForNpcIds(driver, session, state, now, objective.Npcs) is { } kick)
                            return kick;
                        if (names.Length > 0 && TryTriggerOrSubzonePosition(session.MapId ?? 0, names[0], out var spawn) && Flat(me, spawn) > 600)
                            return driver.MoveTowardPublic(session, me, spawn, 200, now, state, "map_event:walk_spawn");
                        return QuestDriverSlice.Wait("map_event:seek_mob");
                    }
                    var skill = QaBotCombat.BasicAttackSkill(driver.Data, session, now);
                    var reach = session.QaBot ? QaBotCombat.CastReach(driver.Data, skill) : QuestStepDriver.KillReach;
                    var walkKill = ClampLayerMovementGoal(session.MapId ?? 0, prey.Position);
                    var hpRatio = session.Resource.HpMax > 0 ? (double)session.Resource.Hp / session.Resource.HpMax : 1;
                    if (hpRatio < 0.35 && Flat(me, prey.Position) < 1200)
                    {
                        var away = new Vector3d(me.X + (me.X - prey.Position.X) * 0.4, me.Y + (me.Y - prey.Position.Y) * 0.4, me.Z);
                        return driver.MoveTowardPublic(session, me, away, 150, now, state, $"map_event:kill_retreat:{prey.NpcId}");
                    }
                    if (Flat(me, walkKill) > reach)
                        return driver.MoveTowardPublic(session, me, walkKill, reach * 0.9, now, state, "map_event:walk_kill");
                    state.CombatTargetKey = prey.Key;
                    QaBotCombat.ScheduleSwing(state, driver.Data, session, skill, now);
                    var yaw = Math.Atan2(prey.Position.Y - me.Y, prey.Position.X - me.X) * 180 / Math.PI;
                    return QuestDriverSlice.Act(
                        [
                            BotRequests.CastBegin(),
                            BotRequests.Cast(skill, prey.Key, yaw, prey.Position, 1),
                            BotRequests.CastEnd(),
                        ], $"map_event:kill:{prey.NpcId}");
                }
                case "UseEnvObj":
                {
                    var objects = WorldObjects.ObjectsOn(mapId).Select((o, i) => (o, i)).ToList();
                    bool NameMatchesObjective(WorldObject o) => objective.Names.Any(n =>
                        NameMatches(n, o.Name) || o.EnvName is { } env && NameMatches(n, env));
                    if (CarryBombSlice(driver, session, me, now, state, objects, NameMatchesObjective) is { } bombSlice)
                        return bombSlice;
                    var matches = objects
                        .Where(x => NameMatchesObjective(x.o))
                        .Where(x => session.EnvObjAvailableForUse(x.i, x.o, now))
                        .Where(x => !state.FailedEnvKeys.Contains(WorldObjects.FirstKey + x.i))
                        .OrderBy(x => Flat(me, x.o.At))
                        .ToList();
                    var placements = objects.Where(x => NameMatchesObjective(x.o)).ToList();
                    if (matches.Count == 0 && placements.Count > 0
                        && placements.All(p => state.FailedEnvKeys.Contains(WorldObjects.FirstKey + p.i)))
                        state.FailedEnvKeys.RemoveWhere(k => placements.Any(p => WorldObjects.FirstKey + p.i == k));
                    if (matches.Count == 0)
                    {
                        state.PendingEnvKey = null;
                        var anchor = placements.OrderBy(x => Flat(me, x.o.At)).FirstOrDefault();
                        if (anchor.o is not null)
                        {
                            if (Flat(me, anchor.o.At) > anchor.o.InteractRange + WorldObjects.InteractSlack)
                                return driver.MoveTowardPublic(session, me, anchor.o.At, anchor.o.InteractRange, now, state, "map_event:walk_env");
                            return QuestDriverSlice.Act(
                                [BotRequests.LoadComplete(1), MoveUpdate(anchor.o.At)],
                                "map_event:env_approach");
                        }
                        foreach (var name in objective.Names)
                        {
                            if (placements.Count > 0 || !TryTriggerOrSubzonePosition(mapId, name, out var at)) continue;
                            if (Flat(me, at) > 400)
                                return driver.MoveTowardPublic(session, me, at, 200, now, state, "map_event:walk_env_spawn");
                            return QuestDriverSlice.Act([MoveUpdate(at), BotRequests.LoadComplete(1)], "map_event:env_spawn");
                        }
                        return QuestDriverSlice.Wait("map_event:seek_env");
                    }
                    var (obj, idx) = matches[0];
                    var key = WorldObjects.FirstKey + idx;
                    var interactMs = obj.InteractTime > 0 ? obj.InteractTime : obj.Carry is not null ? GameSession.CarryPickupMs : 0;
                    if (Flat(me, obj.At) > obj.InteractRange + WorldObjects.InteractSlack)
                        return driver.MoveTowardPublic(session, me, obj.At, obj.InteractRange, now, state, "map_event:walk_env");
                    if (session.CarriedGroup is not null && obj.Destructible && session.CarriedSkillId is { } bombSkill)
                    {
                        state.PendingEnvKey = null;
                        var plantReach = GameSession.BombPlantReach(obj);
                        if (!session.EnvObjBombPlantable(idx, obj, now, me))
                        {
                            var alt = RemainingBombTowers(session, now, me, objects, NameMatchesObjective, run).FirstOrDefault();
                            if (alt.o is not null)
                                return WalkTowardBombTower(driver, session, me, now, state, alt.o);
                            return QuestDriverSlice.Wait($"map_event:bomb_plant:unplantable:env={obj.Env}");
                        }
                        if (Flat(me, obj.At) > plantReach)
                            return WalkTowardBombTower(driver, session, me, now, state, obj);
                        if (TryBombRangeProbe(driver, session, run, me, now, state, obj, bombSkill) is { } probe)
                            return probe;
                        return QuestDriverSlice.Act([BotRequests.BombGroundReady(bombSkill)], $"map_event:bomb_plant:{obj.Env}");
                    }
                    if (state.PendingEnvKey == key && now < state.PendingEnvUntil)
                        return QuestDriverSlice.Wait("map_event:env_channel");
                    if (state.PendingEnvKey == key && now >= state.PendingEnvUntil)
                    {
                        if (!session.EnvObjAvailableForUse(idx, obj, now))
                        {
                            state.PendingEnvKey = null;
                            return QuestDriverSlice.Wait("map_event:env_credited");
                        }
                        if (interactMs == 0)
                        {
                            state.PendingEnvUntil = now + 150;
                            return QuestDriverSlice.Act([BotRequests.UseObject(key, obj.Env)], "map_event:use_env");
                        }
                        state.FailedEnvKeys.Add(key);
                        state.PendingEnvKey = null;
                        return QuestDriverSlice.Wait("map_event:env_rotate");
                    }
                    state.PendingEnvKey = key;
                    state.PendingEnvUntil = now + Math.Max(interactMs, 150) + (interactMs > 0 ? 400 : 0);
                    return QuestDriverSlice.Act([BotRequests.UseObject(key, obj.Env)], "map_event:use_env");
                }
                case "AskNpc" or "CloseNpc":
                {
                    var npc = session.Targets.Values.FirstOrDefault(n =>
                        (objective.Npcs ?? []).Contains(n.NpcId)
                        || objective.Names.Any(na => NameMatches(na, n.Name)));
                    if (npc is null) return QuestDriverSlice.Wait("map_event:seek_npc");
                    if (liveAskNpc || objective.Type == "AskNpc")
                    {
                        if (Flat(me, npc.Position) > QuestStepDriver.TalkReach * 2)
                            return driver.MoveTowardPublic(session, me, npc.Position, QuestStepDriver.TalkReach, now, state, "map_event:walk_talk");
                        return QuestDriverSlice.Act(
                        [
                            new PacketWriter().U16(0xe240).Varint((uint)npc.Key).ToArray(),
                            new PacketWriter().U16(0xe242).Varint((uint)npc.Key).ToArray(),
                        ], $"map_event:talk:{npc.NpcId}");
                    }
                    var index = Math.Max(0, ObjectiveIndex(run, objective));
                    return QuestDriverSlice.Act(
                        [new PacketWriter().U16(0x8d2b).U8(2).I32(run.Id).I32(npc.Key).I32(index).ToArray()],
                        $"map_event:script_talk:{npc.NpcId}");
                }
            }
        }
        foreach (var (_, objective) in run.Open)
        {
            if (objective.Type == "PCCombatStart" && KillHostilesOnce(driver, session, me, now, state, objective.Names.ToArray()) is { } rush)
                return rush;
        }
        if (run.Open.Any())
        {
            if (now >= state.MapEventPhaseUntil)
                state.MapEventPhaseUntil = now + 2000;
            var openTypes = string.Join('+', run.Open.Select(x => x.Objective.Type).Distinct());
            return QuestDriverSlice.Wait(
                $"map_event:script_idle:id={run.Id}:step={run.StepOrder}:goals=[{string.Join(',', run.Goals)}]:open={run.Open.Count()}:types={openTypes}");
        }
        if (TryUncreditedVolumeObjective(driver, session, run, me, now, state) is { } vol)
            return vol;
        return QuestDriverSlice.Wait(
            $"map_event:script_idle:id={run.Id}:step={run.StepOrder}:goals=[{string.Join(',', run.Goals)}]:open=0");
    }

    static MapEventRun? ResolveMapEventRun(GameSession session, string eventName, QuestDriverState state)
    {
        MapEventRun? run = state.ActiveMapEventRunId is { } id
            ? session.MapEventRuns.FirstOrDefault(r => r.Id == id)
            : null;
        run ??= session.MapEventRuns.FirstOrDefault(r =>
            string.Equals(r.Name, eventName, StringComparison.OrdinalIgnoreCase));
        if (run is not null)
            state.ActiveMapEventRunId = run.Id;
        return run;
    }

    static QuestDriverSlice? TryUncreditedVolumeObjective(QuestStepDriver driver, GameSession session, MapEventRun run, Vector3d me, long now, QuestDriverState state)
    {
        var mapId = session.MapId ?? 0;
        if (BombMissionActive(session, run, mapId)) return null;
        if (run.Step is not { Objectives: { Length: > 0 } objectives }) return null;
        for (var i = 0; i < objectives.Length; i++)
        {
            var o = objectives[i];
            if (o.Type is not ("EnterVolumePC" or "EnterSubZone")) continue;
            if (i >= run.Goals.Length || run.Goals[i] >= o.Count) continue;
            if (!TryTriggerOrSubzonePosition(session.MapId ?? 0, o.Value(0), out var at))
                return QuestDriverSlice.Wait($"map_event:seek_trigger:{o.Value(0)}");
            if (Flat(me, at) > 300)
                return driver.MoveTowardPublic(session, me, at, 150, now, state, "map_event:enter_vol");
            return QuestDriverSlice.Act([MoveUpdate(at)], "map_event:enter_vol");
        }
        return null;
    }

    static bool DarkHq001HandTriggers(MapEventRun run) =>
        run.StepOrder < 5 && run.Open.Any(x => x.Objective.Type
            is "PCCombatStart" or "KillNpc" or "KillSpawner" or "KillNpcRelationshipEntity" or "KillNpcSubtype" or "KillNpcCreatureType");

    static List<Target> LivingMonsters(GameSession session, int[]? npcIds = null, params string[] namePrefixes) =>
        session.Targets.Values.Where(t => !t.Dead && (
            npcIds is { Length: > 0 } ids && ids.Contains(t.NpcId)
            || namePrefixes.Any(p => t.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                || t.Name.Contains(p, StringComparison.OrdinalIgnoreCase)))).OrderBy(t => t.NpcId).ToList();

    static bool MapEventSpawnerLabel(string name) =>
        name.Contains("Spawn_", StringComparison.Ordinal) || name.Contains("Trigger", StringComparison.Ordinal);

    static QuestDriverSlice EscortWaitSlice(QuestStepDriver driver, GameSession session, Vector3d me, long now, QuestDriverState state, MapEventObjective objective)
    {
        if (KillHostilesOnce(driver, session, me, now, state, "DracoWar", "HQ005", "DracoArc", "Draco_02") is { } kill)
            return kill;
        var escortName = objective.Value(0);
        var escort = session.Targets.Values.FirstOrDefault(n => !n.Dead && (
            NameMatches(escortName, n.Name)
            || escortName.Length > 0 && n.Name.Contains(escortName, StringComparison.OrdinalIgnoreCase)));
        if (escort is not null)
        {
            var hostileNear = session.Targets.Values.Any(t => t.Dead == false && t.Hostile && Flat(t.Position, escort.Position) < 1500);
            if (hostileNear && KillHostilesOnce(driver, session, me, now, state, "DracoWar", "HQ005") is { } defend)
                return defend;
            if (Flat(me, escort.Position) > 250)
                return driver.MoveTowardPublic(session, me, escort.Position, 200, now, state, "map_event:escort_follow");
        }
        return QuestDriverSlice.Wait("map_event:escort_wait");
    }

    static bool QaOpportunisticPrey(Target t) =>
        t.NpcId > 0 && !t.Dead && t.Hostile && !MapEventSpawnerLabel(t.Name)
        && !t.Name.Contains("Bomb", StringComparison.OrdinalIgnoreCase)
        && !t.Name.Contains("BalaurDevice", StringComparison.OrdinalIgnoreCase);

    internal static bool IsQaOpportunisticPrey(Target t) => QaOpportunisticPrey(t);

    const int BombProbeMoveBudgetMs = 20_000;
    const double BombProbeBeyondReachCm = 2500;

    static void SkipBombRangeProbe(QuestDriverState state, int runId)
    {
        state.BombRangeProbeRunsAttempted.Add(runId);
        state.BombProbeMapEventRunId = null;
        state.BombProbeGoalsBefore = null;
        state.BombProbeAttemptAt = 0;
        state.BombProbeMoveSince = 0;
    }

    static QuestDriverSlice? TryBombRangeProbe(QuestStepDriver driver, GameSession session, MapEventRun run, Vector3d me, long now,
        QuestDriverState state, WorldObject tower, int bombSkill)
    {
        if (state.BombRangeProbeRunsAttempted.Contains(run.Id) || !WorldObjects.HasCarryBoxes(session.MapId ?? 0))
            return null;
        if (session.Targets.Values.Any(t => QaOpportunisticPrey(t) && Flat(me, t.Position) < 2200))
        {
            SkipBombRangeProbe(state, run.Id);
            return null;
        }
        var useIdx = run.Step?.Objectives.ToList().FindIndex(o => o.Type == "UseEnvObj") ?? -1;
        if (useIdx < 0 || useIdx >= run.Goals.Length || run.Goals[useIdx] < 1)
            return null;
        // Exploit probe is optional: never hold tower progress on kills or other open script objectives.
        if (run.Open.Any(x => x.Objective.Type is not "UseEnvObj"))
        {
            SkipBombRangeProbe(state, run.Id);
            return null;
        }
        var plantReach = GameSession.BombPlantReach(tower);
        var minDist = plantReach + BombProbeBeyondReachCm + 80;
        if (!TryBombProbeFarPoint(session.MapId ?? 0, tower.At, minDist, out var far))
        {
            SkipBombRangeProbe(state, run.Id);
            return null;
        }
        if (state.BombProbeMoveSince > 0 && now - state.BombProbeMoveSince > BombProbeMoveBudgetMs)
        {
            SkipBombRangeProbe(state, run.Id);
            return null;
        }
        if (Flat(me, far) > 300)
        {
            state.BombProbeMoveSince = state.BombProbeMoveSince == 0 ? now : state.BombProbeMoveSince;
            return driver.MoveTowardPublic(session, me, far, 200, now, state, "map_event:bomb_range_probe_move");
        }
        state.BombRangeProbeRunsAttempted.Add(run.Id);
        state.BombProbeMapEventRunId = run.Id;
        state.BombProbeGoalsBefore = run.Goals.ToArray();
        state.BombProbeAttemptAt = now;
        state.BombProbeMoveSince = 0;
        return QuestDriverSlice.Act([BotRequests.BombGroundReady(bombSkill)], "map_event:bomb_range_probe");
    }

    /// <summary>A far plant point outside tower reach but still inside layer bounds; null when the clamp leaves none.</summary>
    internal static bool TryBombProbeFarPoint(int layerMap, Vector3d towerAt, double minDist, out Vector3d far)
    {
        var layer = layerMap;
        var deltas = new (double Dx, double Dy)[]
        {
            (minDist, minDist), (minDist, 0), (0, minDist), (-minDist, minDist),
            (minDist, -minDist), (-minDist, -minDist), (-minDist, 0), (0, -minDist),
        };
        foreach (var (dx, dy) in deltas)
        {
            var candidate = ClampLayerMovementGoal(layer, towerAt with { X = towerAt.X + dx, Y = towerAt.Y + dy });
            if (Flat(towerAt, candidate) >= minDist - 40)
            {
                far = candidate;
                return true;
            }
        }
        far = default;
        return false;
    }

    static QuestDriverSlice? KickSpawnersForNpcIds(QuestStepDriver driver, GameSession session, QuestDriverState state, long now, int[]? npcIds)
    {
        if (npcIds is not { Length: > 0 }) return null;
        foreach (var id in npcIds)
        {
            var spawner = driver.Data.Spawns(session.MapId ?? 0).FirstOrDefault(s => s.NpcId == id);
            if (spawner is null || state.KickSpawnersAttempted.Contains(spawner.Name)) continue;
            state.KickSpawnersAttempted.Add(spawner.Name);
            session.QaStartMapEventSpawner(spawner.Name);
            return QuestDriverSlice.Wait("map_event:spawn_kick");
        }
        return null;
    }

    const int SelfDefensePinMs = 12_000;

    static bool BombMissionAllowsSelfDefense(GameSession session, Vector3d me)
    {
        if (session.InCombat) return true;
        var r = session.Resource;
        if (r.HpMax > 0 && (double)r.Hp / r.HpMax < 0.60) return true;
        return session.Targets.Values.Any(t => QaOpportunisticPrey(t) && Flat(me, t.Position) < 550);
    }

    static QuestDriverSlice? TrySelfDefense(QuestStepDriver driver, GameSession session, Vector3d me, long now, QuestDriverState state, bool bombMission)
    {
        if (bombMission && !BombMissionAllowsSelfDefense(session, me)) return null;
        var engageRadius = bombMission ? 450 : 600;
        var hpRatio = session.Resource.HpMax > 0 ? (double)session.Resource.Hp / session.Resource.HpMax : 1;
        var candidates = session.Targets.Values.Where(QaOpportunisticPrey)
            .Where(t => state.CastHeightRepositionKey == t.Key || !state.CastRefusalBlacklisted.Contains(t.Key))
            .Where(t => Flat(me, t.Position) <= engageRadius)
            .ToList();
        var prey = state.SelfDefenseFocusKey is { } focus && candidates.FirstOrDefault(t => t.Key == focus) is { } pinned
            ? pinned
            : candidates.OrderBy(t => Flat(me, t.Position)).FirstOrDefault();
        if (prey is null) return null;
        if (hpRatio < 0.35 && Flat(me, prey.Position) < 900)
        {
            var away = new Vector3d(me.X + (me.X - prey.Position.X) * 0.5, me.Y + (me.Y - prey.Position.Y) * 0.5, me.Z);
            return driver.MoveTowardPublic(session, me, away, 120, now, state, $"map_event:self_defense_retreat:{prey.NpcId}");
        }
        if (state.SelfDefenseFocusKey == prey.Key && now - state.SelfDefenseFocusSince > SelfDefensePinMs)
        {
            state.CastRefusalBlacklisted.Add(prey.Key);
            state.SelfDefenseFocusKey = null;
            return null;
        }
        if (!session.InCombat && Flat(me, prey.Position) > (bombMission ? 280 : 450)) return null;
        if (state.SelfDefenseFocusKey != prey.Key)
        {
            state.SelfDefenseFocusKey = prey.Key;
            state.SelfDefenseFocusSince = now;
        }
        var skill = QaBotCombat.BasicAttackSkill(driver.Data, session, now);
        var reach = session.QaBot ? QaBotCombat.CastReach(driver.Data, skill) : QuestStepDriver.KillReach;
        var chase = ClampLayerMovementGoal(session.MapId ?? 0, prey.Position);
        if (Flat(me, chase) > reach * 1.05)
            return driver.MoveTowardPublic(session, me, chase, reach * 0.9, now, state, "map_event:self_defense_walk");
        QaBotCombat.ScheduleSwing(state, driver.Data, session, skill, now);
        var yaw = Math.Atan2(prey.Position.Y - me.Y, prey.Position.X - me.X) * 180 / Math.PI;
        return QuestDriverSlice.Act(
        [
            BotRequests.CastBegin(),
            BotRequests.Cast(skill, prey.Key, yaw, prey.Position, 1),
            BotRequests.CastEnd(),
        ], $"map_event:self_defense:{prey.NpcId}");
    }

    internal static bool MatchesHpRemainingTarget(MapEventObjective objective, Target t)
    {
        var names = objective.Names;
        return !t.Dead
            && ((objective.Npcs ?? []).Length == 0 || objective.Npcs!.Contains(t.NpcId))
            && ((objective.Npcs ?? []).Length > 0 || names.Length == 0 || names.Any(n => NameMatches(n, t.Name)));
    }

    const int HostileClearMaxPerWindow = 3;
    const int HostileClearWindowMs = 25_000;
    const double HostileClearEngageReach = 700;

    static QuestDriverSlice? TryClearNearbyHostiles(QuestStepDriver driver, GameSession session, Vector3d me, long now, QuestDriverState state, bool bossApproach = false)
    {
        if (!bossApproach) return null;
        if (state.HostileClearWindowSince == 0 || now - state.HostileClearWindowSince > HostileClearWindowMs)
        {
            state.HostileClearWindowSince = now;
            state.HostileClearCount = 0;
        }
        if (state.HostileClearCount >= HostileClearMaxPerWindow) return null;
        var slice = KillHostilesOnce(driver, session, me, now, state, HostileClearEngageReach,
            "Drakan", "InstanceLayer_2", "Multi_Cliff", "Module")
            ?? KillHostilesOnce(driver, session, me, now, state, HostileClearEngageReach,
                "M_D_Starter_HQ001_SkurvWar", "M_D_Starter_HQ001_Cherubim");
        if (slice is not null) state.HostileClearCount++;
        return slice;
    }

    static QuestDriverSlice? KillHostilesOnce(QuestStepDriver driver, GameSession session, Vector3d me, long now, QuestDriverState state, params string[] namePrefixes) =>
        KillHostilesOnce(driver, session, me, now, state, QuestStepDriver.KillReach * 1.2, namePrefixes);

    static QuestDriverSlice? KillHostilesOnce(QuestStepDriver driver, GameSession session, Vector3d me, long now, QuestDriverState state, double engageReach, params string[] namePrefixes)
    {
        var prey = session.Targets.Values.Where(t => QaOpportunisticPrey(t)
            && namePrefixes.Any(p => t.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                || t.Name.Contains(p, StringComparison.OrdinalIgnoreCase)))
            .Where(t => Flat(me, t.Position) <= engageReach)
            .OrderBy(t => Flat(me, t.Position)).FirstOrDefault();
        if (prey is null) return null;
        var skill = QaBotCombat.BasicAttackSkill(driver.Data, session, now);
        var reach = session.QaBot ? QaBotCombat.CastReach(driver.Data, skill) : QuestStepDriver.KillReach;
        if (Flat(me, prey.Position) > reach * 1.05) return null;
        QaBotCombat.ScheduleSwing(state, driver.Data, session, skill, now);
        var yaw = Math.Atan2(prey.Position.Y - me.Y, prey.Position.X - me.X) * 180 / Math.PI;
        return QuestDriverSlice.Act(
        [
            BotRequests.CastBegin(),
            BotRequests.Cast(skill, prey.Key, yaw, prey.Position, 1),
            BotRequests.CastEnd(),
        ], $"map_event:clear:{prey.Name}");
    }

    static int ObjectiveIndex(MapEventRun run, MapEventObjective objective) =>
        run.Step is { } step ? Array.FindIndex(step.Objectives, o => ReferenceEquals(o, objective)) : 0;

    static bool NameMatches(string name, string spawnName)
    {
        if (string.Equals(name, spawnName, StringComparison.OrdinalIgnoreCase)) return true;
        if (spawnName.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Keep QA movement inside hand-authored layer points (prevents chasing out of the overlay and walk-in re-entry churn).</summary>
    internal static Vector3d ClampLayerMovementGoal(int layerMap, Vector3d goal)
    {
        var points = MapEventTables.Embedded.Points(layerMap);
        if (points.Count == 0) return goal;
        var xs = points.Values.Where(p => p.Length >= 2).Select(p => p[0]).ToList();
        var ys = points.Values.Where(p => p.Length >= 2).Select(p => p[1]).ToList();
        if (xs.Count == 0) return goal;
        const double margin = 1200;
        var minX = xs.Min() - margin;
        var maxX = xs.Max() + margin;
        var minY = ys.Min() - margin;
        var maxY = ys.Max() + margin;
        return new Vector3d(Math.Clamp(goal.X, minX, maxX), Math.Clamp(goal.Y, minY, maxY), goal.Z);
    }

    static bool TryWalkInArrival(int layerMap, string eventName, out Vector3d at)
    {
        at = default;
        var points = MapEventTables.Embedded.Points(layerMap);
        var prefix = eventName.Replace("_MapEvent", "", StringComparison.Ordinal);
        foreach (var key in new[] { $"{prefix}_Respawn", $"{prefix}_StartPoint", $"{prefix}_Start_Teleport" })
        {
            if (points.TryGetValue(key, out var p) && p.Length >= 3)
            {
                at = new Vector3d(p[0], p[1], p[2]);
                return true;
            }
        }
        if (MapEventTables.Embedded.Volume(layerMap) is { Points.Length: > 0 } vol)
        {
            at = new Vector3d(vol.Points.Average(p => p[0]), vol.Points.Average(p => p[1]), (vol.ZMin + vol.ZMax) * 0.5);
            return true;
        }
        return false;
    }

    const double BombPlantApproachStopCm = 150;

    internal static List<(WorldObject o, int i)> RemainingBombTowersForTests(GameSession session, long now, Vector3d me,
        List<(WorldObject o, int i)> objects, Func<WorldObject, bool> match, MapEventRun? run = null) =>
        RemainingBombTowers(session, now, me, objects, match, run);

    static List<(WorldObject o, int i)> RemainingBombTowers(GameSession session, long now, Vector3d me,
        List<(WorldObject o, int i)> objects, Func<WorldObject, bool> match, MapEventRun? run = null)
    {
        var spentSites = objects.Where(x => match(x.o) && !session.EnvObjBombTowerRemaining(x.i, x.o, now))
            .Select(x => x.o.At).ToList();
        if (run is not null)
        {
            var credited = GameSession.BombTowerCreditsFromRun(run);
            spentSites.AddRange(objects.Where(x => match(x.o)).OrderBy(x => x.o.Name, StringComparer.Ordinal)
                .Take(credited).Select(x => x.o.At));
        }
        return objects.Where(x => match(x.o) && session.EnvObjBombTowerRemaining(x.i, x.o, now))
            .Where(x => !spentSites.Any(u => Flat(u, x.o.At) < 500))
            .OrderBy(x => x.o.Name, StringComparer.Ordinal)
            .ThenBy(x => Flat(me, x.o.At))
            .ToList();
    }

    static QuestDriverSlice WalkTowardBombTower(QuestStepDriver driver, GameSession session, Vector3d me, long now,
        QuestDriverState state, WorldObject tower)
    {
        var dist = Flat(me, tower.At);
        var plantReach = GameSession.BombPlantReach(tower);
        var stop = dist <= plantReach ? plantReach * 0.85 : BombPlantApproachStopCm;
        return driver.MoveTowardPublic(session, me, tower.At, stop, now, state,
            $"map_event:walk_plant:env={tower.Env}:dist={(int)dist}:stop={(int)stop}");
    }

    static QuestDriverSlice? TryBombMissionSlice(QuestStepDriver driver, GameSession session, MapEventRun run, Vector3d me, long now,
        QuestDriverState state, int mapId)
    {
        var needsPlant = session.CarriedGroup is not null && run.Open.Any(x => x.Objective.Type == "UseEnvObj");
        if (!BombPickupPending(session, run, mapId) && !needsPlant) return null;
        foreach (var (_, objective) in run.Open.Where(x => x.Objective.Type == "UseEnvObj"))
        {
            var objects = WorldObjects.ObjectsOn(mapId).Select((o, i) => (o, i)).ToList();
            bool NameMatchesBombObjective(WorldObject o) => objective.Names.Any(n =>
                NameMatches(n, o.Name) || o.EnvName is { } env && NameMatches(n, env));
            if (session.CarriedGroup is not null)
            {
                var remaining = RemainingBombTowers(session, now, me, objects, NameMatchesBombObjective, run);
                if (remaining.Count == 0)
                    return QuestDriverSlice.Wait("map_event:seek_plant_tower");
                var (obj, idx) = remaining[0];
                var plantReach = GameSession.BombPlantReach(obj);
                if (!session.EnvObjBombPlantable(idx, obj, now, me) || Flat(me, obj.At) > plantReach)
                    return WalkTowardBombTower(driver, session, me, now, state, obj);
                if (session.CarriedSkillId is not { } bombSkill)
                    return QuestDriverSlice.Wait("map_event:bomb_plant:need_skill");
                if (TryBombRangeProbe(driver, session, run, me, now, state, obj, bombSkill) is { } probe)
                    return probe;
                return QuestDriverSlice.Act([BotRequests.BombGroundReady(bombSkill)], $"map_event:bomb_plant:{obj.Env}");
            }
            if (CarryBombSlice(driver, session, me, now, state, objects, NameMatchesBombObjective) is { } pick)
                return pick;
        }
        if (BombPickupPending(session, run, mapId))
            return QuestDriverSlice.Wait(
                $"map_event:bomb_decision:blocked:carry={session.CarriedGroup}:open_use={run.Open.Count(x => x.Objective.Type == "UseEnvObj")}");
        return null;
    }

    static QuestDriverSlice? CarryBombSlice(
        QuestStepDriver driver, GameSession session, Vector3d me, long now, QuestDriverState state,
        List<(WorldObject o, int i)> objects, Func<WorldObject, bool> nameMatchesObjective)
    {
        var mapId = session.MapId ?? 0;
        if (!WorldObjects.HasCarryBoxes(mapId) || session.CarriedGroup is not null) return null;
        var towers = objects.Where(x => nameMatchesObjective(x.o) && x.o.Destructible).ToList();
        var focus = towers.OrderBy(x => Flat(me, x.o.At)).FirstOrDefault();
        var hasTower = focus.o is not null;
        var boxes = objects
            .Where(x => x.o.Carry is not null && session.EnvObjAvailableForUse(x.i, x.o, now))
            .OrderBy(x => Flat(me, x.o.At) + (hasTower ? Flat(x.o.At, focus.o!.At) * 0.3 : 0))
            .ToList();
        if (boxes.Count == 0) return QuestDriverSlice.Wait("map_event:seek_bomb_box");
        var (box, boxIdx) = boxes[0];
        var key = WorldObjects.FirstKey + boxIdx;
        var interactMs = GameSession.CarryPickupMs;
        var boxDist = Flat(me, box.At);
        var pickupReach = box.InteractRange + WorldObjects.InteractSlack;
        if (boxDist <= pickupReach)
        {
            state.Moving = false;
            state.Path = null;
            if (state.PendingEnvKey == key && now < state.PendingEnvUntil)
                return QuestDriverSlice.Wait($"map_event:bomb_channel:dist={(int)boxDist}");
            state.PendingEnvKey = key;
            state.PendingEnvUntil = now + interactMs + 400;
            return QuestDriverSlice.Act([BotRequests.UseObject(key, box.Env)],
                $"map_event:pickup_bomb:env={box.Env}:dist={(int)boxDist}:reachable={(int)pickupReach}");
        }
        var goal = ClampLayerMovementGoal(mapId, box.At);
        return driver.MoveTowardPublic(session, me, goal, Math.Min(pickupReach, 150), now, state,
            $"map_event:walk_bomb:env={box.Env}:dist={(int)boxDist}:reachable={(int)pickupReach}:at=({(int)box.At.X},{(int)box.At.Y})");
    }

    static bool TryTriggerOrSubzonePosition(int mapId, string name, out Vector3d at)
    {
        at = default;
        if (string.IsNullOrWhiteSpace(name)) return false;
        foreach (var trigger in WorldObjects.TriggersOn(mapId))
        {
            if (NameMatches(name, trigger.Name))
            {
                at = trigger.At;
                return true;
            }
        }
        var area = Subzones.OnMap(mapId).FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        if (area is { Points.Length: > 0 })
        {
            var cx = area.Points.Average(p => p[0]);
            var cy = area.Points.Average(p => p[1]);
            var z = area.Z.Length > 0 ? area.Z[0] : 0;
            at = new Vector3d(cx, cy, z);
            return true;
        }
        if (MapEventTables.Embedded.Points(mapId).TryGetValue(name, out var p) && p.Length >= 3)
        {
            at = new Vector3d(p[0], p[1], p[2]);
            return true;
        }
        return false;
    }

    static byte[] MoveUpdate(Vector3d at) =>
        new PacketWriter().U16(0x3701).U8(2).Vec3(at).U16(0).F32(0).U8(1).U64(1).ToArray();

    static byte[] LoadComplete(int ticket) => new PacketWriter().U16(0x3622).I32(ticket).ToArray();

    static double Flat(Vector3d a, Vector3d b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}