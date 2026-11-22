using Aion.Protocol.Native;

namespace Aion.World;

// The carried bomb's use at a Communication Tower. OBSERVED (live 2026-10-30, main 9874b50): with abnormal 160020 the client's F
// at the tower sends 0x384a {skill u32 = 5003} and never 0x3636 or 0x3800. 0x384a is SkillGroundReady_RQ {_skill_id}: the schema's
// opcode table is 343 below the live one (SkillLevelUp_RQ 14056 = 0x383f, SetMainTarget_RQ 14044 = 0x3833, SkillLevelReset_RS
// 14061 = 0x3844), so the ground-skill block is RQ/RS/NT Ready 0x384a-0x384c, Start 0x384d-0x384e (RQ {skill, target info}),
// Stop 0x384f-0x3851. No official sample of the replies exists: the answers below are OURS.
partial class GameSession
{
    public const ushort SkillGroundReadyRq = 0x384a, SkillGroundReadyRs = 0x384b, SkillGroundStartRq = 0x384d, SkillGroundStartRs = 0x384e,
        SkillGroundStopRq = 0x384f, SkillGroundStopRs = 0x3850;

    /// <summary>Max horizontal distance to plant on a tower: EnvObj InteractRange + <see cref="WorldObjects.InteractSlack"/> (matches InteractEnvObj_RQ).</summary>
    public static double BombPlantReach(WorldObject tower) => tower.InteractRange + WorldObjects.InteractSlack;

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

    /// <summary>After a short load or dangling join, align broken/used tower state with map-event goals so bots walk to the next tower.</summary>
    internal void SyncMapEventBombTowerCredits(long now)
    {
        if (mapEventRuns.Count == 0 || !WorldObjects.HasCarryBoxes(worldMap)) return;
        var objects = WorldObjects.ObjectsOn(worldMap);
        foreach (var run in mapEventRuns)
        {
            var credited = BombTowerCreditsFromRun(run);
            if (credited <= 0) continue;
            foreach (var (o, index) in objects.Select((o, i) => (o, i)).Where(x => x.o.Destructible)
                         .OrderBy(x => x.o.Name, StringComparer.Ordinal).Take(credited))
            {
                usedObjects.Add(index);
                if (!Broken(index, now)) brokenEnvObjs[index] = now + BrokenEnvObjRespawnMs;
                shownObjects.Remove(index);
            }
        }
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
        var candidates = shownObjects.Where(i => i < objects.Count && EnvObjBombTowerRemaining(i, objects[i], now) && InBombPlantRange(here, objects[i]))
            .OrderBy(i => objects[i].At.DistanceSquared(here)).ToList();
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
        if (selected >= 0 && selected < objects.Count && objects[selected].Destructible)
        {
            if (Broken(selected, now)) return "broken";
            if (usedObjects.Contains(selected)) return "used";
            if (!InBombPlantRange(here, objects[selected])) return "out_of_range";
            if (!PlayerMayUseEnvObj(selected, objects[selected])) return "cannot_use";
        }
        if (BombTarget(now) is not null) return "ok";
        var near = shownObjects.Where(i => i < objects.Count && objects[i].Destructible)
            .OrderBy(i => objects[i].At.DistanceSquared(here)).FirstOrDefault();
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
        var replies = new List<byte[]> { GroundReply(reply, aimed ? 0 : GroundSkillRefused, skill) };
        replies.AddRange(BreakEnvObj(index, objects[index], now));
        replies.AddRange(DropCarried(ExpireEtc));
        replies.AddRange(await QuestKillsAsync(ct));
        return await WithMapEventClearsFlushedAsync(replies, ct);
    }
}
