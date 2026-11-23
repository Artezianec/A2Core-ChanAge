using System.Globalization;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Drives one quest objective at a time via client request bodies (the same opcodes campaign integration tests use).
/// QA bots call this each tick; it returns at most a few packets per invocation.
/// </summary>
public sealed class QuestStepDriver(GameData data, QaBotOptions? qaOptions = null)
{
    readonly QaBotOptions? qaOptions = qaOptions;
    public GameData Data => data;
    public const double TalkReach = 120, KillReach = 400, ObjReach = 150;
    const double RunSpeed = 600;
    const int MoveUpdateMs = 200;
    static double Flat(Vector3d a, Vector3d b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    static double Yaw(Vector3d from, Vector3d to) => Math.Atan2(to.Y - from.Y, to.X - from.X) * 180 / Math.PI;

    /// <summary>Where to go for the current step's leading objective.</summary>
    public Vector3d? GoalPosition(GameSession session, string code)
    {
        if (session.Quests?.Get(code) is not { Status: QuestLog.Incomplete } record) return null;
        if (session.Quests.CurrentStep(record) is not { } step) return null;
        if (QuestTeleportResolver.Resolve(session.Quests.Book.Find(code)!, step, data) is { } dest)
            return dest.At with { Z = dest.At.Z };
        return null;
    }

    public int GoalMap(GameSession session, string code)
    {
        if (session.Quests?.Book.Find(code) is not { } def) return session.MapId ?? 0;
        if (session.Quests?.Get(code) is not { Status: QuestLog.Incomplete } record) return def.Map;
        if (session.Quests.CurrentStep(record) is { Objectives: var objs })
        {
            var map = objs.Select(o => o.Map).FirstOrDefault(m => m is > 0);
            if (map is > 0) return map.Value;
        }
        return def.Map > 0 ? def.Map : session.MapId ?? 0;
    }

    /// <summary>One advancement slice: movement, talk, kill, env, client objective, or map-event attempt.</summary>
    public QuestDriverSlice NextSlice(GameSession session, string code, long now, QuestDriverState state)
    {
        if (session.Quests?.Get(code) is not { Status: QuestLog.Incomplete } record)
            return QuestDriverSlice.Idle("no_active_quest");
        if (session.Quests.CurrentStep(record) is not { } step)
            return QuestDriverSlice.Idle("no_step");
        if (session.WorldPosition is not { } me) return QuestDriverSlice.Idle("no_position");

        if (PickLeadingObjective(step, record) is not { } pick)
            return QuestDriverSlice.Wait("objectives_met");
        var (objIndex, objective) = pick;
        if (state.ObjectiveIndex != objIndex)
        {
            state.ObjectiveIndex = objIndex;
            state.CombatTargetKey = null;
        }

        return objective.Type switch
        {
            "AskNpc" or "CloseNpc" => TalkSlice(session, code, objective, record, me, now, state),
            "KillNpc" => KillSlice(session, objective, me, now, state),
            "UseEnvObj" => EnvSlice(session, objective, me, now, state),
            "CollectItem" => CollectSlice(session, code, objective, record, objIndex, me, now, state),
            "ClearQuest" => ClearQuestSlice(session, objective, record),
            "EnterVolumePC" or "EnterSubZone" => MoveSlice(session, code, me, now, state, $"enter:{objective.Type}"),
            "CollectFakeItem" => KillSlice(session, objective with { Type = "KillNpc", Npcs = objective.Npcs ?? (objective.Npc is { } n ? [n] : []) }, me, now, state),
            "ClearMapEvent" when objective.Target is { } ev => MapEventSlice(session, ev, me, now, state),
            "EngraveTeleportArtifact" => EngraveSlice(session, objective, me, now, state),
            var t when QuestLog.ClientObjectiveTypes.Contains(t) => ClientObjSlice(session, record, objIndex, now, state),
            _ => QuestDriverSlice.Unsupported(objective.Type),
        };
    }

    static (int Index, QuestObjective Objective)? PickLeadingObjective(QuestStep step, QuestRecord record)
    {
        for (var i = 0; i < step.Objectives.Length; i++)
        {
            var o = step.Objectives[i];
            if (o.Optional) continue;
            var need = o.Count > 0 ? o.Count : 1;
            if (record.Goals[i] < need) return (i, o);
        }
        return null;
    }

    static int? ArtifactIdFromTarget(string target)
    {
        const string prefix = "TeleportArtifact_";
        if (!target.StartsWith(prefix, StringComparison.Ordinal)) return null;
        return int.TryParse(target[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    /// <summary>Walk to the race-matching Kibelisk and send InteractEnvObj_RQ (0x3636) like the live client.</summary>
    QuestDriverSlice EngraveSlice(GameSession session, QuestObjective objective, Vector3d me, long now, QuestDriverState state)
    {
        if (objective.Target is not { } target) return QuestDriverSlice.Stuck("no_artifact", "engrave");
        var artifactId = ArtifactIdFromTarget(target);
        if (artifactId is null) return QuestDriverSlice.Stuck("bad_artifact_target", $"engrave:{target}");
        if (session.Engraved.Contains(artifactId.Value))
        {
            _ = session.QuestWorldGatingForTests(now);
            return QuestDriverSlice.Wait("engrave:already");
        }
        var mapId = objective.Map is > 0 ? objective.Map.Value : session.MapId ?? 0;
        if (!TryKibeliskOnMap(mapId, artifactId.Value, session.ProfileRace, out var index, out var placement) && !Kibelisks.TryPlacement(artifactId.Value, out mapId, out _))
        {
            if (!TryKibeliskOnMap(session.MapId ?? 0, artifactId.Value, session.ProfileRace, out index, out placement))
                return QuestDriverSlice.Stuck("kibelisk_not_found", $"engrave:{artifactId}");
            mapId = session.MapId ?? 0;
        }
        else if (placement is null && !TryKibeliskOnMap(mapId, artifactId.Value, session.ProfileRace, out index, out placement))
            return QuestDriverSlice.Stuck("kibelisk_not_found", $"engrave:{artifactId}");
        if (placement is null)
            return QuestDriverSlice.Stuck("kibelisk_not_found", $"engrave:{artifactId}");

        var stand = new Vector3d(placement.Position[0], placement.Position[1], placement.Position[2]);
        if (session.MapId != mapId && mapId > 0)
            return QuestDriverSlice.Teleport(mapId, stand, $"teleport:kibelisk:{artifactId}");
        if (Flat(me, stand) > Kibelisks.InteractRange)
            return MoveToward(session, me, stand, Kibelisks.InteractRange * 0.85, now, state, $"walk:kibelisk:{artifactId}");
        var key = Kibelisks.FirstKey + index;
        return QuestDriverSlice.Act([BotRequests.UseObject(key, placement.EnvObj)], $"engrave:{artifactId}");
    }

    static bool TryKibeliskOnMap(int mapId, int artifactId, int profileRace, out int index, out KibeliskPlacement? placement)
    {
        var rel = profileRace == 2 ? (byte)9 : (byte)8;
        var placed = Kibelisks.OnMap(mapId);
        for (var i = 0; i < placed.Count; i++)
        {
            if (placed[i].ArtifactId != artifactId) continue;
            if (placed[i].Relationship != rel) continue;
            index = i;
            placement = placed[i];
            return true;
        }
        for (var i = 0; i < placed.Count; i++)
        {
            if (placed[i].ArtifactId != artifactId) continue;
            index = i;
            placement = placed[i];
            return true;
        }
        index = -1;
        placement = null;
        return false;
    }

    QuestDriverSlice MoveSlice(GameSession session, string code, Vector3d me, long now, QuestDriverState state, string label)
    {
        var goal = GoalPosition(session, code);
        var map = GoalMap(session, code);
        if (goal is null) return QuestDriverSlice.Stuck("no_goal", label);
        if (session.MapLoadPending && session.MapId != map)
            return QuestDriverSlice.Wait("map_load_pending");
        if (session.MapId != map && map > 0)
            return QuestDriverSlice.Teleport(map, goal.Value, $"teleport:{map}");
        var reach = label.Contains("EnterVolumePC", StringComparison.Ordinal) ? 80.0 : ObjReach;
        if (session.QaBot && Flat(me, goal.Value) > MaxKillSpawnChaseCm && map > 0)
            return QuestDriverSlice.Teleport(map, goal.Value, $"teleport:goal:{code}");
        if (Flat(me, goal.Value) > reach)
            return MoveToward(session, me, goal.Value, reach * 0.5, now, state, label);
        if (label.Contains("EnterVolumePC", StringComparison.Ordinal))
            return QuestDriverSlice.Act([BotRequests.Move(start: false, goal.Value, 0, now)], label);
        _ = session.QuestWorldGatingForTests(now);
        return QuestDriverSlice.Wait(label);
    }

    QuestDriverSlice TalkSlice(GameSession session, string code, QuestObjective objective, QuestRecord record, Vector3d me, long now, QuestDriverState state)
    {
        var npcId = objective.Npc ?? stepNpc(session, record);
        if (npcId is null or 0) return QuestDriverSlice.Stuck("no_npc_id", "talk");
        var npc = session.Targets.Values.FirstOrDefault(n => n.NpcId == npcId && !n.Dead);
        if (npc is null)
        {
            var spawn = NpcSpawnAt(GoalMap(session, code), npcId.Value) ?? NpcSpawnAt(session.MapId ?? 0, npcId.Value);
            if (spawn is null) return QuestDriverSlice.Stuck("npc_not_spawned", $"npc:{npcId}");
            if (Flat(me, spawn.Value) > TalkReach * 4)
                return MoveToward(session, me, spawn.Value, TalkReach, now, state, $"walk:npc:{npcId}");
            return QuestDriverSlice.Wait($"seek_npc:{npcId}");
        }
        if (Flat(me, npc.Position) > TalkReach)
            return MoveToward(session, me, npc.Position, TalkReach * 0.8, now, state, $"walk:talk:{npcId}");
        var packets = new List<byte[]>
        {
            new PacketWriter().U16(0xe240).I32(npc.Key).ToArray(),
        };
        if (record.Uid != 0)
        {
            var step = session.Quests!.CurrentStep(record);
            var objIdx = step is null ? 0 : Array.FindIndex(step.Objectives, o => o is { Type: "AskNpc" or "CloseNpc", Npc: var id } && id == npcId);
            if (objIdx >= 0)
                packets.Add(new PacketWriter().U16(0x8d2b).U8(1).I32((int)record.Uid).I32(npc.Key).ToArray());
        }
        packets.Add(new PacketWriter().U16(0xe242).I32(npc.Key).ToArray());
        return QuestDriverSlice.Act(packets, $"talk:{npcId}");
    }

    static int? stepNpc(GameSession session, QuestRecord record)
    {
        var step = session.Quests!.CurrentStep(record);
        return step?.Npc;
    }

    const int MaxHeightRepositionNudges = 5;

    internal QuestDriverSlice? TryHeightRepositionSlice(GameSession session, QuestDriverState state, Vector3d me, long now, string label)
    {
        if (state.CastHeightRepositionKey is not { } key) return null;
        if (session.Targets.GetValueOrDefault(key) is not { Dead: false } target)
        {
            state.CastHeightRepositionKey = null;
            state.HeightRepositionNudges.Remove(key);
            return null;
        }
        if (state.HeightRepositionNudges.GetValueOrDefault(key) >= MaxHeightRepositionNudges)
        {
            state.CastHeightRepositionKey = null;
            state.CastRefusalBlacklisted.Add(key);
            state.HeightRepositionNudges.Remove(key);
            return null;
        }
        var z = session.SharedMap?.Ai.Nav?.FloorAt(target.Position.X, target.Position.Y, target.Position.Z) ?? target.Position.Z;
        var dest = target.Position with { Z = z };
        if (Flat(me, dest) > 150)
            return MoveToward(session, me, dest, 100, now, state, $"{label}:height_reposition:key={key}");
        state.HeightRepositionNudges[key] = state.HeightRepositionNudges.GetValueOrDefault(key) + 1;
        return QuestDriverSlice.Act([BotRequests.Move(start: true, dest, 0, now)], $"{label}:height_reposition:nudge:key={key}");
    }

    const double MaxKillSpawnChaseCm = 12_000;
    const int MaxCombatRetreatsPerWindow = 4;
    const int CombatRetreatWindowMs = 30_000;

    static bool TryKillRetreat(QuestDriverState state, long now)
    {
        if (now - state.CombatRetreatWindowSince > CombatRetreatWindowMs)
        {
            state.CombatRetreatCount = 0;
            state.CombatRetreatWindowSince = now;
        }
        if (state.CombatRetreatCount >= MaxCombatRetreatsPerWindow) return false;
        state.CombatRetreatCount++;
        return true;
    }

    QuestDriverSlice KillSlice(GameSession session, QuestObjective objective, Vector3d me, long now, QuestDriverState state)
    {
        if (TryHeightRepositionSlice(session, state, me, now, "kill") is { } heightRepo) return heightRepo;
        var mapId = objective.Map is > 0 ? objective.Map.Value : session.MapId ?? 0;
        var npcIds = (objective.Npcs ?? (objective.Npc is { } one ? [one] : [])).ToList();
        if (npcIds.Count == 0) return QuestDriverSlice.Stuck("no_kill_target", "kill");
        if (session.MapId != mapId && mapId > 0)
        {
            var entry = npcIds
                .Select(id => NpcSpawnAt(mapId, id))
                .Where(s => s is not null)
                .Select(s => s!.Value)
                .OrderBy(s => Flat(me, s))
                .FirstOrDefault(s => Flat(me, s) <= MaxKillSpawnChaseCm * 2);
            if (entry != default)
                return QuestDriverSlice.Teleport(mapId, entry, $"teleport:kill:{npcIds[0]}");
        }
        Target? living = null;
        if (state.CombatTargetKey is { } lockedKey && session.Targets.GetValueOrDefault(lockedKey) is { Dead: false } locked
            && npcIds.Contains(locked.NpcId))
            living = locked;
        living ??= session.Targets.Values.Where(t => !t.Dead && npcIds.Contains(t.NpcId))
            .Where(t => Flat(me, t.Position) <= MaxKillSpawnChaseCm)
            .Where(t => !state.CastRefusalBlacklisted.Contains(t.Key))
            .Where(t => qaOptions is null || !session.QaBot || !QaPlayerSafety.MustNotAttack(session, t, qaOptions))
            .OrderBy(t => Flat(me, t.Position)).FirstOrDefault();
        if (living is null) state.CombatTargetKey = null;
        if (living is null)
        {
            living = session.Targets.Values.Where(t => !t.Dead && npcIds.Contains(t.NpcId))
                .Where(t => state.CastHeightRepositionKey == t.Key || !state.CastRefusalBlacklisted.Contains(t.Key))
                .OrderBy(t => Flat(me, t.Position)).FirstOrDefault();
        }
        if (living is null)
        {
            var spawn = npcIds
                .Select(id => NpcSpawnAt(mapId, id))
                .Where(s => s is not null)
                .Select(s => s!.Value)
                .Where(s => Flat(me, s) <= MaxKillSpawnChaseCm)
                .OrderBy(s => Flat(me, s))
                .FirstOrDefault();
            if (spawn == default)
                return QuestDriverSlice.Wait($"seek_mob:far:{string.Join(',', npcIds)}");
            state.KillSeekSince = state.KillSeekSince == 0 ? now : state.KillSeekSince;
            var seekRadius = now - state.KillSeekSince > 20_000 ? KillReach * 8 : KillReach * 3;
            if (Flat(me, spawn) > seekRadius)
                return MoveToward(session, me, spawn, KillReach, now, state, "walk:kill");
            return QuestDriverSlice.Wait(session.QaBot && qaOptions is not null ? "seek_mob:players_nearby" : "seek_mob");
        }
        state.KillSeekSince = 0;
        if (qaOptions is not null && session.QaBot && QaPlayerSafety.MustNotAttack(session, living, qaOptions))
            return QuestDriverSlice.Wait("defer_kill:player");
        var skill = QaBotCombat.BasicAttackSkill(data, session, now);
        var reach = session.QaBot ? QaBotCombat.CastReach(data, skill) : KillReach;
        var navZ = session.SharedMap?.Ai.Nav?.FloorAt(living.Position.X, living.Position.Y, living.Position.Z) ?? living.Position.Z;
        var combatAt = living.Position with { Z = navZ };
        var dist = Flat(me, combatAt);
        var hpRatio = session.Resource.HpMax > 0 ? (double)session.Resource.Hp / session.Resource.HpMax : 1;
        if (hpRatio < 0.40 && dist > reach && dist < 1200 && TryKillRetreat(state, now))
        {
            var away = new Vector3d(me.X + (me.X - living.Position.X) * 0.3, me.Y + (me.Y - living.Position.Y) * 0.3, me.Z);
            return MoveToward(session, me, away, 120, now, state, $"kill:retreat:{living.NpcId}");
        }
        if (dist > MaxKillSpawnChaseCm && session.QaBot && mapId > 0)
            return QuestDriverSlice.Teleport(mapId, combatAt, $"teleport:combat:{living.NpcId}");
        if (!QaBotCombat.InCastRange(data, me, living, skill))
            return MoveToward(session, me, combatAt, reach * 0.9, now, state, "walk:combat");
        state.CombatTargetKey = living.Key;
        state.Moving = false;
        state.Path = null;
        QaBotCombat.ScheduleSwing(state, data, session, skill, now);
        var yaw = Math.Atan2(living.Position.Y - me.Y, living.Position.X - me.X) * 180 / Math.PI;
        return QuestDriverSlice.Act(
        [
            BotRequests.CastBegin(),
            BotRequests.Cast(skill, living.Key, yaw, living.Position, 1),
            BotRequests.CastEnd(),
        ], $"attack:{living.NpcId}");
    }

    QuestDriverSlice EnvSlice(GameSession session, QuestObjective objective, Vector3d me, long now, QuestDriverState state)
    {
        if (objective.Envs is not { Length: > 0 } envs) return QuestDriverSlice.Unsupported("UseEnvObj");
        var map = objective.Map is > 0 ? objective.Map.Value : session.MapId ?? 0;
        if (session.MapId != map && map > 0)
        {
            var objects = WorldObjects.ObjectsOn(map).Select((o, i) => (o, i)).Where(x => envs.Contains(x.o.Env)).ToList();
            if (objects.Count > 0)
            {
                var dest = objects.MinBy(x => x.o.At)!.o.At;
                return QuestDriverSlice.Teleport(map, dest, $"teleport:env:{envs[0]}");
            }
        }
        var onMap = WorldObjects.ObjectsOn(session.MapId ?? 0).Select((o, i) => (o, i)).Where(x => envs.Contains(x.o.Env)).ToList();
        if (onMap.Count == 0) return QuestDriverSlice.Stuck("env_not_found", $"env:{envs[0]}");
        var (obj, idx) = onMap.MinBy(x => Flat(me, x.o.At))!;
        var interactMs = EnvInteractMs(obj);
        var key = WorldObjects.FirstKey + idx;
        var reach = Math.Max(ObjReach, obj.InteractRange + WorldObjects.InteractSlack * 0.5);
        if (Flat(me, obj.At) > reach)
            return MoveToward(session, me, obj.At, obj.InteractRange * 0.9, now, state, $"walk:env");
        if (state.PendingEnvKey == key && now < state.PendingEnvUntil)
            return QuestDriverSlice.Wait($"use_env:channel:{obj.Env}");
        state.PendingEnvKey = key;
        state.PendingEnvUntil = now + interactMs + 400;
        return QuestDriverSlice.Act([BotRequests.UseObject(key, obj.Env)], $"use_env:{obj.Env}");
    }

    static int EnvInteractMs(WorldObject obj) =>
        obj.InteractTime > 0 ? obj.InteractTime : obj.Carry is not null ? GameSession.CarryPickupMs : 0;

    QuestDriverSlice CollectSlice(GameSession session, string code, QuestObjective objective, QuestRecord record, int objIndex, Vector3d me, long now, QuestDriverState state)
    {
        if (objective.Count > 0 && record.Goals[objIndex] >= objective.Count)
            return QuestDriverSlice.Wait("collect:done");
        if (session.Quests!.CurrentStep(record) is not { } step)
            return QuestDriverSlice.Wait("collect");
        for (var i = 0; i < step.Objectives.Length; i++)
        {
            if (i == objIndex) continue;
            var o = step.Objectives[i];
            if (o.Optional) continue;
            return o.Type switch
            {
                "UseEnvObj" => EnvSlice(session, o, me, now, state),
                "KillNpc" => KillSlice(session, o, me, now, state),
                "EnterVolumePC" or "EnterSubZone" => MoveSlice(session, code, me, now, state, "collect:enter"),
                _ => QuestDriverSlice.Wait($"collect:via:{o.Type}"),
            };
        }
        return MoveSlice(session, code, me, now, state, "collect:goal");
    }

    static QuestDriverSlice ClearQuestSlice(GameSession session, QuestObjective objective, QuestRecord record)
    {
        if (objective.Target is not { } gate) return QuestDriverSlice.Stuck("no_clear_quest", "clear_quest");
        if (session.Quests!.Get(gate) is { Status: >= QuestLog.Complete }) return QuestDriverSlice.Wait($"clear_quest:{gate}");
        return QuestDriverSlice.Wait($"clear_quest:wait:{gate}");
    }

    QuestDriverSlice MapEventSlice(GameSession session, string eventName, Vector3d me, long now, QuestDriverState state) =>
        QaMapEventRunner.NextSlice(this, session, eventName, me, now, state);

    public QuestDriverSlice MoveTowardPublic(GameSession session, Vector3d me, Vector3d goal, double stopWithin, long now, QuestDriverState state, string label) =>
        MoveToward(session, me, goal, stopWithin, now, state, label);

    QuestDriverSlice ClientObjSlice(GameSession session, QuestRecord record, int objIndex, long now, QuestDriverState state)
    {
        if (record.Uid == 0) return QuestDriverSlice.Wait("no_uid");
        return QuestDriverSlice.Act([new PacketWriter().U16(0x9230).U8(1).I32((int)record.Uid).I32(objIndex).ToArray()], "client_objective");
    }

    Vector3d? NpcSpawnAt(int mapId, int npcId)
    {
        if (mapId <= 0) return null;
        if (data.NpcPlaced(npcId, mapId) is { } placed)
            return new Vector3d(placed.Position[0], placed.Position[1], placed.Position[2]);
        foreach (var s in data.Spawns(mapId))
            if (s.NpcId == npcId)
                return new Vector3d(s.Position[0], s.Position[1], s.Position[2]);
        return null;
    }

    QuestDriverSlice MoveToward(GameSession session, Vector3d me, Vector3d goal, double stopWithin, long now, QuestDriverState state, string label)
    {
        if (session.QaBot && Flat(me, goal) > MaxKillSpawnChaseCm
            && !label.Contains("self_defense", StringComparison.Ordinal)
            && !label.Contains("walk_bomb", StringComparison.Ordinal)
            && !label.Contains("walk_plant", StringComparison.Ordinal)
            && !label.Contains("walk_kill", StringComparison.Ordinal)
            && !label.Contains("hp_remaining_walk", StringComparison.Ordinal)
            && !label.Contains("return_battle", StringComparison.Ordinal)
            && (label.StartsWith("walk:combat", StringComparison.Ordinal) || label.StartsWith("walk:kill", StringComparison.Ordinal)
                || label.StartsWith("walk:env", StringComparison.Ordinal) || label.StartsWith("walk:npc", StringComparison.Ordinal)
                || label.StartsWith("enter:", StringComparison.Ordinal) || label.StartsWith("map_event:", StringComparison.Ordinal)
                || label.StartsWith("collect:", StringComparison.Ordinal)))
        {
            var map = session.MapId ?? 0;
            if (map > 0)
                return QuestDriverSlice.Teleport(map, goal, $"teleport:chase:{label.Split(':')[^1]}");
        }
        if (Flat(me, goal) <= stopWithin)
        {
            state.Moving = false;
            state.Path = null;
            return QuestDriverSlice.Wait(label);
        }
        if (!state.Moving)
        {
            state.Moving = true;
            state.LastMovePacketAt = now;
            return QuestDriverSlice.Act([BotRequests.Move(start: true, me, Yaw(me, goal), now)], label);
        }
        if (now - state.LastMovePacketAt < MoveUpdateMs)
            return QuestDriverSlice.Wait($"{label}:interval");
        var budget = RunSpeed * Math.Min(now - state.LastMovePacketAt, 1000) / 1000.0;
        state.LastMovePacketAt = now;
        var nav = session.SessionAi.Nav ?? session.SharedMap?.Ai.Nav;
        if (state.Path is null || state.PathIndex >= state.Path.Count || state.PathGoal is not { } planned || Flat(planned, goal) > 150)
        {
            state.Path = nav?.FindPath(me, goal) ?? [goal];
            state.PathIndex = 0;
            state.PathGoal = goal;
        }
        else state.Path[^1] = goal with { Z = state.Path[^1].Z };
        var at = me;
        while (budget > 0 && state.PathIndex < state.Path.Count)
        {
            var next = state.Path[state.PathIndex];
            var leg = Flat(at, next);
            if (leg <= budget)
            {
                at = next;
                budget -= leg;
                state.PathIndex++;
                continue;
            }
            var f = budget / leg;
            at = new Vector3d(at.X + (next.X - at.X) * f, at.Y + (next.Y - at.Y) * f, at.Z + (next.Z - at.Z) * f);
            budget = 0;
        }
        if (Flat(at, goal) < stopWithin && Flat(me, goal) > stopWithin)
        {
            var back = (Flat(me, goal) - stopWithin) / Math.Max(1, Flat(me, at));
            at = new Vector3d(me.X + (at.X - me.X) * back, me.Y + (at.Y - me.Y) * back, me.Z + (at.Z - me.Z) * back);
        }
        at = at with { Z = Ground(at, nav, session) };
        return QuestDriverSlice.Act([BotRequests.Move(start: false, at, Yaw(me, at), now)], label);
    }

    static double Ground(Vector3d at, NavMesh? nav, GameSession session) =>
        nav?.FloorAt(at.X, at.Y, at.Z) ?? session.SessionAi.Terrain?.GroundAt(at.X, at.Y)
        ?? session.SharedMap?.Ai.Terrain?.GroundAt(at.X, at.Y) ?? at.Z;

    /// <summary>Fingerprint of quest progress for stuck detection.</summary>
    public static string ProgressFingerprint(GameSession session, string code)
    {
        if (session.Quests?.Get(code) is not { } rec) return "none";
        var goals = string.Join(',', rec.Goals);
        return $"{rec.Step}|{rec.Status}|{goals}";
    }
}

public sealed class QuestDriverState
{
    public int ObjectiveIndex { get; set; }
    public HashSet<string> MapEventsTried { get; } = [];
    public bool Moving { get; set; }
    public long LastMovePacketAt { get; set; }
    public List<Vector3d>? Path { get; set; }
    public int PathIndex { get; set; }
    public Vector3d? PathGoal { get; set; }
    public int? PendingEnvKey { get; set; }
    public long PendingEnvUntil { get; set; }
    public HashSet<int> FailedEnvKeys { get; } = [];
    public HashSet<string> KickSpawnersAttempted { get; } = [];
    /// <summary>When set, map-event KillSpawner/HpRemaining has no living boss after a spawner kick.</summary>
    public long MapEventBossSeekSince { get; set; }
    public string? ActiveMapEvent { get; set; }
    public int? ActiveMapEventRunId { get; set; }
    public int MapEventPhase { get; set; }
    public int MapEventSubPhase { get; set; }
    public int MapEventKillIndex { get; set; }
    public long MapEventPhaseUntil { get; set; }
    /// <summary>After a QA cast, tick the session through this time so Skill.dat hits (0x3804) land.</summary>
    public long CombatBusyUntil { get; set; }
  /// <summary>Tick through this time once per swing; <see cref="CombatBusyUntil"/> still enforces full cooldown.</summary>
    public long CombatHitLandUntil { get; set; }
    public int? CombatTargetKey { get; set; }
    public HashSet<int> BombRangeProbeRunsAttempted { get; } = [];
    public int? BombProbeMapEventRunId { get; set; }
    public int[]? BombProbeGoalsBefore { get; set; }
    public long BombProbeAttemptAt { get; set; }
    public const int CastRefusalBlacklistAfter = 6;
    public Dictionary<int, int> CastRefusalStreak { get; } = [];
    public HashSet<int> CastRefusalBlacklisted { get; } = [];
    public int? CastHeightRepositionKey { get; set; }
    public Dictionary<int, int> HeightRepositionNudges { get; } = [];
    public int CombatRetreatCount { get; set; }
    public long CombatRetreatWindowSince { get; set; }
    public long BombProbeMoveSince { get; set; }
    public Vector3d? BombProbeAt { get; set; }
    public string? LastSliceDecisionSig { get; set; }
    public long LastSliceDecisionAt { get; set; }
    public string? LastKillSpawnerSliceSig { get; set; }
    public long LastKillSpawnerSliceAt { get; set; }
    public string? LastKillSpawnerTargetSig { get; set; }
    public long LastKillSpawnerTargetAt { get; set; }
    public int? SelfDefenseFocusKey { get; set; }
    public long SelfDefenseFocusSince { get; set; }
    public long KillSeekSince { get; set; }
    public int HostileClearCount { get; set; }
    public long HostileClearWindowSince { get; set; }
    /// <summary>After respawn, avoid re-engaging until HP has recovered (reduces death_loop on low-level bots).</summary>
    public long PostCombatRecoverUntil { get; set; }
    public string? LastHpRemainingTargetSig { get; set; }
    public long LastHpRemainingTargetAt { get; set; }
}

public sealed record QuestDriverSlice(
    QuestDriverKind Kind,
    IReadOnlyList<byte[]> Requests,
    string ActionLabel,
    int? TeleportMap = null,
    Vector3d? TeleportAt = null,
    string? StuckReason = null)
{
    public static QuestDriverSlice Idle(string label) => new(QuestDriverKind.Idle, [], label);
    public static QuestDriverSlice Wait(string label) => new(QuestDriverKind.Wait, [], label);
    public static QuestDriverSlice Act(IReadOnlyList<byte[]> packets, string label) => new(QuestDriverKind.Act, packets, label);
    public static QuestDriverSlice Teleport(int map, Vector3d at, string label) => new(QuestDriverKind.Teleport, [], label, map, at);
    public static QuestDriverSlice Stuck(string reason, string label) => new(QuestDriverKind.Stuck, [], label, StuckReason: reason);
    public static QuestDriverSlice Unsupported(string type) => new(QuestDriverKind.Unsupported, [], $"unsupported:{type}");

    static double Flat(Vector3d a, Vector3d b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}

public enum QuestDriverKind { Idle, Wait, Act, Teleport, Stuck, Unsupported }