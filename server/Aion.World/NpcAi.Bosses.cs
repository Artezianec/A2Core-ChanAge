using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Boss phases (tools/data/boss-phases.json, OURS; ported from ckrealm/A2Core c299686, AGPL-3.0). A scripted boss in a fight
/// reaches its phases as its HP falls (each once, highest first: harder hits, faster attacks, adds, a new slam), its frenzy once
/// the fight is long enough, and its telegraphed slam: it stops, winds up (0x3809 with the wind-up time), then hits every player
/// inside the slam's area (the skill's client shape from skill-areas.json, else the scripted circle) with the skill's 0x3802 and
/// a 0x3804 each. A hold or stun breaks the wind-up; walking out of the area dodges it. A wipe (the boss runs home) or its death
/// ends the fight: its living adds vanish and the phases start over.
/// INFERRED (no official boss capture): the wire is our monster cast's (NpcAi.MonsterCombat) with the wind-up as the cast time.
/// </summary>
public sealed partial class NpcAi
{
    /// <summary>Boss scripts by NpcData id.</summary>
    public BossPhases Bosses { get; set; } = BossPhases.Embedded;
    /// <summary>Aion:Bosses: phases on or off, the frenzy, a damage scale.</summary>
    public BossOptions BossRules { get; set; } = BossOptions.Default;

    /// <summary>A phase's call for adds, for the host to place (it owns the map's table): the boss, its fight, the adds, the victim to send them at.</summary>
    public sealed record SummonRequest(Target Boss, BossFight Fight, BossAdds Adds, int VictimKey);
    readonly List<SummonRequest> summonRequests = [];

    /// <summary>The adds called since the last take (the host spawns them with <see cref="BossSummons.Spawn"/> and sends them in).</summary>
    public List<SummonRequest> TakeSummons()
    {
        var taken = summonRequests.ToList();
        summonRequests.Clear();
        return taken;
    }

    /// <summary>A slam broken by a hold or stun is tried again this long after (OURS).</summary>
    public const int SlamRetryMs = 4000;
    /// <summary>After a slam the boss waits this long before its next attack (OURS).</summary>
    public const int SlamRecoverMs = 800;
    /// <summary>The shortest wind-up a slam gets, so the warning can be read (OURS).</summary>
    public const int SlamMinWindUpMs = 500;

    /// <summary>The boss's running fight (diagnostics and tests), or null.</summary>
    public BossFight? FightOf(Target target) => brains.TryGetValue(target.Key, out var brain) ? brain.Fight : null;

    /// <summary>When the slam being wound up lands (Unix ms), or null.</summary>
    public long? TelegraphDueAt(Target target) =>
        brains.TryGetValue(target.Key, out var brain) && brain.Fight?.Winding is { } w ? w.DueMs : null;

    /// <summary>Slams landed so far on this map (diagnostics).</summary>
    public int SlamsLanded { get; private set; }

    /// <summary>The scripted boss's damage multiplier now (phases, frenzy, Aion:Bosses:DamageScale), 1 for everyone else.</summary>
    double BossDamageFactor(Target attacker) =>
        brains.TryGetValue(attacker.Key, out var brain) && brain.Fight is { } fight ? fight.Damage * BossRules.DamageScale : 1.0;

    /// <summary>A skill's cooldown shortened by the scripted boss's haste (phases, frenzy).</summary>
    static long Hasted(Brain brain, int cooldown) => brain.Fight is { Haste: var haste } ? (long)(cooldown * haste) : cooldown;

    /// <summary>A slam skill of the boss's script: never chosen as an ordinary special while the phases run.</summary>
    bool IsScriptedSlam(Brain brain, int skillId) => brain.Fight is { } fight && fight.Script.SlamSkills.Contains(skillId);

