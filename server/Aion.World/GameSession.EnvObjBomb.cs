using Aion.Protocol.Native;

namespace Aion.World;

// The carried bomb's use at a Communication Tower. OBSERVED (live 2026-10-30, main 9874b50): with abnormal 160020 the client's F
// at the tower sends 0x384a {skill u32 = 5003} and never 0x3636 or 0x3800. 0x384a is SkillGroundReady_RQ {_skill_id}: the schema's
// opcode table is 343 below the live one (SkillLevelUp_RQ 14056 = 0x383f, SetMainTarget_RQ 14044 = 0x3833, SkillLevelReset_RS
// 14061 = 0x3844), so the ground-skill block is RQ/RS/NT Ready 0x384a-0x384c, Start 0x384d-0x384e (RQ {skill, target info}),
// Stop 0x384f-0x3851. No official sample of the replies exists: the answers below are OURS.
partial class GameSession
{
    /// <summary>Max bomb-tower env spend this instance has seen (survives death/rejoin; blocks false stage&gt;1 reconcile restarts).</summary>
    int bombTowerPeakSpent;

    internal void ResetBombTowerPeakSpent() => bombTowerPeakSpent = 0;

    internal int BombTowerEnvPeak => bombTowerPeakSpent;

    void RecordBombTowerPeak(int envSpent, int goalCredits) =>
        bombTowerPeakSpent = Math.Max(bombTowerPeakSpent, envSpent);

    internal void BumpBombTowerProgress(MapEventRun run, int envSpent, int goalCredits)
    {
        if (envSpent > 0 || goalCredits > 0) run.EverTowerCredited = true;
        run.BombTowerPeakEver = Math.Max(run.BombTowerPeakEver, Math.Max(envSpent, goalCredits));
        if (run.BombTowerPeakEver >= 3) run.BombProbeDue = true;
        bombTowerPeakSpent = Math.Max(bombTowerPeakSpent, Math.Max(envSpent, run.BombTowerPeakEver));
    }

    /// <summary>Stage&gt;1 with no tower goals on the current step: stale only when this instance never recorded tower progress before this join.</summary>
    bool StaleBombTowerStageSkip(MapEventRun run, int goalCredits, int peakBeforeJoin) =>
        run.StageOrder > 1 && goalCredits == 0
        && peakBeforeJoin == 0 && !run.EverTowerCredited && run.BombTowerPeakEver == 0 && run.MaxStageOrderReached <= 1;
    public const ushort SkillGroundReadyRq = 0x384a, SkillGroundReadyRs = 0x384b, SkillGroundStartRq = 0x384d, SkillGroundStartRs = 0x384e,
        SkillGroundStopRq = 0x384f, SkillGroundStopRs = 0x3850;

    /// <summary>Skill 5003 NeedSkillUseRange (Skill.json: 3000) lets the throw land from 30 m, not only at the
    /// tower's 650 cm interact range. Add a small latency margin so a throw at the edge is not refused.</summary>
    public const double BombThrowRange = 3_000, BombThrowSlack = 250;

    /// <summary>Max horizontal distance to plant on a tower: the bomb skill's ground range (or the tower's interact
    /// range, whichever is larger).</summary>
    public static double BombPlantReach(WorldObject tower) =>
        Math.Max(tower.InteractRange + WorldObjects.InteractSlack, BombThrowRange + BombThrowSlack);

    public static bool InBombPlantRange(Vector3d here, WorldObject tower)
    {
        var dx = here.X - tower.At.X;
        var dy = here.Y - tower.At.Y;
        return Math.Sqrt(dx * dx + dy * dy) <= BombPlantReach(tower);
    }

    /// <summary>Map-event UseEnvObj credit for bomb towers only when the player was in plant range (blocks far plants that still break visuals).</summary>
    bool BombPlantMapEventCreditAllowed(WorldObject o, Vector3d? plantAt = null)
    {
        if (!WorldObjects.HasCarryBoxes(worldMap) || !o.Destructible || CarriedGroup is null) return true;
        var here = plantAt ?? position;
        return here is { } at && InBombPlantRange(at, o);
    }

