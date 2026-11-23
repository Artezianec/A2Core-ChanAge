using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// The boss mechanics engine (docs/BOSS-MECHANICS-ENGINE.md): runs an encounter's rules (triggers to primitive actions) on top of
/// the boss slam pipeline of NpcAi.Bosses. Casts go through that pipeline (wind-up, ground marker, stagger break, landing); every
/// other primitive is evaluated here each AI tick. Damage uses the NPC damage formula (<see cref="Damage"/>) and reaches players
/// through the tick's hit callback, like every other NPC hit. Off with Aion:Bosses:Engine=false (the legacy hand-coded path).
/// </summary>
public sealed partial class NpcAi
{
    /// <summary>Authored encounters by NpcData id (tools/data/boss-mechanics); bosses without one run their compiled boss-phases.json script.</summary>
    public BossEncounters Encounters { get; set; } = BossEncounters.Embedded;

    /// <summary>Damage that kills outright (wipe, lethal telegraphs); the host clamps it to the player's HP.</summary>
    public const int LethalDamage = 99_999_999;

    /// <summary>Optional visual for engine debuff stacks: (player key, client abnormal id, stacks, duration ms) to packets. GameSession wires it to the skill-effect engine (EngineApplyAbnormal) for abnormals that exist in the ported tables.</summary>
    public Func<int, int, int, long, IReadOnlyList<byte[]>>? MechanicDebuffHook { get; set; }

    readonly HashSet<int> activeRuns = [];
    readonly List<string> waveStarts = [];

    /// <summary>The engine run of this boss's fight, or null (no fight, or a legacy fight).</summary>
    public EncounterRun? EncounterOf(Target target) => brains.TryGetValue(target.Key, out var brain) ? brain.Fight?.Encounter : null;

    /// <summary>The key of the player this NPC is fighting, or 0.</summary>
    public int VictimOf(Target target) => brains.TryGetValue(target.Key, out var brain) ? brain.VictimKey : 0;

    /// <summary>Map waves the engine's "summon" actions started since the last take (the host runs StartWave for each).</summary>
    public List<string> TakeWaveStarts()
    {
        var taken = waveStarts.ToList();
        waveStarts.Clear();
        return taken;
    }

    /// <summary>Entity key of an engine-spawned env object (pillars, orbs, walls): clear of map NPCs, summons and Kibelisks.</summary>
    public static int MechanicEnvKey(int bossKey, int slot) => GameSession.MechanicEnvObjKeyBase + 50_000 + bossKey % 3_000 * 16 + slot % 16;

    sealed record MechanicCtx(Target Boss, Brain Brain, BossFight Fight, EncounterRun Run, long Now, string Rule)
    {
        public Vector3d? Origin { get; init; }
        public int? Player { get; init; }
        public Vector3d? CastCentre { get; init; }
        public long? CastDueMs { get; init; }
        public int Iteration { get; init; }
    }

    readonly record struct MPoint(Vector3d At, double Yaw, int? Player);

    readonly record struct ShapeSpec(string Shape, double Radius, double Inner, double Angle, double Length, double Width, bool Invert, int Skill);

    // ---- fight lifecycle ------------------------------------------------------------------------------------------------

    /// <summary>A new engine fight for this boss, or null when it has no encounter (then the legacy script, if any, runs).</summary>
    BossFight? StartEncounter(Target target, Brain brain, long now)
    {
        if (Encounters.Resolve(target.NpcId, Bosses) is not { } encounter) return null;
        BossFight fight;
        if (encounter.Compiled && encounter.Script is { } compiled)
            fight = new BossFight(compiled, now) { NextSlamMs = now + (compiled.Telegraph?.EveryMs ?? 0) / 2 };
        else
        {
            var rotation = encounter.Rotation.Select(a => TelegraphOf(encounter, a, "rotation")).ToArray();
            var script = new BossScript(encounter.Def.Display ?? encounter.Id, encounter.Dungeon, "encounter", Telegraphs: rotation,
                Display: encounter.Def.Display, DisplayRu: encounter.Def.DisplayRu, Source: encounter.Def.Source);
            var opening = encounter.Def.OpeningMs is { } o ? encounter.Ms(o) : (rotation.FirstOrDefault()?.EveryMs ?? 0) / 2;
            fight = new BossFight(script, now) { NextSlamMs = now + opening, SlamSkills = CastSkills(encounter) };
        }
        var run = new EncounterRun(encounter, target.Key, now);
        run.ArenaCentre = encounter.Def.Arena?.Centre is { } centre
            ? centre.Base == "boss" ? target.Position : centre.Base == "point" ? new Vector3d(centre.X, centre.Y, centre.Z == 0 ? target.Position.Z : centre.Z) : brain.Home
            : brain.Home;
        fight.Encounter = run;
        activeRuns.Add(target.Key);
        run.Record(now, "fight", "start", currentPlayers.Count, $"{encounter.Id} ({encounter.Difficulty}{(encounter.Compiled ? ", compiled" : "")})");
        PlaytestDebug.Action("boss_mechanic", ("boss", target.NpcId), ("boss_key", target.Key), ("encounter", encounter.Id),
            ("difficulty", encounter.Difficulty), ("rule", "fight"), ("action", "start"), ("targets", currentPlayers.Count), ("result", encounter.Compiled ? "compiled" : "authored"));
        return fight;
    }

    /// <summary>Every skill an authored encounter casts through the slam pipeline: the boss never uses them as ordinary specials.</summary>
    static HashSet<int> CastSkills(ResolvedEncounter encounter)
    {
        var skills = new HashSet<int>();
        void Walk(IEnumerable<ActionDef>? actions)
        {
            foreach (var a in actions ?? [])
            {
                if (a.Type == "cast" && a.Skill is { } s) skills.Add(encounter.Skill(s));
                foreach (var list in new[] { a.During, a.OnLand, a.OnBreak, a.OnHit, a.OnSuccess, a.OnFail, a.OnExpire, a.OnMax, a.OnComplete, a.OnTouch, a.Do, a.Rotation })
                    Walk(list);
                foreach (var c in a.Choices ?? []) Walk(c.Do);
            }
        }
        Walk(encounter.Rotation);
        foreach (var rule in encounter.Rules) Walk(rule.Do);
        return skills;
    }

    /// <summary>A cast action as a slam for the pipeline (a compiled boss-phases.json slam is reused unchanged).</summary>
    static BossTelegraph TelegraphOf(ResolvedEncounter encounter, ActionDef a, string rule)
    {
        if (a.Compiled is { } compiled) return compiled;
        var once = a.Once ?? (rule != "rotation" && a.EveryMs is null);
        return new BossTelegraph(encounter.Skill(a.Skill ?? 0), (int)encounter.Ms(a.EveryMs, 999_999), (int)encounter.Ms(a.WindUpMs, SlamMinWindUpMs),
            encounter.N(a.Radius, 0), encounter.N(a.Damage?.Scale, 1.0) * encounter.DamageScale, a.Warning, a.WarningRu,
            Once: once, StaggerCheck: a.StaggerCheck ?? false, Land: a.Land ?? true) { Tag = new EngineCast(a, rule) };
    }

    /// <summary>The fight ended (wipe or death): engine env objects go, an invulnerability shield is lifted.</summary>
    List<byte[]> EndEncounter(EncounterRun run)
    {
        activeRuns.Remove(run.BossKey);
        var replies = new List<byte[]>();
        if (current?.GetValueOrDefault(run.BossKey) is { } boss)
        {
            foreach (var shield in run.Shields.Values.Where(s => s.Invuln)) boss.Immune = shield.WasImmune;
            foreach (var slot in spawnedEnv.GetValueOrDefault(run.BossKey) ?? [])
                replies.AddRange(RaiseTelegraph(new BossMechanicTelegraphEventArgs(boss.NpcId, boss.Key, "env", 0, boss.Position, 0, 0, [0], Despawn: true, EnvSlot: slot)));
        }
        spawnedEnv.Remove(run.BossKey);
        return replies;
    }

    readonly Dictionary<int, HashSet<int>> spawnedEnv = [];

    // ---- per tick -------------------------------------------------------------------------------------------------------

    /// <summary>Everything but the slam pipeline: triggers, events, delayed work, zones, shields, orbs, stagger windows, parts, debuffs.</summary>
    List<byte[]> EncounterWorldTick(Target boss, Brain brain, BossFight fight, EncounterRun run, long now)
    {
        var ctx = new MechanicCtx(boss, brain, fight, run, now, "tick");
        var replies = new List<byte[]>();
        replies.AddRange(EvaluateTriggers(ctx));
        replies.AddRange(DrainEvents(ctx));
        replies.AddRange(RunPending(run, now));
        replies.AddRange(TickZones(ctx));
        replies.AddRange(TickShields(ctx));
        replies.AddRange(TickOrbs(ctx));
        replies.AddRange(TickStaggerWatches(ctx));
        TickParts(ctx);
        TickDebuffs(ctx);
        run.Covers.RemoveAll(c => now >= c.EndsMs);
        replies.AddRange(DrainEvents(ctx));
        run.LastHp = boss.Hp;
        return replies;
    }

    static bool OnceByDefault(string trigger) => trigger is "hp_below" or "enrage_timer" or "npc_killed" or "fight_start";

    bool CanFire(RuleDef rule, EncounterRun run)
    {
        var fires = run.FireCount(rule.Id);
        if ((rule.Once ?? OnceByDefault(rule.When.Type)) && fires > 0) return false;
        if (rule.MaxFires is { } max && fires >= max) return false;
        if (rule.Phase is { Count: > 0 } phases && (run.Phase is null || !phases.Contains(run.Phase))) return false;
        if (rule.NpcAlive is { } alive && current?.Values.Any(t => t.NpcId == alive && !t.Dead && !t.CorpseRemoved) != true) return false;
        if (rule.Chance is { } chance && rng.NextDouble() >= run.N(chance, 1)) return false;
        return true;
    }