    /// <summary>
    /// A scripted boss's tick: its phases, frenzy and slam. Winding: the boss is busy with a slam this tick (it neither moves
    /// nor starts another attack).
    /// </summary>
    (List<byte[]> Replies, bool Winding) BossTick(Target target, Brain brain, long now)
    {
        var replies = new List<byte[]>();
        if (!BossRules.Phases || !target.Hostile || target.OwnerKey != 0) return (replies, false);
        if (brain.Fight is null)
        {
            if (!brain.Engaged || brain.Mode != Mode.Chase || brain.VictimIsNpc || Bosses.Of(target.NpcId) is not { } script) return (replies, false);
            brain.Fight = new BossFight(script, now) { NextSlamMs = now + (script.Telegraph?.EveryMs ?? 0) / 2 };
        }
        var fight = brain.Fight;
        var percent = target.HpMax > 0 ? target.Hp * 100.0 / target.HpMax : 100;
        while (fight.Reached < fight.Script.Ordered.Count && percent <= fight.Script.Ordered[fight.Reached].At)
        {
            var phase = fight.Script.Ordered[fight.Reached++];
            fight.Damage = Math.Max(fight.Damage, phase.Damage * (fight.Frenzied ? fight.Script.Frenzy?.Damage ?? 1 : 1));
            fight.Haste = Math.Min(fight.Haste, phase.Haste * (fight.Frenzied ? fight.Script.Frenzy?.Haste ?? 1 : 1));
            if (phase.Telegraph is { } slam)
            {
                fight.Telegraph = slam;
                fight.NextSlamMs = Math.Min(fight.NextSlamMs, now + slam.EveryMs / 2);
            }
            if (phase.Notice is { Length: > 0 } notice) replies.Add(GameSession.Notice(notice, phase.NoticeRu, 22, "FFB040"));
            if (phase.Adds is { Count: > 0 } adds) summonRequests.Add(new SummonRequest(target, fight, adds, Victim(brain)));
        }
        if (fight.Script.Frenzy is { } frenzy && BossRules.Frenzy && !fight.Frenzied && !OfficialFrenzy(fight.Script.Dungeon)
            && now - fight.StartedMs >= (BossRules.FrenzySeconds > 0 ? BossRules.FrenzySeconds * 1000L : frenzy.AfterMs))
        {
            fight.Frenzied = true;
            fight.Damage *= frenzy.Damage;
            fight.Haste *= frenzy.Haste;
            if (frenzy.Notice is { Length: > 0 } notice) replies.Add(GameSession.Notice(notice, frenzy.NoticeRu, 24, "FF4040"));
        }
        if (fight.Winding is { } winding)
        {
            if (now < winding.DueMs) return (replies, true);
            fight.Winding = null;
            replies.AddRange(SlamLands(target, brain, winding.Slam, winding.Seq, winding.Centre, winding.Yaw, now));
            brain.NextAttackMs = Math.Max(brain.NextAttackMs, now + SlamRecoverMs);
            return (replies, false);
        }
        if (fight.Telegraph is { } next && now >= fight.NextSlamMs && brain.Pending is null && brain.Volley.Count == 0
            && brain.VictimKey != 0 && !brain.VictimIsNpc && !target.Held && VictimPosition(brain) is { } victimAt)
        {
            var facing = HeadingTo(target.Position, Flat(victimAt));
            var yaw = target.NoRotation ? brain.Heading : facing;
            if (!target.NoRotation) replies.AddRange(Stop(target, brain, facing, now));
            else brain.Moving = false;
            brain.ActionSeq = brain.ActionSeq % 255 + 1;
            var windUp = Math.Max(SlamWindUpMs(next), SlamMinWindUpMs);
            var due = now + windUp + HitDelayMs;
            // The area is placed at the wind-up (as CastLocation does for ordinary area casts): on the victim for an
            // on-target or ground shape, else on the boss.
            var area = SkillAreas.For(next.Skill);
            var centre = area is { IsArea: true } a && (a.OnTarget || a.Ground == "Location") ? victimAt : target.Position;
            fight.Winding = (next, due, brain.ActionSeq, centre, yaw);
            fight.NextSlamMs = due + next.EveryMs;
            replies.Add(PlayerPackets.SkillCastingStart(target.Key, next.Skill, Victim(brain), yaw, centre, windUp));
            if (next.Warning is { Length: > 0 } warning) replies.Add(GameSession.Notice(warning, next.WarningRu, 22, "FF8040"));
            return (replies, true);
        }
        return (replies, false);
    }

    /// <summary>
    /// Dungeons whose boss Frenzy the Oct 28 2026 notice already sets (Patch20261028.FrenzyMs: the new Urugugu Canyon and Vakron Sky
    /// Island Hard modes, applied to every boss hit in GameSession.DungeonBossDamage): the script's own frenzy stays off there so
    /// the two never stack.
    /// </summary>
    static bool OfficialFrenzy(string dungeon) => officialFrenzy.GetOrAdd(dungeon, name =>
        MapEventTables.Embedded.AllDungeons.Any(d => d.Value.Name == name && Patch20261028.FrenzyMs(d.Value) > 0));
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> officialFrenzy = new(StringComparer.Ordinal);