    /// <summary>Tower UseEnvObj credit when the break/plant position is in range (even after bomb drop).</summary>
    internal bool BombTowerMapEventCreditAllowed(WorldObject o, Vector3d? plantAt)
    {
        if (!WorldObjects.HasCarryBoxes(worldMap) || !IsBombTower(o)) return true;
        var here = plantAt ?? position;
        return here is { } at && InBombPlantRange(at, o);
    }

    bool MapEventBombCreditAllowed(WorldObject o, Vector3d? creditAt) =>
        IsBombTower(o) ? BombTowerMapEventCreditAllowed(o, creditAt) : BombPlantMapEventCreditAllowed(o, creditAt);

    internal long QaLastBombTowerCreditMs { get; private set; }
    internal bool QaLastBombTowerCreditInRange { get; private set; }
    internal Vector3d? QaLastBombTowerCreditPos { get; private set; }

    internal void RecordQaBombTowerCredit(WorldObject o, Vector3d? creditAt, long now)
    {
        if (!QaBot || !IsBombTower(o)) return;
        QaLastBombTowerCreditMs = now;
        QaLastBombTowerCreditPos = creditAt ?? position;
        QaLastBombTowerCreditInRange = BombTowerMapEventCreditAllowed(o, creditAt);
    }

    internal void ClearQaBombTowerCreditWatch() => QaLastBombTowerCreditMs = 0;