    List<byte[]> Fire(RuleDef rule, MechanicCtx ctx, Vector3d? origin = null, int? player = null)
    {
        var run = ctx.Run;
        run.Fires[rule.Id] = run.FireCount(rule.Id) + 1;
        if (rule.When.Type is "hp_below") ctx.Fight.Reached++;
        run.Record(ctx.Now, rule.Id, "rule:" + rule.When.Type, currentPlayers.Count, "fired");
        PlaytestDebug.Action("boss_mechanic", ("boss", ctx.Boss.NpcId), ("boss_key", ctx.Boss.Key), ("encounter", run.Id), ("difficulty", run.Difficulty),
            ("rule", rule.Id), ("action", "rule:" + rule.When.Type), ("targets", currentPlayers.Count), ("result", "fired"));
        return Exec(rule.Do, ctx with { Rule = rule.Id, Origin = origin, Player = player });
    }

    List<byte[]> EvaluateTriggers(MechanicCtx ctx)
    {
        var (boss, run, now) = (ctx.Boss, ctx.Run, ctx.Now);
        var rules = run.Encounter.Rules;
        var replies = new List<byte[]>();
        if (!run.Started)
        {
            run.Started = true;
            run.PhaseStartedMs = now;
            run.LastHp = boss.Hp;
            if (run.Encounter.Def.StartPhase is { } start) replies.AddRange(SetPhase(ctx, start));
            foreach (var rule in rules.Where(r => r.When.Type == "fight_start" && CanFire(r, run))) replies.AddRange(Fire(rule, ctx));
        }
        var pct = boss.HpMax > 0 ? boss.Hp * 100.0 / boss.HpMax : 100;
        foreach (var rule in rules.Where(r => r.When.Type == "hp_below").OrderByDescending(r => run.N(r.When.Pct)))
            if (pct <= run.N(rule.When.Pct) && CanFire(rule, run)) replies.AddRange(Fire(rule, ctx));
        foreach (var rule in rules.Where(r => r.When.Type == "hp_every"))
        {
            var step = Math.Max(0.1, run.N(rule.When.Step, 10));
            var reached = (int)Math.Floor((run.N(rule.When.Pct, 100) - pct) / step);
            var done = run.HpSteps.GetValueOrDefault(rule.Id);
            while (done < reached)
            {
                run.HpSteps[rule.Id] = ++done;
                if (CanFire(rule, run)) replies.AddRange(Fire(rule, ctx));
            }
        }
        foreach (var rule in rules.Where(r => r.When.Type == "timer"))
        {
            var t = rule.When;
            var generation = t.From == "phase" ? run.PhaseGeneration : 0;
            if (!run.TimerGeneration.TryGetValue(rule.Id, out var seen) || seen != generation)
            {
                run.TimerGeneration[rule.Id] = generation;
                run.TimerCount[rule.Id] = 0;
                var from = t.From == "phase" ? run.PhaseStartedMs : run.StartedMs;
                run.NextTimerMs[rule.Id] = from + (t.AfterMs is not null ? run.Ms(t.AfterMs) : run.Ms(t.EveryMs));
            }
            if (now < run.NextTimerMs[rule.Id]) continue;
            var count = run.TimerCount[rule.Id] = run.TimerCount.GetValueOrDefault(rule.Id) + 1;
            run.NextTimerMs[rule.Id] = t.EveryMs is not null && (t.Count is null || count < t.Count)
                ? Math.Max(run.NextTimerMs[rule.Id] + run.Ms(t.EveryMs), now + 1) : long.MaxValue;
            if (CanFire(rule, run)) replies.AddRange(Fire(rule, ctx));
        }
        foreach (var rule in rules.Where(r => r.When.Type == "enrage_timer"))
        {
            if (!BossRules.Frenzy || OfficialFrenzy(run.Encounter.Dungeon)) continue;
            var after = BossRules.FrenzySeconds > 0 ? BossRules.FrenzySeconds * 1000L : run.Ms(rule.When.AfterMs);
            if (now - run.StartedMs >= after && CanFire(rule, run)) replies.AddRange(Fire(rule, ctx));
        }
        foreach (var rule in rules.Where(r => r.When.Type == "adds_dead"))
        {
            var members = ctx.Fight.Groups.GetValueOrDefault(rule.When.Group!) ?? [];
            if (members.Count == 0 || members.Any(Alive) || run.GroupDeadAt.GetValueOrDefault(rule.Id) == members.Count) continue;
            run.GroupDeadAt[rule.Id] = members.Count;
            if (CanFire(rule, run)) replies.AddRange(Fire(rule, ctx));
        }
        foreach (var (group, members) in ctx.Fight.Groups)
            foreach (var key in members.Where(k => !Alive(k) && run.DeadSeen.Add(k)))
                run.Events.Enqueue(("add_died", group, current?.GetValueOrDefault(key)?.Position, null, 1, 0));
        foreach (var rule in rules.Where(r => r.When.Type == "npc_killed"))
            if (current?.Values.Any(t => t.NpcId == rule.When.Npc && t.Dead) == true && CanFire(rule, run)) replies.AddRange(Fire(rule, ctx));
        foreach (var rule in rules.Where(r => r.When.Type == "players_in_area"))
        {
            var t = rule.When;
            var shape = ShapeOf(t.Area!, null, run.Encounter, 0);
            var centres = Points(t.At, ctx);
            var count = currentPlayers.Count(p => InsideAny(shape, centres.Select(c => (c.At, c.Yaw)), p.Position));
            var met = count >= (t.Min ?? 0) && count <= (t.Max ?? int.MaxValue);
            if (!met) { run.AreaWasMet[rule.Id] = false; run.AreaMetSinceMs.Remove(rule.Id); continue; }
            if (!run.AreaMetSinceMs.ContainsKey(rule.Id)) run.AreaMetSinceMs[rule.Id] = now;
            if (run.AreaWasMet.GetValueOrDefault(rule.Id) || now - run.AreaMetSinceMs[rule.Id] < run.Ms(t.HoldMs)) continue;
            run.AreaWasMet[rule.Id] = true;
            if (CanFire(rule, run)) replies.AddRange(Fire(rule, ctx));
        }
        var staggered = Staggered(boss, now);
        if (staggered && !run.WasStaggered)
            foreach (var rule in rules.Where(r => r.When.Type == "stagger_broken" && CanFire(r, run))) replies.AddRange(Fire(rule, ctx));
        run.WasStaggered = staggered;
        return replies;
    }

    static bool Staggered(Target boss, long now) => boss.Held || boss.StaggerUntil > now;

    bool Alive(int key) => current?.GetValueOrDefault(key) is { Dead: false, CorpseRemoved: false };

    List<byte[]> DrainEvents(MechanicCtx ctx)
    {
        var run = ctx.Run;
        var replies = new List<byte[]>();
        for (var guard = 0; run.Events.Count > 0 && guard < 256; guard++)
        {
            var e = run.Events.Dequeue();
            foreach (var rule in run.Encounter.Rules.Where(r => Matches(r.When, e, run)).ToList())
                if (CanFire(rule, run)) replies.AddRange(Fire(rule, ctx, e.At, e.Player));
        }
        return replies;
    }

    static bool Matches(TriggerDef t, (string Kind, string Key, Vector3d? At, int? Player, double Value, double Old) e, EncounterRun run) => (t.Type, e.Kind) switch
    {
        ("event", "event") => t.Event == e.Key,
        ("shield_broken", "shield_broken") => t.Shield == e.Key,
        ("part_broken", "part_broken") => t.Part == e.Key,
        ("phase_enter", "phase_enter") or ("phase_exit", "phase_exit") => t.Phase == e.Key,
        ("add_died", "add_died") => t.Group == e.Key,
        ("stacks_reached", "stacks") => t.Debuff == e.Key && e.Old < t.Stacks && e.Value >= t.Stacks,
        ("counter_reached", "counter") => t.Counter == e.Key && e.Old < run.N(t.Value) && e.Value >= run.N(t.Value),
        _ => false,
    };

    void Schedule(EncounterRun run, long due, Func<long, List<byte[]>> work) => run.Pending.Add((due, run.PendingSeq++, work));

    static List<byte[]> RunPending(EncounterRun run, long now)
    {
        var replies = new List<byte[]>();
        for (var guard = 0; guard < 512; guard++)
        {
            var index = -1;
            for (var i = 0; i < run.Pending.Count; i++)
                if (run.Pending[i].DueMs <= now && (index < 0 || (run.Pending[i].DueMs, run.Pending[i].Seq).CompareTo((run.Pending[index].DueMs, run.Pending[index].Seq)) < 0))
                    index = i;
            if (index < 0) break;
            var work = run.Pending[index].Run;
            run.Pending.RemoveAt(index);
            replies.AddRange(work(now));
        }
        return replies;
    }

    void Emit(MechanicCtx ctx, ActionDef a, bool success, Vector3d? at = null, int? player = null)
    {
        if (a.Id is { } id) ctx.Run.Events.Enqueue(("event", id + (success ? ".success" : ".fail"), at, player, 1, 0));
    }

    void Note(MechanicCtx ctx, ActionDef a, int targets, string result)
    {
        ctx.Run.Record(ctx.Now, ctx.Rule, a.Id ?? a.Type, targets, result);
        PlaytestDebug.Action("boss_mechanic", ("boss", ctx.Boss.NpcId), ("boss_key", ctx.Boss.Key), ("encounter", ctx.Run.Id), ("difficulty", ctx.Run.Difficulty),
            ("rule", ctx.Rule), ("action", a.Type), ("id", a.Id), ("targets", targets), ("result", result));
    }

    // ---- geometry -------------------------------------------------------------------------------------------------------