    /// <summary>The slam's wind-up: the skill's own cast time (Skill.dat) when it has one, else the script's (OURS).</summary>
    int SlamWindUpMs(BossTelegraph slam) => data?.NpcSkill(slam.Skill) is { Cast: > 0 } skill ? skill.Cast : slam.WindUpMs;

    /// <summary>Whether a player at <paramref name="at"/> is inside the slam: the skill's client shape, else the scripted circle around the boss.</summary>
    internal static bool InSlam(Target boss, BossTelegraph slam, Vector3d centre, double yaw, Vector3d at)
    {
        if (SkillAreas.For(slam.Skill) is { IsArea: true } area) return area.Contains(centre, yaw, at, PlayerRadius);
        return Distance(boss.Position, Flat(at)) <= slam.Radius + boss.Radius + PlayerRadius;
    }

    /// <summary>A slam lands: the skill start, then every player inside the area (nearest first, up to the shape's target count) takes its own roll.</summary>
    List<byte[]> SlamLands(Target target, Brain brain, BossTelegraph slam, int seq, Vector3d centre, double yaw, long now)
    {
        var replies = new List<byte[]>();
        var skill = data?.NpcSkill(slam.Skill) ?? Basic(target)?.Skill ?? new NpcSkill(0, 0, slam.EveryMs, slam.WindUpMs, 0, 10000, 10000);
        replies.Add(GamePackets.SkillStart(slam.Skill, seq, yaw, centre, targetKey: Victim(brain), actorKey: target.Key, cooldown: slam.EveryMs));
        brain.Volley.Add(new SummonShot(now + MonsterEndAfterLastHitMs, ShotKind.End, slam.Skill, seq, Victim(brain), 0));
        if (brain.Fight is { } fight) fight.Slams++;
        SlamsLanded++;
        if (currentHitPlayer is not { } hitPlayer) return replies;
        var max = SkillAreas.For(slam.Skill) is { IsArea: true } area ? area.Max : int.MaxValue;
        foreach (var (key, _) in currentPlayers.Where(p => InSlam(target, slam, centre, yaw, p.Position))
                     .OrderBy(p => p.Position.DistanceSquared(centre)).ThenBy(p => p.Key).Take(max).ToList())
        {
            var raw = Damage(target, skill, PlayerLevelOf?.Invoke(key) ?? 1) * slam.Damage;
            var damage = Math.Max(1, (int)Math.Round(raw * 1000.0 / (1000 + Math.Max(0, DefenseOf?.Invoke(key) ?? 0))));
            LastAttacker = target.Key;
            LastHitSkill = slam.Skill;
            replies.Add(GamePackets.NpcSkillHit(key, target.Key, slam.Skill, seq, skill.Effect, damage));
            replies.AddRange(hitPlayer(key, damage));
        }
        return replies;
    }

    /// <summary>A hold or stun breaks the slam's wind-up; the boss tries again <see cref="SlamRetryMs"/> later.</summary>
    static void BreakSlam(Brain brain, long now)
    {
        if (brain.Fight is not { Winding: not null } fight) return;
        fight.Winding = null;
        fight.NextSlamMs = now + SlamRetryMs;
    }

    /// <summary>A boss's fight ends (a wipe sent it home, or it died): its living adds vanish (0x3642 flag 7) and the phases start over.</summary>
    List<byte[]> EndFight(Brain brain)
    {
        if (brain.Fight is not { } fight) return [];
        brain.Fight = null;
        var replies = new List<byte[]>();
        foreach (var key in fight.Adds)
        {
            if (current?.GetValueOrDefault(key) is not { } add || add.Dead && add.CorpseRemoved) continue;
            add.Hp = 0;
            add.CorpseRemoved = true;
            if (brains.TryGetValue(key, out var mind))
            {
                mind.Mode = Mode.Dead; mind.ClearThreat(); mind.VictimKey = 0; mind.Pending = null; mind.Volley.Clear();
                if (mind.Engaged) { mind.Engaged = false; replies.Add(GamePackets.CombatState(key, false)); }
            }
            replies.Add(GamePackets.Death(key, 7));
        }
        return replies;
    }
}