    WorldObject? MapEventObjectForName(string spawnName)
    {
        var objects = WorldObjects.ObjectsOn(worldMap);
        for (var i = 0; i < objects.Count; i++)
        {
            var o = objects[i];
            if (string.Equals(EventName(o), spawnName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(o.Name, spawnName, StringComparison.OrdinalIgnoreCase)
                || o.EnvName is { } env && string.Equals(env, spawnName, StringComparison.OrdinalIgnoreCase))
                return o;
        }
        return null;
    }

    /// <summary>0x3850/0x384b/0x384e result: refused.</summary>
    const int GroundSkillRefused = 1;

    /// <summary>
    /// A ground-skill reply: u16 result, i32 skill. The result is a u16 on the wire like every observed RS and the neighbouring
    /// 0x383a/0x383c cast replies (<see cref="GamePackets.CastReply(bool, int, int, byte)"/>); sent as an i32 (live 2026-10-30, main
    /// 9a80963) the client read skill 0x138b0000, never matched the reply to its pending Ready request and kept every cast locked for
    /// ~25 s after the plant, while carrying the bomb casts worked.
    /// </summary>
    static byte[] GroundReply(ushort opcode, int result, int skill) => PacketWriter.Opcode(opcode).U16(result).I32(skill).ToArray();

    /// <summary>Tower not already broken/used for this session (no distance check).</summary>
    internal bool EnvObjBombTowerRemaining(int index, WorldObject o, long now) =>
        o.Destructible && !Broken(index, now) && !usedObjects.Contains(index) && PlayerMayUseEnvObj(index, o);

    /// <summary>UseEnvObj goals on bomb-tower steps: max credited count (shared spawner names credit every matching objective).</summary>
    internal static int BombTowerCreditsFromRun(MapEventRun run)
    {
        if (run.Step is not { } step) return 0;
        var credited = 0;
        for (var i = 0; i < step.Objectives.Length && i < run.Goals.Length; i++)
        {
            var o = step.Objectives[i];
            if (o.Type != "UseEnvObj") continue;
            if (o.Names.Any(n => n.Contains("Tower", StringComparison.OrdinalIgnoreCase)))
                credited = Math.Max(credited, run.Goals[i]);
        }
        return credited;
    }

    internal const int BombTowerEnvId = 1400824;

    static bool IsBombTower(WorldObject o) => o.Env == BombTowerEnvId;

    IEnumerable<int> BombTowerIndices() =>
        WorldObjects.ObjectsOn(worldMap).Select((o, i) => (o, i)).Where(x => IsBombTower(x.o)).Select(x => x.i);

    /// <summary>Clears communication-tower spent state for this player on the current layer map.</summary>
    internal void ClearBombTowerEnvState()
    {
        foreach (var i in BombTowerIndices().ToList())
        {
            usedObjects.Remove(i);
            brokenEnvObjs.Remove(i);
            envObjHits.Remove(i);
        }
    }

    internal int CountSpentBombTowers(long now)
    {
        if (!WorldObjects.HasCarryBoxes(worldMap)) return 0;
        return BombTowerIndices().Count(i => Broken(i, now) || usedObjects.Contains(i));
    }

    /// <summary>After a short load, mirror already-spent towers into used/broken sets (never invent breaks from goals alone).</summary>
    internal void SyncMapEventBombTowerCredits(long now)
    {
        if (mapEventRuns.Count == 0 || !WorldObjects.HasCarryBoxes(worldMap)) return;
        var envSpent = CountSpentBombTowers(now);
        if (envSpent == 0) return;
        var objects = WorldObjects.ObjectsOn(worldMap);
        foreach (var run in mapEventRuns)
        {
            var credited = Math.Min(BombTowerCreditsFromRun(run), envSpent);
            if (credited <= 0) continue;
            foreach (var (o, index) in objects.Select((o, i) => (o, i)).Where(x => IsBombTower(x.o))
                         .OrderBy(x => x.o.Name, StringComparer.Ordinal).Take(credited))
            {
                usedObjects.Add(index);
                if (!Broken(index, now)) brokenEnvObjs[index] = now + BrokenEnvObjRespawnMs;
                shownObjects.Remove(index);
            }
        }
    }

    /// <summary>Dangling joins and short loads: goals must not skip towers when every destructible is still up.</summary>
    internal void ReconcileMapEventBombRuns(long now, string joinSource = "reconcile")
    {
        if (mapEventRuns.Count == 0 || !WorldObjects.HasCarryBoxes(worldMap)) return;
        foreach (var run in mapEventRuns)
        {
            if (run.Finished) continue;
            var envSpent = CountSpentBombTowers(now);
            var goalCredits = BombTowerCreditsFromRun(run);
            var peakBefore = bombTowerPeakSpent;
            LogMapEventJoin(run, joinSource, now, envSpent, goalCredits);
            if (StaleBombTowerStageSkip(run, goalCredits, peakBefore))
            {
                Note($"map_event:reconcile:restart {run.Name} (stage {run.StageOrder} goal_towers=0 env_spent={envSpent} tower_peak={bombTowerPeakSpent})");
                if (envSpent > 0) ClearBombTowerEnvState();
                if (!run.EverTowerCredited) ResetBombTowerPeakSpent();
                run.RestartScript(now);
                ((IMapEventHost)this).Progress(run);
                continue;
            }
            RecordBombTowerPeak(envSpent, goalCredits);
            BumpBombTowerProgress(run, envSpent, goalCredits);
            if (run.BombTowerPeakEver >= 3 || bombTowerPeakSpent >= 3) run.BombProbeDue = true;
            if (goalCredits == 0 && envSpent > 0 && run.StageOrder == 1)
            {
                Note($"map_event:reconcile:clear_stale_towers {run.Name} (env_spent={envSpent} without goal credit)");
                ClearBombTowerEnvState();
                envSpent = 0;
            }
            if (goalCredits > envSpent)
            {
                run.ClampBombTowerGoals(envSpent);
                ((IMapEventHost)this).Progress(run);
            }
        }
        SyncMapEventBombTowerCredits(now);
        SyncVacatedMapEventRuns(now);
    }

    internal void SanitizeBombMapEventRuns(long now, string reason)
    {
        if (!WorldObjects.HasCarryBoxes(worldMap)) return;
        foreach (var run in mapEventRuns)
        {
            if (run.Finished) continue;
            var envSpent = CountSpentBombTowers(now);
            var goalCredits = BombTowerCreditsFromRun(run);
            var peakBefore = bombTowerPeakSpent;
            if (!StaleBombTowerStageSkip(run, goalCredits, peakBefore)) continue;
            Note($"map_event:sanitize:{reason} {run.Name} run={run.Id} stage={run.StageOrder} goal_towers={goalCredits} env_spent={envSpent} peak={run.BombTowerPeakEver} max_stage={run.MaxStageOrderReached}");
            if (envSpent > 0) ClearBombTowerEnvState();
            if (!run.EverTowerCredited) ResetBombTowerPeakSpent();
            run.RestartScript(now);
            ((IMapEventHost)this).Progress(run);
            continue;
        }
        foreach (var run in mapEventRuns.Where(r => !r.Finished))
        {
            var env = CountSpentBombTowers(now);
            var goals = BombTowerCreditsFromRun(run);
            RecordBombTowerPeak(env, goals);
            BumpBombTowerProgress(run, env, goals);
        }
        SyncVacatedMapEventRuns(now);
    }

    internal void LogMapEventJoin(MapEventRun run, string source, long now, int? envSpent = null, int? goalTowers = null)
    {
        var towerMap = WorldObjects.HasCarryBoxes(worldMap);
        var env = envSpent ?? (towerMap ? CountSpentBombTowers(now) : 0);
        var towers = goalTowers ?? (towerMap ? BombTowerCreditsFromRun(run) : 0);
        var bot = CharacterName ?? character?.Name ?? "?";
        var towerPart = towerMap
            ? $":env_spent={env}:goal_towers={towers}:tower_peak={bombTowerPeakSpent}:run_peak={run.BombTowerPeakEver}:max_stage={run.MaxStageOrderReached}"
            : "";
        Note($"map_event:join:bot={bot}:inst={instanceKey}:run={run.Id}:src={source}:event={run.Name}:stage={run.StageOrder}:step={run.StepOrder}:wire={run.WireStep}:goals=[{string.Join(',', run.Goals)}]{towerPart}");
        PlaytestDebug.Action("map_event_join",
            ("bot", bot), ("instance", instanceKey), ("runId", run.Id), ("source", source),
            ("event", run.Name), ("stage", run.StageOrder), ("step", run.StepOrder), ("goals", string.Join(',', run.Goals)),
            ("envSpent", env), ("goalTowers", towers), ("towerPeak", bombTowerPeakSpent), ("map", worldMap));
    }

    internal void LogAllMapEventJoins(string source, long now)
    {
        foreach (var run in mapEventRuns.Where(r => !r.Finished))
            LogMapEventJoin(run, source, now);
    }

    /// <summary>QA/bots: in plant range and <see cref="EnvObjBombTowerRemaining"/>.</summary>
    internal bool EnvObjBombPlantable(int index, WorldObject o, long now, Vector3d? at = null)
    {
        if (position is null && at is null) return false;
        var here = at ?? position!.Value;
        return EnvObjBombTowerRemaining(index, o, now) && InBombPlantRange(here, o);
    }

    /// <summary>The tower the bomb hits: the selected target when it is a shown destructible in reach, else the nearest one.</summary>
    int? BombTarget(long now)
    {
        if (position is not { } here) return null;
        var objects = WorldObjects.ObjectsOn(worldMap);
        // Not every tower is pushed to the client's shown set while the player stands at it (map-event visibility is
        // distance-triggered); the plant is in reach, so consider every remaining tower and let the range decide.
        var candidates = objects.Select((o, i) => (o, i))
            .Where(x => EnvObjBombTowerRemaining(x.i, x.o, now) && InBombPlantRange(here, x.o))
            .OrderBy(x => x.o.At.DistanceSquared(here)).Select(x => x.i).ToList();
        if (candidates.Count == 0) return null;
        var selected = selectedTarget - WorldObjects.FirstKey;
        return candidates.Contains(selected) ? selected : candidates[0];
    }

    internal string BombPlantRefusalReason(long now, int skill)
    {
        if (resources is null) return "no_resources";
        if (dead) return "dead";
        if (skill == 0) return "no_skill";
        if (CarriedGroup is null) return "not_carrying";
        if (CarriedSkill != skill) return "wrong_skill";
        if (position is not { } here) return "no_position";
        var objects = WorldObjects.ObjectsOn(worldMap);
        var selected = selectedTarget - WorldObjects.FirstKey;
        // A stale selected target (another tower, or one already broken) must not block a tower that is in reach now.
        if (selected >= 0 && selected < objects.Count && objects[selected].Destructible
            && !Broken(selected, now) && !usedObjects.Contains(selected)
            && InBombPlantRange(here, objects[selected]) && PlayerMayUseEnvObj(selected, objects[selected]))
            return "ok";
        if (BombTarget(now) is not null) return "ok";
        var near = objects.Select((o, i) => (o, i)).Where(x => x.o.Destructible)
            .OrderBy(x => x.o.At.DistanceSquared(here)).Select(x => x.i).FirstOrDefault();
        if (near >= 0)
        {
            if (Broken(near, now)) return "broken";
            if (usedObjects.Contains(near)) return "used";
            if (!InBombPlantRange(here, objects[near])) return "out_of_range";
        }
        return "no_target";
    }

    void LogBombPlantRefused(ushort replyOpcode, int skill, string reason)
    {
        PlaytestDebug.Action("bomb_plant_refused", ("opcode", replyOpcode), ("skill", skill), ("reason", reason), ("map", worldMap));
        Note($"Bomb plant refused: {reason} (skill {skill}, map {worldMap})");
    }

    /// <summary>
    /// SkillGroundReady_RQ / SkillGroundStart_RQ / SkillGroundStop_RQ. The carried bomb's skill (the buff's ActivateSkill) breaks the
    /// tower in reach at once: the result, the tower's death and despawn, the bomb's 0x382c, then the UseEnvObj credit. Ready and
    /// Start both do it (the client may send either; only the first finds the bomb). Any other ground skill is refused, never
    /// left unanswered, so the client leaves its aiming state.
    /// </summary>
    async Task<List<byte[]>> GroundSkillAsync(ushort header, byte[] plain, long now, CancellationToken ct)
    {
        if (header == SkillGroundStopRq) return [PacketWriter.Opcode(SkillGroundStopRs).U16(0).ToArray()];
        var reply = header == SkillGroundReadyRq ? SkillGroundReadyRs : SkillGroundStartRs;
        var skill = plain.Length >= 6 ? BitConverter.ToInt32(plain, 2) : 0;
        var refusal = BombPlantRefusalReason(now, skill);
        if (refusal is not "ok")
        {
            LogBombPlantRefused(reply, skill, refusal);
            return [GroundReply(reply, GroundSkillRefused, skill)];
        }
        var objects = WorldObjects.ObjectsOn(worldMap);
        if (BombTarget(now) is not { } index || position is not { } at || !InBombPlantRange(at, objects[index]))
        {
            LogBombPlantRefused(reply, skill, "out_of_range");
            return [GroundReply(reply, GroundSkillRefused, skill)];
        }
        // Ready: refuse the aim so the client never enters its ground-aim state (SkillAction_InGroundAim blocks every skill), and
        // plant here at once, as the env-object use does. Only the reply and the plant's own packets go out: no Stop_NT. Start
        // (the client really aimed and clicked) is answered with success.
        var aimed = header == SkillGroundStartRq;
        var tower = objects[index];
        Note($"map_event:bomb_plant:env={tower.Env} idx={index} skill={skill} map={worldMap} at=({at.X:F0},{at.Y:F0},{at.Z:F0})");
        PlaytestDebug.Action("bomb_plant", ("env", tower.Env), ("index", index), ("skill", skill), ("map", worldMap));
        RecordQaBombTowerCredit(tower, at, now);
        var replies = new List<byte[]> { GroundReply(reply, aimed ? 0 : GroundSkillRefused, skill) };
        replies.AddRange(BreakEnvObj(index, tower, now));
        foreach (var run in mapEventRuns.Where(r => !r.Finished))
            BumpBombTowerProgress(run, CountSpentBombTowers(now), BombTowerCreditsFromRun(run));
        replies.AddRange(DropCarried(ExpireEtc));
        replies.AddRange(await QuestKillsAsync(ct));
        return await WithMapEventClearsFlushedAsync(replies, ct);
    }
}