    /// <summary>The points an anchor names now (with a facing and, for players, their key), moved by toward/distance and offsets.</summary>
    List<MPoint> Points(AnchorDef? anchor, MechanicCtx ctx, ActionDef? placement = null)
    {
        var (boss, brain, run) = (ctx.Boss, ctx.Brain, ctx.Run);
        var players = currentPlayers;
        var victimAt = VictimPosition(brain);
        double Facing(Vector3d from) => victimAt is { } v && Distance(from, Flat(v)) > 1 ? HeadingTo(from, Flat(v)) : brain.Heading;
        double FromBoss(Vector3d to) => Distance(boss.Position, Flat(to)) > 1 ? HeadingTo(boss.Position, Flat(to)) : brain.Heading;
        var list = new List<MPoint>();
        anchor ??= AnchorDef.Boss;
        switch (anchor.Base)
        {
            case "boss": list.Add(new(boss.Position, Facing(boss.Position), null)); break;
            case "victim":
                var victim = Victim(brain);
                foreach (var p in players.Where(p => p.Key == victim)) list.Add(new(p.Position, FromBoss(p.Position), p.Key));
                break;
            case "each_player": list.AddRange(players.Select(p => new MPoint(p.Position, FromBoss(p.Position), p.Key))); break;
            case "random_player":
                if (players.Count > 0) { var p = players[rng.Next(players.Count)]; list.Add(new(p.Position, FromBoss(p.Position), p.Key)); }
                break;
            case "farthest_player" or "nearest_player":
                var ordered = players.OrderBy(p => Distance(boss.Position, Flat(p.Position))).ThenBy(p => p.Key).ToList();
                if (ordered.Count > 0) { var p = anchor.Base == "nearest_player" ? ordered[0] : ordered[^1]; list.Add(new(p.Position, FromBoss(p.Position), p.Key)); }
                break;
            case "arena_centre": list.Add(new(run.ArenaCentre, Facing(run.ArenaCentre), null)); break;
            case "home": list.Add(new(brain.Home, brain.HomeHeading, null)); break;
            case "cast_centre": var c = ctx.CastCentre ?? boss.Position; list.Add(new(c, Facing(c), null)); break;
            case "event": var o = ctx.Origin ?? boss.Position; list.Add(new(o, Facing(o), ctx.Player)); break;
            case "event_player":
                foreach (var p in players.Where(p => p.Key == ctx.Player)) list.Add(new(p.Position, FromBoss(p.Position), p.Key));
                break;
            case "mark":
                if (run.Mark(anchor.Arg!) is { } marked)
                    foreach (var p in players.Where(p => p.Key == marked)) list.Add(new(p.Position, FromBoss(p.Position), p.Key));
                break;
            case "group":
                foreach (var key in ctx.Fight.Groups.GetValueOrDefault(anchor.Arg!) ?? [])
                    if (current?.GetValueOrDefault(key) is { Dead: false, CorpseRemoved: false } add) list.Add(new(add.Position, add.Direction, null));
                break;
            case "point":
                var at = new Vector3d(anchor.X, anchor.Y, anchor.Z == 0 ? boss.Position.Z : anchor.Z);
                list.Add(new(at, Facing(at), null));
                break;
        }
        if (placement is null) return list;
        if (placement.Toward is { } toward)
        {
            var targets = Points(toward, ctx);
            var moved = new List<MPoint>();
            var distance = placement.Distance is { } d ? run.N(d) : (double?)null;
            foreach (var p in list)
                foreach (var q in targets)
                {
                    var yaw = Distance(p.At, Flat(q.At)) > 1 ? HeadingTo(p.At, Flat(q.At)) : p.Yaw;
                    var r = yaw * Math.PI / 180;
                    moved.Add(distance is { } dist ? new(new Vector3d(p.At.X + Math.Cos(r) * dist, p.At.Y + Math.Sin(r) * dist, p.At.Z), yaw, p.Player) : p with { Yaw = yaw });
                }
            list = moved;
        }
        else if (placement.Distance is { } forward && placement.Type != "displace")
        {
            var dist = run.N(forward);
            list = list.Select(p => p with { At = new Vector3d(p.At.X + Math.Cos(p.Yaw * Math.PI / 180) * dist, p.At.Y + Math.Sin(p.Yaw * Math.PI / 180) * dist, p.At.Z) }).ToList();
        }
        if (placement.Offsets is { Count: > 0 } offsets)
        {
            var facing = placement.OffsetSpace == "facing";
            list = list.SelectMany(p => offsets.Select(o =>
            {
                double a = run.N(o[0]), b = run.N(o[1]);
                if (!facing) return p with { At = new Vector3d(p.At.X + a, p.At.Y + b, p.At.Z) };
                var r = p.Yaw * Math.PI / 180;
                return p with { At = new Vector3d(p.At.X + Math.Cos(r) * a + Math.Sin(r) * b, p.At.Y + Math.Sin(r) * a - Math.Cos(r) * b, p.At.Z) };
            })).ToList();
        }
        return list;
    }

    static ShapeSpec ShapeOf(ActionDef a, ResolvedEncounter enc, int iteration)
    {
        var step = a.Steps is { Count: > 0 } steps ? steps[Math.Min(iteration, steps.Count - 1)] : null;
        var skill = enc.Skill(a.Skill ?? 0);
        var shape = step?.Shape ?? a.Shape ?? (skill > 0 ? "skill_area" : "circle");
        return new ShapeSpec(shape, enc.N(step?.Radius ?? a.Radius), enc.N(step?.Inner ?? a.Inner), enc.N(step?.Angle ?? a.Angle, 90),
            enc.N(step?.Length ?? a.Length), enc.N(step?.Width ?? a.Width), (step?.Invert ?? a.Invert ?? false) || shape == "safe_zone", skill);
    }

    static ShapeSpec ShapeOf(ShapeDef s, int? skill, ResolvedEncounter enc, int _) =>
        new(s.Shape ?? "circle", enc.N(s.Radius), enc.N(s.Inner), enc.N(s.Angle, 90), enc.N(s.Length), enc.N(s.Width), (s.Invert ?? false) || s.Shape == "safe_zone", skill ?? 0);

    /// <summary>Whether a player at <paramref name="at"/> is inside the shape placed at <paramref name="centre"/> facing <paramref name="yaw"/> (flat distance to the player's centre).</summary>
    static bool Inside(ShapeSpec s, Vector3d centre, double yaw, Vector3d at)
    {
        var d = Distance(centre, Flat(at));
        double Forward() { var r = yaw * Math.PI / 180; return (at.X - centre.X) * Math.Cos(r) + (at.Y - centre.Y) * Math.Sin(r); }
        double Side() { var r = yaw * Math.PI / 180; return -(at.X - centre.X) * Math.Sin(r) + (at.Y - centre.Y) * Math.Cos(r); }
        var inside = s.Shape switch
        {
            "circle" or "safe_zone" => d <= s.Radius,
            "donut" => d >= s.Inner && d <= s.Radius,
            "cone" => d <= s.Radius && (d < 1 || AngleDelta(HeadingTo(centre, Flat(at)), yaw) <= s.Angle / 2),
            "rect" => Forward() is var f && f >= 0 && f <= s.Length && Math.Abs(Side()) <= s.Width / 2,
            "line" => Math.Abs(Forward()) <= s.Length / 2 && Math.Abs(Side()) <= s.Width / 2,
            "skill_area" => SkillAreas.For(s.Skill) is { IsArea: true } area ? area.Contains(centre, yaw, at, PlayerRadius) : d <= s.Radius,
            _ => false,
        };
        return inside != s.Invert;
    }

    /// <summary>
    /// Inside any of the placed copies of a shape. An inverted shape placed at several points (offsets) is a set of safe spots:
    /// it hits only a player outside every copy (rocks, coloured shields), not one outside any single copy.
    /// </summary>
    static bool InsideAny(ShapeSpec s, IEnumerable<(Vector3d At, double Yaw)> centres, Vector3d at)
    {
        var list = centres as IList<(Vector3d At, double Yaw)> ?? centres.ToList();
        return s.Invert && list.Count > 1 ? list.All(c => Inside(s, c.At, c.Yaw, at)) : list.Any(c => Inside(s, c.At, c.Yaw, at));
    }

    static bool InCover(Vector3d boss, MechanicCover cover, Vector3d player)
    {
        double toBossX = boss.X - cover.At.X, toBossY = boss.Y - cover.At.Y, toPlayerX = player.X - cover.At.X, toPlayerY = player.Y - cover.At.Y;
        if (toPlayerX * toBossX + toPlayerY * toBossY >= 0) return false;
        var perp = Math.Abs(toPlayerX * toBossY - toPlayerY * toBossX) / Math.Max(1, Math.Sqrt(toBossX * toBossX + toBossY * toBossY));
        return perp <= cover.HalfWidth && Math.Sqrt(toPlayerX * toPlayerX + toPlayerY * toPlayerY) <= cover.Depth;
    }

    /// <summary>Whether a cast marked "cover": true misses this player: behind an active cover object, seen from the boss.</summary>
    internal bool Covered(Target boss, EncounterRun run, Vector3d player, long now) =>
        run.Covers.Any(c => now < c.EndsMs && InCover(boss.Position, c, player));

    // ---- damage ---------------------------------------------------------------------------------------------------------

    /// <summary>One roll of an action's damage on a player (0: the action deals none).</summary>
    int Roll(DamageDef? d, MechanicCtx ctx, int playerKey)
    {
        if (d is null) return 0;
        if (d.Lethal) return LethalDamage;
        var run = ctx.Run;
        if (d.HpRatio is { } share && SkillEffectEngine.NpcRatioDamage && PlayerHpOf?.Invoke(playerKey) is { HpMax: > 0 } hp)
            return (int)Math.Clamp(hp.HpMax * run.N(share) / 10_000 * run.N(d.Scale, 1) * run.Encounter.DamageScale, 1, LethalDamage);
        double raw;
        var defense = !d.IgnoreDefense;
        if (d.Fixed is { Length: 2 } f)
        {
            int lo = (int)run.N(f[0]), hi = (int)run.N(f[1]);
            raw = rng.Next(Math.Min(lo, hi), Math.Max(lo, hi) + 1);
            defense = false;
        }
        else
        {
            if (d.Ratio == true && d.Skill is { } ratioId && RatioDamage(run.Encounter.Skill(ratioId), playerKey, victimIsNpc: false) is { } ratio)
                return Math.Max(1, (int)Math.Round(ratio * run.Encounter.DamageScale * run.N(d.Scale, 1)));
            var skill = d.Skill is { } id ? data?.NpcSkill(run.Encounter.Skill(id)) : null;
            if (skill is null && d.Factor is { Length: 2 } fa) skill = new NpcSkill(0, 0, 1, 1, 0, (int)run.N(fa[0]), (int)run.N(fa[1]));
            if (skill is null) return 0;
            raw = Damage(ctx.Boss, skill, PlayerLevelOf?.Invoke(playerKey) ?? 1);
        }
        raw *= run.N(d.Scale, 1) * run.Encounter.DamageScale;
        if (defense) raw = raw * 1000.0 / (1000 + Math.Max(0, DefenseOf?.Invoke(playerKey) ?? 0));
        return Math.Max(1, (int)Math.Round(raw));
    }

    List<byte[]> Hit(MechanicCtx ctx, int key, int damage, int skill, int seq, bool skillPacket)
    {
        if (damage <= 0 || currentHitPlayer is not { } hit) return [];
        LastAttacker = ctx.Boss.Key;
        LastHitSkill = skill;
        LastHitSeq = 1;
        LastAttackerAt = ctx.Boss.Position;
        var replies = new List<byte[]>();
        if (skillPacket && skill > 0) replies.Add(GamePackets.NpcSkillHit(key, ctx.Boss.Key, skill, seq, data?.NpcSkill(skill)?.Effect ?? 0, Math.Min(damage, LethalDamage)));
        replies.AddRange(hit(key, damage));
        return replies;
    }

    int NextSeq(Brain brain) => brain.ActionSeq = brain.ActionSeq % 255 + 1;

    /// <summary>A ground marker of the skill's client shape at an exact point (not re-centred on the caster).</summary>
    List<byte[]> Decal(Target boss, int skill, int seq, Vector3d centre, double yaw, long durationMs, long now)
    {
        if (skill <= 0 || durationMs <= 0 || !MechanicTelegraphs.WantsMarker(SkillAreas.For(skill))) return [];
        return MechanicTelegraphs.Place(GameSession.CombatTelegraphRules,
            new MechanicTelegraphs.Started(boss, skill, (byte)seq, 1, centre, yaw, (int)Math.Min(durationMs, 120_000), now), MechanicTelegraphStarted);
    }

    List<byte[]> Env(MechanicCtx ctx, int envObj, Vector3d at, int slot, bool despawn, string id)
    {
        var set = spawnedEnv.TryGetValue(ctx.Boss.Key, out var s) ? s : spawnedEnv[ctx.Boss.Key] = [];
        if (despawn) { if (!set.Remove(slot)) return []; }
        else set.Add(slot);
        return RaiseTelegraph(new BossMechanicTelegraphEventArgs(ctx.Boss.NpcId, ctx.Boss.Key, id, 0, at, ctx.Now, ctx.Now, [envObj],
            SpawnEnv: !despawn, Despawn: despawn, EnvSlot: slot));
    }

    // ---- actions --------------------------------------------------------------------------------------------------------

    List<byte[]> Exec(IReadOnlyList<ActionDef>? actions, MechanicCtx ctx)
    {
        var replies = new List<byte[]>();
        foreach (var a in actions ?? []) replies.AddRange(ExecOne(a, ctx));
        return replies;
    }

    List<byte[]> ExecOne(ActionDef a, MechanicCtx ctx)
    {
        try
        {
            return a.Type switch
            {
                "cast" => CastAction(a, ctx),
                "telegraph" => TelegraphAction(a, ctx),
                "zone" => ZoneAction(a, ctx),
                "soak" or "stack" => SoakAction(a, ctx),
                "spread" => SpreadAction(a, ctx),
                "cover" => CoverAction(a, ctx),
                "displace" => DisplaceAction(a, ctx),
                "summon" => SummonAction(a, ctx),
                "despawn_group" => DespawnGroupAction(a, ctx),
                "shield" => ShieldAction(a, ctx),
                "stagger_check" => StaggerAction(a, ctx),
                "parts" => PartsAction(a, ctx),
                "debuff" => DebuffAction(a, ctx),
                "cleanse" => CleanseAction(a, ctx),
                "tank_swap" => TankSwapAction(a, ctx),
                "orb" => OrbAction(a, ctx),
                "move" => MoveAction(a, ctx),
                "enrage" => EnrageAction(a, ctx),
                "wipe" => WipeAction(a, ctx),
                "phase" => SetPhase(ctx, a.Phase!),
                "notice" => NoticeAction(a, ctx),
                "caption" => CaptionAction(a, ctx),
                "env" => EnvAction(a, ctx),
                "decal" => DecalAction(a, ctx),
                "modify" => ModifyAction(a, ctx),
                "set_rotation" => SetRotationAction(a, ctx),
                "counter" => CounterAction(a, ctx),
                "delay" => DelayAction(a, ctx),
                "repeat" => RepeatAction(a, ctx),
                "random" => RandomAction(a, ctx),
                "mark" => MarkAction(a, ctx),
                "emit" => EmitAction(a, ctx),
                _ => Unknown(a, ctx),
            };
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or KeyNotFoundException or IndexOutOfRangeException or NullReferenceException)
        {
            Note(ctx, a, 0, "error: " + e.Message);
            return [];
        }
    }

    List<byte[]> Unknown(ActionDef a, MechanicCtx ctx)
    {
        Note(ctx, a, 0, "error: unknown action");
        return [];
    }

    /// <summary>
    /// cast: the boss casts a real skill through the slam pipeline. A once-only cast (the default from a rule) goes next; a repeating
    /// one (every_ms, once false) replaces the filler. A filler being wound up is broken for it; a once-only one being wound up keeps
    /// going and this cast waits behind it.
    /// </summary>
    List<byte[]> CastAction(ActionDef a, MechanicCtx ctx)
    {
        var (fight, now) = (ctx.Fight, ctx.Now);
        var slam = TelegraphOf(ctx.Run.Encounter, a, ctx.Rule);
        var replies = new List<byte[]>();
        var urgent = slam.Once || slam.StaggerCheck;
        if (fight.Winding is { } winding)
        {
            if (winding.Slam.Once || winding.Slam.StaggerCheck)
            {
                ctx.Run.CastQueue.Enqueue(slam);
                Note(ctx, a, 0, "queued");
                return replies;
            }
            replies.AddRange(BreakSlam(ctx.Boss, ctx.Brain, now));
        }
        if (urgent && fight.Telegraph is { } pending && (pending.Once || pending.StaggerCheck) && !ReferenceEquals(pending, fight.Filler) && fight.NextSlamMs <= now)
        {
            ctx.Run.CastQueue.Enqueue(slam);
            Note(ctx, a, 0, "queued");
            return replies;
        }
        fight.Filler ??= fight.Telegraph;
        fight.Telegraph = slam;
        fight.NextSlamMs = urgent ? now : Math.Min(fight.NextSlamMs, now + slam.EveryMs / 2);
        Note(ctx, a, 0, urgent ? "next" : "rotation");
        return replies;
    }

    /// <summary>A slam of the engine started winding up: its "during" actions (rings, edge snaps, walls) start with it.</summary>
    List<byte[]> EngineCastStarted(Target boss, Brain brain, BossFight fight, EngineCast cast, Vector3d centre, long dueMs, long now)
    {
        if (fight.Encounter is not { } run) return [];
        var ctx = new MechanicCtx(boss, brain, fight, run, now, cast.Rule) { CastCentre = centre, CastDueMs = dueMs };
        Note(ctx, cast.Def, currentPlayers.Count, $"wind-up {dueMs - now} ms");
        return Exec(cast.Def.During, ctx);
    }

    List<byte[]> EngineCastLanded(Target boss, Brain brain, BossTelegraph slam, Vector3d centre, int hits, long now)
    {
        if (slam.Tag is not EngineCast cast || brain.Fight?.Encounter is not { } run) return [];
        var ctx = new MechanicCtx(boss, brain, brain.Fight, run, now, cast.Rule) { CastCentre = centre, CastDueMs = now };
        Note(ctx, cast.Def, hits, slam.Land ? $"landed, {hits} hit" : "landed (no damage)");
        Emit(ctx, cast.Def, success: hits == 0, centre);
        return Exec(cast.Def.OnLand, ctx);
    }

    List<byte[]> EngineCastBroken(Target boss, Brain brain, BossTelegraph slam, Vector3d centre, long now)
    {
        if (slam.Tag is not EngineCast cast || brain.Fight?.Encounter is not { } run) return [];
        var ctx = new MechanicCtx(boss, brain, brain.Fight, run, now, cast.Rule) { CastCentre = centre };
        Note(ctx, cast.Def, 0, "broken");
        if (cast.Def.StaggerCheck == true) Emit(ctx, cast.Def, success: true, centre);
        return Exec(cast.Def.OnBreak, ctx);
    }

    /// <summary>telegraph: a ground shape that resolves after delay_ms; count/interval_ms place it again (re-aimed) that many times.</summary>
    List<byte[]> TelegraphAction(ActionDef a, MechanicCtx ctx)
    {
        var replies = new List<byte[]>();
        var count = Math.Max(1, a.Count ?? 1);
        var interval = ctx.Run.Ms(a.IntervalMs, 0);
        replies.AddRange(PlaceTelegraph(a, ctx));
        for (var i = 1; i < count; i++)
        {
            var iteration = i;
            Schedule(ctx.Run, ctx.Now + interval * i, now => PlaceTelegraph(a, ctx with { Now = now, Iteration = ctx.Iteration + iteration }));
        }
        return replies;
    }

    List<byte[]> PlaceTelegraph(ActionDef a, MechanicCtx ctx)
    {
        var run = ctx.Run;
        var shape = ShapeOf(a, run.Encounter, ctx.Iteration);
        var points = Points(a.At, ctx, a);
        var delay = run.Ms(a.DelayMs, 0);
        var seq = NextSeq(ctx.Brain);
        var replies = new List<byte[]>();
        if (a.Decal ?? true)
            foreach (var p in points) replies.AddRange(Decal(ctx.Boss, shape.Skill, seq, p.At, p.Yaw, delay, ctx.Now));
        List<byte[]> Resolve(long now)
        {
            var c = ctx with { Now = now };
            var hits = currentPlayers.Where(p => InsideAny(shape, points.Select(pt => (pt.At, pt.Yaw)), p.Position)).ToList();
            var packets = new List<byte[]>();
            if (shape.Skill > 0 && a.Damage is not null && points.Count > 0)
                packets.Add(GamePackets.SkillStart(shape.Skill, seq, points[0].Yaw, points[0].At, targetKey: Victim(ctx.Brain), actorKey: ctx.Boss.Key));
            foreach (var (key, position) in hits)
            {
                packets.AddRange(Hit(c, key, Roll(a.Damage, c, key), shape.Skill, seq, skillPacket: true));
                packets.AddRange(Exec(a.OnHit, c with { Player = key, Origin = position }));
            }
            Note(c, a, hits.Count, $"{points.Count} placed, {hits.Count} hit");
            Emit(c, a, hits.Count == 0, points.FirstOrDefault().At);
            packets.AddRange(Exec(hits.Count == 0 ? a.OnSuccess : a.OnFail, c));
            return packets;
        }
        if (delay <= 0) replies.AddRange(Resolve(ctx.Now));
        else Schedule(run, ctx.Now + delay, Resolve);
        return replies;
    }

    long EndOf(ActionDef a, MechanicCtx ctx)
    {
        if (a.Ends == "cast_land" && ctx.CastDueMs is { } due) return due + ctx.Run.Ms(a.LingerMs, 0);
        return a.DurationMs is not null ? ctx.Now + ctx.Run.Ms(a.DurationMs) : long.MaxValue;
    }

    /// <summary>zone: a lasting hazard (DoT field, puddle, whirlpools, outer-arena burn, wall grid), ticking damage on players inside.</summary>
    List<byte[]> ZoneAction(ActionDef a, MechanicCtx ctx)
    {
        var run = ctx.Run;
        var id = a.Id ?? $"zone{run.Zones.Count}";
        run.Zones.RemoveAll(z => z.Id == id);
        var points = Points(a.At, ctx, a);
        var tick = Math.Max(50, run.Ms(a.TickMs, 1000));
        var zone = new MechanicZone
        {
            Id = id, Rule = ctx.Rule, Def = a, Centres = points.Select(p => (p.At, p.Yaw)).ToList(), StartedMs = ctx.Now, EndsMs = EndOf(a, ctx),
            TickMs = tick, NextTickMs = ctx.Now + run.Ms(a.FirstTickMs, tick), BlocksMovement = a.BlocksMovement ?? false,
        };
        if (a.Grid is { } grid && points.Count > 0)
        {
            zone.Walls.AddRange(GridWalls.Build(grid, run.Encounter.Vars, points[0].At, grid.PatternOffset));
            zone.NextStepMs = ctx.Now + run.Ms(grid.StepIntervalMs, 2500);
        }
        run.Zones.Add(zone);
        var replies = new List<byte[]>();
        var skill = run.Encounter.Skill(a.Skill ?? 0);
        if (a.Decal ?? true)
            foreach (var p in points) replies.AddRange(Decal(ctx.Boss, skill, NextSeq(ctx.Brain), p.At, p.Yaw, Math.Min(zone.EndsMs - ctx.Now, 120_000), ctx.Now));
        if (a.EnvObj is { } env) replies.AddRange(Env(ctx, env, points.FirstOrDefault().At, a.Slot ?? run.EnvSlots++, false, id));
        Note(ctx, a, 0, zone.EndsMs == long.MaxValue ? "until the fight ends" : $"{zone.EndsMs - ctx.Now} ms");
        return replies;
    }

    bool InZone(MechanicZone zone, ResolvedEncounter enc, Vector3d at)
    {
        if (zone.Def.Grid is not null) return zone.Walls.Any(w => w.Contains(at));
        var shape = ShapeOf(zone.Def, enc, 0);
        return InsideAny(shape, zone.Centres.Select(c => (c.Centre, c.Yaw)), at);
    }

    List<byte[]> TickZones(MechanicCtx ctx)
    {
        var (run, now) = (ctx.Run, ctx.Now);
        var replies = new List<byte[]>();
        foreach (var zone in run.Zones.ToList())
        {
            var c = ctx with { Rule = zone.Rule };
            if (now >= zone.EndsMs)
            {
                run.Zones.Remove(zone);
                if (zone.Def.EnvObj is { } env) replies.AddRange(Env(c, env, zone.Centres.FirstOrDefault().Centre, zone.Def.Slot ?? 0, true, zone.Id));
                Note(c, zone.Def, 0, "ended");
                continue;
            }
            if (zone.Def.Grid is { } grid && now >= zone.NextStepMs)
            {
                zone.PatternIndex++;
                zone.NextStepMs = now + run.Ms(grid.StepIntervalMs, 2500);
                zone.Walls.Clear();
                zone.Walls.AddRange(GridWalls.Build(grid, run.Encounter.Vars, zone.Centres[0].Centre, zone.PatternIndex + grid.PatternOffset));
            }
            if (now < zone.NextTickMs) continue;
            zone.NextTickMs = now + zone.TickMs;
            var inside = currentPlayers.Where(p => InZone(zone, run.Encounter, p.Position)).ToList();
            foreach (var (key, position) in inside)
            {
                replies.AddRange(Hit(c, key, Roll(zone.Def.Damage, c, key), run.Encounter.Skill(zone.Def.Skill ?? 0), 0, skillPacket: false));
                replies.AddRange(Exec(zone.Def.OnHit, c with { Player = key, Origin = position }));
            }
            if (inside.Count > 0) Note(c, zone.Def, inside.Count, "tick");
        }
        return replies;
    }

    /// <summary>soak / stack: players within radius of the anchor share (split) or each take the hit; fewer than min_players fails.</summary>
    List<byte[]> SoakAction(ActionDef a, MechanicCtx ctx)
    {
        var run = ctx.Run;
        var stack = a.Type == "stack";
        var anchor = a.At ?? new AnchorDef(stack ? "victim" : "boss");
        var radius = run.N(a.Radius);
        var delay = run.Ms(a.DelayMs, 0);
        var skill = run.Encounter.Skill(a.Skill ?? 0);
        var replies = new List<byte[]>();
        var seq = NextSeq(ctx.Brain);
        if (a.Decal ?? true)
            foreach (var p in Points(anchor, ctx, a)) replies.AddRange(Decal(ctx.Boss, skill, seq, p.At, p.Yaw, delay, ctx.Now));
        List<byte[]> Resolve(long now)
        {
            var c = ctx with { Now = now };
            var packets = new List<byte[]>();
            var anchors = Points(anchor, c, a);
            if (anchors.Count == 0) { Note(c, a, 0, "no anchor"); return packets; }
            var centre = anchors[0];
            var inside = currentPlayers.Where(p => anchors.Any(an => Distance(an.At, Flat(p.Position)) <= radius)).ToList();
            var min = a.MinPlayers ?? 1;
            var split = a.Split ?? stack;
            var roller = centre.Player ?? inside.Select(p => p.Key).DefaultIfEmpty(PlayerKey).First();
            var raw = Roll(a.Damage, c, roller);
            var each = split ? Math.Max(raw > 0 ? 1 : 0, (int)Math.Round((double)raw / Math.Max(1, inside.Count))) : raw;
            foreach (var (key, position) in inside)
            {
                packets.AddRange(Hit(c, key, split ? each : Roll(a.Damage, c, key), skill, seq, skillPacket: skill > 0));
                packets.AddRange(Exec(a.OnHit, c with { Player = key, Origin = position }));
            }
            var ok = inside.Count >= min;
            Note(c, a, inside.Count, ok ? $"success ({inside.Count}/{min})" : $"fail ({inside.Count}/{min})");
            Emit(c, a, ok, centre.At, centre.Player);
            packets.AddRange(Exec(ok ? a.OnSuccess : a.OnFail, c with { Origin = centre.At, Player = centre.Player }));
            return packets;
        }
        if (delay <= 0) replies.AddRange(Resolve(ctx.Now));
        else Schedule(run, ctx.Now + delay, Resolve);
        return replies;
    }

    /// <summary>spread: a circle on each target; each player is hit once per circle covering them (overlap_scale extra per overlap). Any overlap fails.</summary>
    List<byte[]> SpreadAction(ActionDef a, MechanicCtx ctx)
    {
        var run = ctx.Run;
        var targets = Points(a.At ?? new AnchorDef("each_player"), ctx, a).Where(p => p.Player is not null).Select(p => p.Player!.Value).Distinct().ToList();
        var radius = run.N(a.Radius);
        var delay = run.Ms(a.DelayMs, 0);
        var skill = run.Encounter.Skill(a.Skill ?? 0);
        var seq = NextSeq(ctx.Brain);
        var replies = new List<byte[]>();
        if (a.Decal ?? true)
            foreach (var p in currentPlayers.Where(p => targets.Contains(p.Key))) replies.AddRange(Decal(ctx.Boss, skill, seq, p.Position, 0, delay, ctx.Now));
        List<byte[]> Resolve(long now)
        {
            var c = ctx with { Now = now };
            var centres = currentPlayers.Where(p => targets.Contains(p.Key)).Select(p => p.Position).ToList();
            var packets = new List<byte[]>();
            var overlapped = 0;
            var hit = 0;
            foreach (var (key, position) in currentPlayers)
            {
                var n = centres.Count(cn => Distance(cn, Flat(position)) <= radius);
                if (n == 0) continue;
                hit++;
                if (n > 1) overlapped++;
                var damage = (int)Math.Round(Roll(a.Damage, c, key) * (1 + run.N(a.OverlapScale, 1) * (n - 1)));
                packets.AddRange(Hit(c, key, damage, skill, seq, skillPacket: skill > 0));
                packets.AddRange(Exec(a.OnHit, c with { Player = key, Origin = position }));
            }
            Note(c, a, hit, overlapped == 0 ? "success" : $"fail ({overlapped} overlapped)");
            Emit(c, a, overlapped == 0);
            packets.AddRange(Exec(overlapped == 0 ? a.OnSuccess : a.OnFail, c));
            return packets;
        }
        if (delay <= 0) replies.AddRange(Resolve(ctx.Now));
        else Schedule(run, ctx.Now + delay, Resolve);
        return replies;
    }

    /// <summary>cover: line-of-sight objects (pillars); "length" is the covered depth behind each, "width" its full width.</summary>
    List<byte[]> CoverAction(ActionDef a, MechanicCtx ctx)
    {
        var run = ctx.Run;
        var ends = EndOf(a, ctx);
        var replies = new List<byte[]>();
        foreach (var old in run.Covers.Where(c => c.Id == a.Id && c.EnvSlot >= 0))
            if (a.EnvObj is { } oldEnv) replies.AddRange(Env(ctx, oldEnv, old.At, old.EnvSlot, true, a.Id!));
        run.Covers.RemoveAll(c => c.Id == a.Id);
        var points = Points(a.At, ctx, a);
        foreach (var p in points)
        {
            var slot = a.EnvObj is not null ? run.EnvSlots++ : -1;
            run.Covers.Add(new MechanicCover(a.Id!, p.At, run.N(a.Length, 900), run.N(a.Width, 1400) / 2, ends, slot));
            if (a.EnvObj is { } env)
            {
                replies.AddRange(Env(ctx, env, p.At, slot, false, a.Id!));
                if (ends != long.MaxValue)
                {
                    var at = p.At;
                    Schedule(run, ends, now => Env(ctx with { Now = now }, env, at, slot, true, a.Id!));
                }
            }
            var skill = run.Encounter.Skill(a.Skill ?? 0);
            if (skill > 0) replies.AddRange(Decal(ctx.Boss, skill, NextSeq(ctx.Brain), p.At, p.Yaw, Math.Min(ends - ctx.Now, 12_000), ctx.Now));
        }
        Note(ctx, a, points.Count, $"{points.Count} placed");
        return replies;
    }

    /// <summary>displace: knockback (away from the anchor), pull (toward it), to_radius (onto a circle around it), to_point (onto it).</summary>
    List<byte[]> DisplaceAction(ActionDef a, MechanicCtx ctx)
    {
        var run = ctx.Run;
        var origins = Points(a.At, ctx);
        if (origins.Count == 0) { Note(ctx, a, 0, "no anchor"); return []; }
        var origin = origins[0];
        var targets = Points(a.Target ?? new AnchorDef("each_player"), ctx).Where(p => p.Player is not null).ToList();
        var distance = run.N(a.Distance, 0);
        foreach (var t in targets)
        {
            double dx = t.At.X - origin.At.X, dy = t.At.Y - origin.At.Y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) { dx = 1; dy = 0; len = 1; }
            var to = a.Mode switch
            {
                "knockback" => new Vector3d(t.At.X + dx / len * distance, t.At.Y + dy / len * distance, t.At.Z),
                "pull" => distance <= 0 || distance >= len ? origin.At with { Z = t.At.Z } : new Vector3d(t.At.X - dx / len * distance, t.At.Y - dy / len * distance, t.At.Z),
                "to_radius" => ExpeditionBossMechanics.ArenaEdge(origin.At, t.At, run.N(a.Radius)),
                _ => origin.At with { Z = t.At.Z },
            };
            edgeSnaps.Add((t.Player!.Value, to));
        }
        Note(ctx, a, targets.Count, a.Mode!);
        return [];
    }

    /// <summary>summon: copies of an NPC (npc 0: of the boss itself) around a point, sent at the victim, kept in a named group; or a map wave.</summary>
    List<byte[]> SummonAction(ActionDef a, MechanicCtx ctx)
    {
        if (a.Wave is { } wave)
        {
            waveStarts.Add(wave);
            Note(ctx, a, 0, $"wave {wave}");
        }
        if (a.Npc is not { } npc) return [];
        var at = a.At is null ? (Vector3d?)null : Points(a.At, ctx, a).FirstOrDefault().At;
        var victim = a.Target is { } t ? Points(t, ctx).FirstOrDefault(p => p.Player is not null).Player ?? Victim(ctx.Brain) : Victim(ctx.Brain);
        var adds = new BossAdds(npc == 0 ? ctx.Boss.NpcId : npc, Math.Max(1, a.Count ?? 1), ctx.Run.N(a.Radius, 500));
        summonRequests.Add(new SummonRequest(ctx.Boss, ctx.Fight, adds, victim) { Group = a.Group, At = at });
        Note(ctx, a, adds.Count, $"npc {adds.Npc} x{adds.Count}{(a.Group is null ? "" : " -> " + a.Group)}");
        return [];
    }

    List<byte[]> DespawnGroupAction(ActionDef a, MechanicCtx ctx)
    {
        var replies = new List<byte[]>();
        var members = ctx.Fight.Groups.GetValueOrDefault(a.Group!) ?? [];
        foreach (var key in members)
        {
            if (current?.GetValueOrDefault(key) is not { } add || add.Dead && add.CorpseRemoved) continue;
            add.Hp = 0;
            add.CorpseRemoved = true;
            if (brains.TryGetValue(key, out var mind))
            {
                mind.Mode = Mode.Dead; mind.ClearThreat(); mind.VictimKey = 0; mind.Pending = null; mind.Volley.Clear();
                if (mind.Engaged) { mind.Engaged = false; replies.Add(GamePackets.CombatState(key, false)); }
            }
            ctx.Run.DeadSeen.Add(key);
            replies.Add(GamePackets.Death(key, 7));
        }
        Note(ctx, a, members.Count, "despawned");
        return replies;
    }

    /// <summary>
    /// shield: invuln (players' hits land for nothing) and/or an HP pool that absorbs damage (the boss's HP is held while it lasts),
    /// until broken (pool emptied, "until": "adds_dead:&lt;group&gt;" or "stagger") or expired (duration_ms: on_expire, a fail).
    /// </summary>
    List<byte[]> ShieldAction(ActionDef a, MechanicCtx ctx)
    {
        var (boss, run) = (ctx.Boss, ctx.Run);
        if (run.Shields.Remove(a.Id!, out var old) && old.Invuln) boss.Immune = old.WasImmune;
        var pool = a.Hp is not null ? run.N(a.Hp) : a.HpPct is not null ? boss.HpMax * run.N(a.HpPct) / 100.0 : double.PositiveInfinity;
        var shield = new MechanicShield
        {
            Id = a.Id!, Rule = ctx.Rule, Def = a, Invuln = a.Invuln == true, WasImmune = boss.Immune, Pool = pool, LockedHp = boss.Hp,
            EndsMs = a.DurationMs is not null ? ctx.Now + run.Ms(a.DurationMs) : long.MaxValue,
        };
        if (shield.Invuln) boss.Immune = true;
        run.Shields[a.Id!] = shield;
        Note(ctx, a, 0, shield.Invuln ? "invulnerable" : double.IsInfinity(pool) ? "up" : $"pool {pool:0}");
        return [];
    }

    List<byte[]> TickShields(MechanicCtx ctx)
    {
        var (boss, run, now) = (ctx.Boss, ctx.Run, ctx.Now);
        var replies = new List<byte[]>();
        foreach (var shield in run.Shields.Values.ToList())
        {
            var c = ctx with { Rule = shield.Rule };
            var broken = false;
            if (!double.IsInfinity(shield.Pool) && boss.Hp < shield.LockedHp && !boss.Dead)
            {
                shield.Pool -= shield.LockedHp - boss.Hp;
                boss.Hp = shield.Pool > 0 ? shield.LockedHp : Math.Max(1, shield.LockedHp + (int)Math.Round(shield.Pool));
                replies.Add(GamePackets.NpcHp(boss.Key, boss.Hp));
                broken = shield.Pool <= 0;
            }
            var until = shield.Def.Until;
            if (until?.StartsWith("adds_dead:", StringComparison.Ordinal) == true
                && ctx.Fight.Groups.GetValueOrDefault(until["adds_dead:".Length..]) is { Count: > 0 } members && !members.Any(Alive))
                broken = true;
            if (until == "stagger" && Staggered(boss, now)) broken = true;
            if (!broken && now < shield.EndsMs) continue;
            run.Shields.Remove(shield.Id);
            if (shield.Invuln) boss.Immune = shield.WasImmune;
            Note(c, shield.Def, 0, broken ? "broken" : "expired");
            Emit(c, shield.Def, broken);
            if (broken) run.Events.Enqueue(("shield_broken", shield.Id, boss.Position, null, 1, 0));
            replies.AddRange(Exec(broken ? shield.Def.OnBreak : shield.Def.OnExpire, c));
        }
        return replies;
    }

    /// <summary>stagger_check: the party must stagger the boss (break its gauge, or hold/stun it) within window_ms.</summary>
    List<byte[]> StaggerAction(ActionDef a, MechanicCtx ctx)
    {
        ctx.Run.StaggerWatches.Add(new MechanicStaggerWatch(a.Id ?? "stagger_check", ctx.Rule, a, ctx.Now + ctx.Run.Ms(a.WindowMs)));
        Note(ctx, a, 0, $"window {ctx.Run.Ms(a.WindowMs)} ms");
        if (a.Warning is { Length: > 0 } warning) return [GameSession.Notice(warning, a.WarningRu, 22, "FF8040")];
        return [];
    }

    List<byte[]> TickStaggerWatches(MechanicCtx ctx)
    {
        var replies = new List<byte[]>();
        foreach (var watch in ctx.Run.StaggerWatches.ToList())
        {
            var staggered = Staggered(ctx.Boss, ctx.Now);
            if (!staggered && ctx.Now < watch.EndsMs) continue;
            ctx.Run.StaggerWatches.Remove(watch);
            var c = ctx with { Rule = watch.Rule };
            Note(c, watch.Def, currentPlayers.Count, staggered ? "success" : "fail");
            Emit(c, watch.Def, staggered);
            replies.AddRange(Exec(staggered ? watch.Def.OnSuccess : watch.Def.OnFail, c));
        }
        return replies;
    }

    /// <summary>parts: breakable parts; the boss's HP loss is credited to the first unbroken part (CANDIDATE: no part hit wire yet).</summary>
    List<byte[]> PartsAction(ActionDef a, MechanicCtx ctx)
    {
        var run = ctx.Run;
        var parts = a.Parts?.ToList() ?? [];
        if (a.FromClient == true)
            parts.AddRange(BehaviorCases.PartsFor(ctx.Boss.NpcId).Where(p => parts.All(x => x.ClientPartId != p.PartsId))
                .Select(p => new PartDef { Id = $"part{p.PartsId}", Hp = p.MaxHp, ClientPartId = p.PartsId }));
        foreach (var part in parts)
        {
            var hp = part.Hp is not null ? run.N(part.Hp)
                : part.ClientPartId is { } cid && BehaviorCases.PartsFor(ctx.Boss.NpcId).FirstOrDefault(p => p.PartsId == cid) is { } client ? client.MaxHp : 1000;
            run.Parts.Add(new MechanicPart { Def = part, Rule = ctx.Rule, Hp = hp });
        }
        Note(ctx, a, 0, $"{parts.Count} parts");
        return [];
    }

    void TickParts(MechanicCtx ctx)
    {
        var run = ctx.Run;
        if (run.LastHp < 0 || ctx.Boss.Hp >= run.LastHp) return;
        var loss = (double)(run.LastHp - ctx.Boss.Hp);
        foreach (var part in run.Parts.Where(p => !p.Broken))
        {
            var taken = Math.Min(loss, part.Hp);
            part.Hp -= taken;
            loss -= taken;
            if (part.Hp <= 0)
            {
                part.Broken = true;
                run.Events.Enqueue(("part_broken", part.Def.Id, ctx.Boss.Position, null, 1, 0));
                var c = ctx with { Rule = part.Rule };
                run.Record(ctx.Now, part.Rule, "part:" + part.Def.Id, 0, "broken");
                if (part.Def.OnBreak is { Count: > 0 }) Schedule(run, ctx.Now, now => Exec(part.Def.OnBreak, c with { Now = now }));
            }
            if (loss <= 0) break;
        }
    }

    /// <summary>debuff: stacks on players (engine-tracked; optional client abnormal through <see cref="MechanicDebuffHook"/>); max reached runs on_max.</summary>
    List<byte[]> DebuffAction(ActionDef a, MechanicCtx ctx)
    {
        var run = ctx.Run;
        var map = run.Debuffs.TryGetValue(a.Debuff!, out var m) ? m : run.Debuffs[a.Debuff!] = [];
        var targets = Points(a.Target ?? a.At ?? new AnchorDef(ctx.Player is not null ? "event" : "victim"), ctx).Where(p => p.Player is not null).ToList();
        var replies = new List<byte[]>();
        var until = a.DurationMs is not null ? ctx.Now + run.Ms(a.DurationMs) : long.MaxValue;
        foreach (var t in targets)
        {
            var key = t.Player!.Value;
            var old = map.TryGetValue(key, out var s) && s.UntilMs > ctx.Now ? s.Stacks : 0;
            var stacks = old + (a.Stacks ?? 1);
            if (a.Max is { } max) stacks = Math.Min(stacks, max);
            map[key] = (stacks, until);
            for (var level = old + 1; level <= stacks; level++) run.Events.Enqueue(("stacks", a.Debuff!, t.At, key, level, level - 1));
            if (a.Abnormal is { } abnormal && MechanicDebuffHook is { } hook) replies.AddRange(hook(key, abnormal, stacks, a.DurationMs is null ? -1 : run.Ms(a.DurationMs)));
            if (a.Max is { } cap && stacks >= cap && old < cap) replies.AddRange(Exec(a.OnMax, ctx with { Player = key, Origin = t.At }));
        }
        Note(ctx, a, targets.Count, $"+{a.Stacks ?? 1}");
        return replies;
    }

    List<byte[]> CleanseAction(ActionDef a, MechanicCtx ctx)
    {
        if (!ctx.Run.Debuffs.TryGetValue(a.Debuff!, out var map)) return [];
        var targets = a.Target is null && a.At is null ? map.Keys.ToList() : Points(a.Target ?? a.At, ctx).Where(p => p.Player is not null).Select(p => p.Player!.Value).ToList();
        foreach (var key in targets) map.Remove(key);
        Note(ctx, a, targets.Count, "cleansed");
        return [];
    }

    static void TickDebuffs(MechanicCtx ctx)
    {
        foreach (var map in ctx.Run.Debuffs.Values)
            foreach (var key in map.Where(p => ctx.Now >= p.Value.UntilMs).Select(p => p.Key).ToList()) map.Remove(key);
    }

    /// <summary>tank_swap: the victim gains a stack; at max (default 2) the boss turns to the top-hate player without the debuff.</summary>
    List<byte[]> TankSwapAction(ActionDef a, MechanicCtx ctx)
    {
        var (brain, run) = (ctx.Brain, ctx.Run);
        var tank = Victim(brain);
        var map = run.Debuffs.TryGetValue(a.Debuff!, out var m) ? m : run.Debuffs[a.Debuff!] = [];
        var old = map.TryGetValue(tank, out var s) && s.UntilMs > ctx.Now ? s.Stacks : 0;
        var stacks = old + 1;
        map[tank] = (stacks, a.DurationMs is not null ? ctx.Now + run.Ms(a.DurationMs) : long.MaxValue);
        run.Events.Enqueue(("stacks", a.Debuff!, null, tank, stacks, old));
        if (stacks < (a.Max ?? 2)) { Note(ctx, a, 1, $"stack {stacks}"); return []; }
        var present = currentPlayers.Select(p => p.Key).ToHashSet();
        var next = brain.Hate.Where(h => h.Key != tank && present.Contains(h.Key)).OrderBy(h => run.Stacks(a.Debuff!, h.Key) > 0 ? 1 : 0)
            .ThenByDescending(h => h.Value).Select(h => (int?)h.Key).FirstOrDefault()
            ?? currentPlayers.Where(p => p.Key != tank).Select(p => (int?)p.Key).FirstOrDefault();
        if (next is not { } swap) { Note(ctx, a, 1, "no other tank"); return []; }
        brain.Hate[swap] = (brain.Hate.Count == 0 ? 0 : brain.Hate.Values.Max()) + 1;
        brain.VictimKey = brain.CandidateKey = swap;
        Note(ctx, a, 1, $"swap {tank} -> {swap}");
        return [GamePackets.TargetUpdate(ctx.Boss.Key, swap)];
    }

    /// <summary>orb: interaction objects; players touching them count toward "required" within window_ms (consume, per_player or per_tick).</summary>
    List<byte[]> OrbAction(ActionDef a, MechanicCtx ctx)
    {
        var run = ctx.Run;
        var orb = new MechanicOrb
        {
            Id = a.Id ?? "orb", Rule = ctx.Rule, Def = a, Required = a.Required ?? 1, Radius = run.N(a.Radius),
            EndsMs = a.WindowMs is not null ? ctx.Now + run.Ms(a.WindowMs) : long.MaxValue,
        };
        run.Orbs.RemoveAll(o => o.Id == orb.Id);
        var replies = new List<byte[]>();
        foreach (var p in Points(a.At, ctx, a))
        {
            var slot = a.EnvObj is not null ? run.EnvSlots++ : -1;
            orb.Orbs.Add((p.At, slot, true));
            if (a.EnvObj is { } env) replies.AddRange(Env(ctx, env, p.At, slot, false, orb.Id));
            if (a.Skill is { } skill) replies.AddRange(Decal(ctx.Boss, run.Encounter.Skill(skill), NextSeq(ctx.Brain), p.At, p.Yaw, Math.Min(orb.EndsMs - ctx.Now, 120_000), ctx.Now));
        }
        run.Orbs.Add(orb);
        Note(ctx, a, 0, $"{orb.Orbs.Count} orbs, {orb.Required} needed");
        return replies;
    }

    List<byte[]> TickOrbs(MechanicCtx ctx)
    {
        var (run, now) = (ctx.Run, ctx.Now);
        var replies = new List<byte[]>();
        foreach (var orb in run.Orbs.ToList())
        {
            var c = ctx with { Rule = orb.Rule };
            var mode = orb.Def.CountMode ?? "consume";
            for (var i = 0; i < orb.Orbs.Count && orb.Hits < orb.Required; i++)
            {
                var (at, slot, alive) = orb.Orbs[i];
                if (!alive) continue;
                foreach (var (key, position) in currentPlayers.Where(p => Distance(at, Flat(p.Position)) <= orb.Radius))
                {
                    if (mode == "per_player" && !orb.Touched.Add(key)) continue;
                    orb.Hits = Math.Min(orb.Required, orb.Hits + 1);
                    replies.AddRange(Exec(orb.Def.OnTouch, c with { Player = key, Origin = position }));
                    if (mode != "consume") continue;
                    orb.Orbs[i] = (at, slot, false);
                    if (orb.Def.EnvObj is { } env && slot >= 0) replies.AddRange(Env(c, env, at, slot, true, orb.Id));
                    break;
                }
            }
            var complete = orb.Hits >= orb.Required;
            if (!complete && now < orb.EndsMs) continue;
            run.Orbs.Remove(orb);
            foreach (var (at, slot, alive) in orb.Orbs.Where(o => o.Alive && o.Slot >= 0))
                if (orb.Def.EnvObj is { } env) replies.AddRange(Env(c, env, at, slot, true, orb.Id));
            Note(c, orb.Def, orb.Hits, complete ? "complete" : $"fail ({orb.Hits}/{orb.Required})");
            Emit(c, orb.Def, complete);
            replies.AddRange(Exec(complete ? orb.Def.OnComplete : orb.Def.OnFail, c));
        }
        return replies;
    }

    /// <summary>move: the boss jumps to a point (centre, edge, a player): placed there and stopped facing its victim.</summary>
    List<byte[]> MoveAction(ActionDef a, MechanicCtx ctx)
    {
        var (boss, brain) = (ctx.Boss, ctx.Brain);
        var points = Points(a.To, ctx, a);
        if (points.Count == 0) { Note(ctx, a, 0, "no point"); return []; }
        var to = points[0];
        boss.Position = to.At;
        brain.Path = null;
        brain.SentPosition = to.At;
        var facing = VictimPosition(brain) is { } v ? HeadingTo(boss.Position, Flat(v)) : brain.Heading;
        Note(ctx, a, 0, $"to ({to.At.X:0},{to.At.Y:0})");
        return Stop(boss, brain, facing, ctx.Now);
    }

    /// <summary>enrage: soft (damage and attack-speed multipliers for good) and, with hard, a lethal hit on everyone every every_ms.</summary>
    List<byte[]> EnrageAction(ActionDef a, MechanicCtx ctx)
    {
        var (fight, run) = (ctx.Fight, ctx.Run);
        double damage = run.N(a.DamageMul, 1), haste = run.N(a.HasteMul, 1);
        fight.Frenzied = true;
        run.Enraged = true;
        fight.Damage *= damage;
        fight.Haste *= haste;
        run.EnrageDamage *= damage;
        run.EnrageHaste *= haste;
        var replies = new List<byte[]>();
        if (a.Text is { Length: > 0 } text) replies.Add(GameSession.Notice(text, a.TextRu, a.Size ?? 24, a.Color ?? "FF4040"));
        if (a.Hard == true)
        {
            var every = Math.Max(500, run.Ms(a.EveryMs, 5000));
            List<byte[]> Wipe(long now)
            {
                if (ctx.Brain.Fight?.Encounter != run) return [];
                var c = ctx with { Now = now };
                var packets = new List<byte[]>();
                foreach (var (key, _) in currentPlayers) packets.AddRange(Hit(c, key, a.Damage is null ? LethalDamage : Roll(a.Damage, c, key), 0, 0, false));
                Note(c, a, currentPlayers.Count, "hard enrage hit");
                Schedule(run, now + every, Wipe);
                return packets;
            }
            replies.AddRange(Wipe(ctx.Now));
        }
        Note(ctx, a, currentPlayers.Count, $"damage x{damage}, haste x{haste}{(a.Hard == true ? ", hard" : "")}");
        return replies;
    }

    List<byte[]> WipeAction(ActionDef a, MechanicCtx ctx)
    {
        var replies = new List<byte[]>();
        if (a.Text is { Length: > 0 } text) replies.Add(GameSession.Notice(text, a.TextRu, a.Size ?? 24, a.Color ?? "FF4040"));
        var targets = Points(a.Target ?? new AnchorDef("each_player"), ctx).Where(p => p.Player is not null).ToList();
        foreach (var t in targets) replies.AddRange(Hit(ctx, t.Player!.Value, a.Damage is null ? LethalDamage : Roll(a.Damage, ctx, t.Player.Value), 0, 0, false));
        Note(ctx, a, targets.Count, "wipe");
        return replies;
    }

    List<byte[]> SetPhase(MechanicCtx ctx, string phase)
    {
        var run = ctx.Run;
        if (run.Phase == phase) return [];
        if (run.Phase is { } old) run.Events.Enqueue(("phase_exit", old, null, null, 1, 0));
        run.Phase = phase;
        run.PhaseStartedMs = ctx.Now;
        run.PhaseGeneration++;
        run.Events.Enqueue(("phase_enter", phase, null, null, 1, 0));
        run.Record(ctx.Now, ctx.Rule, "phase", 0, phase);
        return [];
    }

    List<byte[]> NoticeAction(ActionDef a, MechanicCtx ctx)
    {
        Note(ctx, a, currentPlayers.Count, a.Text!);
        return [GameSession.Notice(a.Text!, a.TextRu, a.Size ?? 22, a.Color ?? "FFB040")];
    }

    List<byte[]> CaptionAction(ActionDef a, MechanicCtx ctx)
    {
        Note(ctx, a, currentPlayers.Count, a.Talk!);
        return [GamePackets.Caption(a.Talk!)];
    }

    List<byte[]> EnvAction(ActionDef a, MechanicCtx ctx)
    {
        var run = ctx.Run;
        var despawn = a.Op == "despawn";
        var slot = a.Slot ?? (despawn ? 0 : run.EnvSlots++);
        var at = Points(a.At, ctx, a).FirstOrDefault().At;
        var replies = Env(ctx, a.EnvObj!.Value, at, slot, despawn, a.Id ?? "env");
        if (!despawn && a.DurationMs is not null)
            Schedule(run, ctx.Now + run.Ms(a.DurationMs), now => Env(ctx with { Now = now }, a.EnvObj!.Value, at, slot, true, a.Id ?? "env"));
        Note(ctx, a, 0, $"{(despawn ? "despawn" : "spawn")} {a.EnvObj} slot {slot}");
        return replies;
    }

    List<byte[]> DecalAction(ActionDef a, MechanicCtx ctx)
    {
        var replies = new List<byte[]>();
        var duration = (a.Ends == "cast_land" && ctx.CastDueMs is not null ? EndOf(a, ctx) : ctx.Now + ctx.Run.Ms(a.DurationMs, 3000)) - ctx.Now;
        var points = Points(a.At, ctx, a);
        foreach (var p in points) replies.AddRange(Decal(ctx.Boss, ctx.Run.Encounter.Skill(a.Skill!.Value), NextSeq(ctx.Brain), p.At, p.Yaw, duration, ctx.Now));
        Note(ctx, a, points.Count, $"{duration} ms");
        return replies;
    }

    /// <summary>modify: the boss's damage and attack-speed multipliers. "phase" (default) is the legacy phase rule: never weaker, enrage kept.</summary>
    List<byte[]> ModifyAction(ActionDef a, MechanicCtx ctx)
    {
        var (fight, run) = (ctx.Fight, ctx.Run);
        double damage = run.N(a.DamageMul, 1), haste = run.N(a.HasteMul, 1);
        switch (a.Mode ?? "phase")
        {
            case "mul": fight.Damage *= damage; fight.Haste *= haste; break;
            case "set": fight.Damage = damage * run.EnrageDamage; fight.Haste = haste * run.EnrageHaste; break;
            default:
                fight.Damage = Math.Max(fight.Damage, damage * (fight.Frenzied ? run.EnrageDamage : 1));
                fight.Haste = Math.Min(fight.Haste, haste * (fight.Frenzied ? run.EnrageHaste : 1));
                break;
        }
        Note(ctx, a, 0, $"damage x{fight.Damage:0.##}, haste x{fight.Haste:0.##}");
        return [];
    }

    List<byte[]> SetRotationAction(ActionDef a, MechanicCtx ctx)
    {
        var fight = ctx.Fight;
        var rotation = a.Rotation!.Select(r => TelegraphOf(ctx.Run.Encounter, r, "rotation")).ToList();
        var fillerActive = fight.Telegraph is null || ReferenceEquals(fight.Telegraph, fight.Filler) || !(fight.Telegraph.Once || fight.Telegraph.StaggerCheck);
        fight.Rotation = rotation;
        fight.RotationIndex = 0;
        fight.Filler = rotation[0];
        if (fillerActive && fight.Winding is null)
        {
            fight.Telegraph = rotation[0];
            fight.NextSlamMs = ctx.Now + rotation[0].EveryMs / 2;
        }
        Note(ctx, a, 0, $"{rotation.Count} casts");
        return [];
    }

    List<byte[]> CounterAction(ActionDef a, MechanicCtx ctx)
    {
        var run = ctx.Run;
        var old = run.Counters.GetValueOrDefault(a.Counter!);
        var value = a.Set is not null ? run.N(a.Set) : old + run.N(a.Add);
        run.Counters[a.Counter!] = value;
        run.Events.Enqueue(("counter", a.Counter!, ctx.Origin, ctx.Player, value, old));
        Note(ctx, a, 0, $"{a.Counter}={value}");
        return [];
    }

    List<byte[]> DelayAction(ActionDef a, MechanicCtx ctx)
    {
        Schedule(ctx.Run, ctx.Now + ctx.Run.Ms(a.Ms), now => Exec(a.Do, ctx with { Now = now }));
        return [];
    }

    /// <summary>repeat: run "do" every every_ms (first after delay_ms, default every_ms) for count times, duration_ms, or until the cast lands (+ linger_ms).</summary>
    List<byte[]> RepeatAction(ActionDef a, MechanicCtx ctx)
    {
        var run = ctx.Run;
        var every = Math.Max(50, run.Ms(a.EveryMs));
        var end = EndOf(a, ctx);
        void Next(int iteration, long due)
        {
            if (a.Count is { } count && iteration >= count) return;
            if (a.Count is null && due >= end) return;
            Schedule(run, due, now =>
            {
                if (ctx.Brain.Fight?.Encounter != run) return [];
                Next(iteration + 1, due + every);
                return Exec(a.Do, ctx with { Now = now, Iteration = iteration });
            });
        }
        Next(0, ctx.Now + run.Ms(a.DelayMs, every));
        Note(ctx, a, 0, a.Count is { } n ? $"x{n} every {every} ms" : $"every {every} ms");
        return [];
    }

    List<byte[]> RandomAction(ActionDef a, MechanicCtx ctx)
    {
        var choices = a.Choices!;
        var total = choices.Sum(c => Math.Max(0, ctx.Run.N(c.Weight, 1)));
        var roll = rng.NextDouble() * total;
        foreach (var (choice, index) in choices.Select((c, i) => (c, i)))
        {
            roll -= Math.Max(0, ctx.Run.N(choice.Weight, 1));
            if (roll > 0 && index < choices.Count - 1) continue;
            Note(ctx, a, 0, $"choice {index}");
            return Exec(choice.Do, ctx);
        }
        return [];
    }

    List<byte[]> MarkAction(ActionDef a, MechanicCtx ctx)
    {
        var target = Points(a.Target ?? a.At ?? new AnchorDef("victim"), ctx).FirstOrDefault(p => p.Player is not null);
        if (target.Player is not { } key) { Note(ctx, a, 0, "nobody to mark"); return []; }
        ctx.Run.Marks[a.Name!] = key;
        Note(ctx, a, 1, $"{a.Name}={key}");
        return a.Text is { Length: > 0 } text ? [GameSession.Notice(text, a.TextRu, a.Size ?? 22, a.Color ?? "FF8040")] : [];
    }

    List<byte[]> EmitAction(ActionDef a, MechanicCtx ctx)
    {
        ctx.Run.Events.Enqueue(("event", a.Event!, ctx.Origin, ctx.Player, 1, 0));
        Note(ctx, a, 0, a.Event!);
        return [];
    }

    /// <summary>Whether an engine zone that blocks movement stops a step from <paramref name="from"/> into it.</summary>
    bool EngineBlocksMove(Vector3d from, Vector3d to, long now)
    {
        foreach (var key in activeRuns)
        {
            if (!brains.TryGetValue(key, out var brain) || brain.Fight?.Encounter is not { } run) continue;
            foreach (var zone in run.Zones)
                if (zone.BlocksMovement && now < zone.EndsMs && InZone(zone, run.Encounter, to) && !InZone(zone, run.Encounter, from)) return true;
        }
        return false;
    }
}
