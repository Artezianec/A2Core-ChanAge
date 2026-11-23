namespace Aion.World;

/// <summary>
/// Abnormal (buff/debuff) engine v2: stack groups, priority, and a registry of effect-type handlers.
/// Data rows stay in tools/data (skill-areas.json, boxes.json); this layer applies them without per-skill code.
/// </summary>
public static class AbnormalEngine
{
    public enum StackKind { Replace, Refresh, StackN, RejectWeaker }

    /// <summary>What to do when a new abnormal lands on a target that already has buffs.</summary>
    public enum StackAction { AddNew, RefreshExisting, Reject, RemoveSameGroup }

    public sealed record Definition(
        int Id,
        int Group,
        StackKind StackKind,
        int StackLimit,
        int Priority,
        int DurationMs,
        int TickMs,
        bool Passive,
        bool Dispellable,
        IReadOnlyList<(int EffectId, string Type, IReadOnlyList<string> Values)> Effects);

    static readonly HashSet<string> KnownEffectTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "UseRebirth", "Polymorph", "PetPolymorph", "StatChange", "Hot_Item", "HotRatio_Item", "Hot", "Hot_Ratio", "Slow",
        "ChangeRelationshipEntity", "Dot_NormalCalc", "Dot_TargetMaxHP", "Dot_TargetHP", "Dot_Dmg", "KnockdownInstant",
        "Stun", "Hold", "Stone", "Sleep", "Aerial", "Fear", "Knockdown", "Frozen", "Root", "BlockMovePosition", "Paralysis",
        "Bind", "Silence", "Blockade", "BlockSkill", "BlockActive", "Snare", "Taunt", "ImmuneDecreaseHP", "Invincible",
        "ImmuneSkillEffectGroup", "HpBarrier", "DamageReflect", "ImmuneAbnormal", "ImmuneBind", "Blind", "Stagger",
        "BlockTargeting", "Poison", "Bleed", "Knockback", "WeaponEquipable", "NoEffect",
        // Skill effect engine (SkillEffectRegistry, GameSession.SkillEffectEngine.cs).
        "TriggerSkillByAttackAll", "TriggerSkillByGetDamage", "TriggerSkillByKill", "TriggerSkillByGetAbnormal", "TriggerSkillByAddAbnormal",
        "TriggerSkillBySelfBlock", "TriggerSkillBySelfEvade", "TriggerSkillBySelfWeaponBlock", "TriggerSkillBySelfShieldBlock",
        "Mot", "Mot_Ratio", "Sot", "Sot_Ratio", "AbnormalClear", "OnExpireAbnormal", "DecreaseCooltimeByKill", "CastSkill",
    };

    static int unknownEffectTypeCount;
    static readonly HashSet<string> unknownEffectTypesLogged = new(StringComparer.OrdinalIgnoreCase);

    public static int UnknownEffectTypeCount => unknownEffectTypeCount;

    /// <summary>Builds a v2 definition from the abnormal tables (group/stack from AbnormalStackRules, effects from AbnormalSpecs).</summary>
    public static Definition DefinitionOf(int abnormalId, int durationMs = 0, bool passive = false)
    {
        var rule = AbnormalStackRules.For(abnormalId);
        var stack = rule.OverlapTime switch
        {
            "Separate" => StackKind.StackN,
            "Longer" or "Renew" => StackKind.Refresh,
            _ => rule.Replace.Equals("Reject", StringComparison.OrdinalIgnoreCase) ? StackKind.RejectWeaker : StackKind.Replace,
        };
        var effects = AbnormalSpecs.Info(abnormalId)?.Effects
            ?? (IReadOnlyList<(int EffectId, string Type, IReadOnlyList<string> Values)>)Array.Empty<(int, string, IReadOnlyList<string>)>();
        var tick = effects.Select(e => TickMsOf(e.Type, e.Values)).FirstOrDefault(t => t > 0);
        return new Definition(abnormalId, rule.Group, stack, Math.Max(1, rule.OverlapCount), abnormalId, durationMs, tick,
            passive, !passive, effects);
    }

    static int TickMsOf(string type, IReadOnlyList<string> values)
    {
        if (type is "Hot" or "Hot_Ratio" or "Hot_Item" or "HotRatio_Item" or "Dot_TargetMaxHP" or "Dot_TargetHP" or "Dot_Dmg" or "Dot_NormalCalc")
            return values.Count > 0 && int.TryParse(values[0], out var v) && v > 0 ? v : 0;
        return 0;
    }

    /// <summary>
    /// Resolves stacking against active buffs with the same group. <paramref name="incomingStats"/> is used for RejectWeaker
    /// (StatChange rows only); empty skips the weaker check.
    /// </summary>
    public static (StackAction Action, IReadOnlyList<int> RemoveAbnormalIds) ResolveStack(
        IEnumerable<(int AbnormalId, int Group, Dictionary<string, int> Stats)> active,
        Definition incoming,
        IReadOnlyDictionary<string, int> incomingStats)
    {
        var sameId = active.Where(b => b.AbnormalId == incoming.Id).ToList();
        if (sameId.Count > 0 && incoming.StackKind is StackKind.Refresh or StackKind.Replace)
            return (StackAction.RefreshExisting, []);
        if (incoming.StackKind == StackKind.RejectWeaker && IsWeaker(incomingStats, active.Where(b => b.Group == incoming.Group && b.AbnormalId != incoming.Id)))
            return (StackAction.Reject, []);
        var sameGroup = active.Where(b => b.Group == incoming.Group && b.AbnormalId != incoming.Id).ToList();
        if (sameGroup.Count == 0) return (StackAction.AddNew, []);
        if (incoming.StackKind == StackKind.StackN && sameId.Count >= incoming.StackLimit)
            return (StackAction.RefreshExisting, []);
        return (StackAction.RemoveSameGroup, sameGroup.Select(b => b.AbnormalId).ToList());
    }

    /// <summary>One active abnormal on a target, as <see cref="Resolve"/> sees it.</summary>
    public readonly record struct Active(int Uid, int AbnormalId, int Group, int Level, long RemainingMs);

    public enum Outcome { Add, Refresh, Reject }

    /// <summary>What a new abnormal does to a target: add it (after removing <c>Remove</c>), refresh <c>RefreshUid</c>, or nothing.</summary>
    public readonly record struct Decision(Outcome Outcome, int RefreshUid, IReadOnlyList<int> Remove, bool KeepLonger, string Reason);

    /// <summary>
    /// SkillAbnormal stacking in one place (v3, docs/SKILL-EFFECT-ENGINE.md). The same abnormal id: AbnormalOverlapTimeType
    /// Separate stacks up to AbnormalOverlapCount (then refreshes the oldest), Longer keeps the later expiry, Renew / None restart
    /// it. Another abnormal of the same GroupId: AbnormalReplaceType Replace / None removes it; Priority and PriorityReplace keep a
    /// higher-level one (CANDIDATE: the export has no other priority field: DisplayPriority is 50 on nearly every row); Time keeps
    /// the one with more time left than the incoming duration (CANDIDATE). A duration below 0 (passives) never loses on time.
    /// </summary>
    public static Decision Resolve(IReadOnlyList<Active> active, int abnormalId, int level, int durationMs, AbnormalStackRules.Rule rule)
    {
        var sameId = active.Where(b => b.AbnormalId == abnormalId).ToList();
        var sameGroup = active.Where(b => b.Group == rule.Group && b.AbnormalId != abnormalId).ToList();
        var replace = rule.Replace ?? "Replace";
        foreach (var other in sameGroup)
        {
            if (replace is "Priority" or "PriorityReplace" && other.Level > level)
                return new(Outcome.Reject, 0, [], false, $"group {rule.Group}: {other.AbnormalId} level {other.Level} > {level}");
            if (replace == "Time" && durationMs >= 0 && other.RemainingMs > durationMs)
                return new(Outcome.Reject, 0, [], false, $"group {rule.Group}: {other.AbnormalId} has {other.RemainingMs} ms left");
        }
        var remove = sameGroup.Select(b => b.Uid).ToList();
        if (sameId.Count == 0) return new(Outcome.Add, 0, remove, false, "new");
        if (rule.OverlapTime == "Separate")
            return sameId.Count < Math.Max(1, rule.OverlapCount)
                ? new(Outcome.Add, 0, remove, false, $"stack {sameId.Count + 1}/{rule.OverlapCount}")
                : new(Outcome.Refresh, sameId.OrderBy(b => b.RemainingMs).First().Uid, remove, false, $"stack full {rule.OverlapCount}");
        return new(Outcome.Refresh, sameId[0].Uid, remove, rule.OverlapTime == "Longer", rule.OverlapTime == "Longer" ? "longer" : "renew");
    }

    static bool IsWeaker(IReadOnlyDictionary<string, int> incoming, IEnumerable<(int AbnormalId, int Group, Dictionary<string, int> Stats)> existing)
    {
        if (incoming.Count == 0) return false;
        foreach (var (_, _, stats) in existing)
        {
            var shared = incoming.Keys.Intersect(stats.Keys).ToList();
            if (shared.Count == 0) continue;
            if (shared.All(k => incoming[k] <= stats[k]) && shared.Any(k => incoming[k] < stats[k]))
                return true;
        }
        return false;
    }

    public static Dictionary<string, int> StatModifiers(int abnormalId) =>
        AbnormalSpecs.Info(abnormalId)?.Effects.Aggregate(new Dictionary<string, int>(), (dict, e) =>
        {
            if (e.Type != "StatChange" || e.Values.Count < 2 || !int.TryParse(e.Values[1], out var amount)) return dict;
            dict[e.Values[0]] = dict.GetValueOrDefault(e.Values[0]) + amount;
            return dict;
        }) ?? [];

    /// <summary>Logs and counts an effect type the server does not model yet (PlaytestDebug action abnormal_unknown_effect).</summary>
    public static void NoteUnknownEffectType(int abnormalId, string type)
    {
        if (KnownEffectTypes.Contains(type)) return;
        unknownEffectTypeCount++;
        if (!unknownEffectTypesLogged.Add(type)) return;
        PlaytestDebug.Action("abnormal_unknown_effect", ("abnormalId", abnormalId), ("effectType", type), ("count", unknownEffectTypeCount));
    }

    internal delegate void EffectHandler(AbnormalEffectContext ctx);

    internal sealed class AbnormalEffectContext
    {
        public required BuffFacade Buff { get; init; }
        public required int EffectId { get; init; }
        public required string Type { get; init; }
        public required IReadOnlyList<string> Values { get; init; }
        public required int DurationMs { get; init; }
        public required long Now { get; init; }
        public required Random Rng { get; init; }
        public required int HpMax { get; init; }
        public required List<byte[]> Replies { get; init; }
    }

    /// <summary>Facade so handlers can mutate buff state without referencing GameSession.Buff directly.</summary>
    internal interface BuffFacade
    {
        int AbnormalId { get; }
        Dictionary<string, int> Stats { get; }
        void SetRebirth(bool on);
        void SetPolymorph(bool on);
        void SetRelationship(int? relation);
        void SetKnockdown(int? effectId, bool held);
        void AddHeal(HealOverTimeRow row);
        void EnsureCombat();
        AbnormalCombat.BuffCombat? Combat { get; }
    }

    internal sealed class HealOverTimeRow
    {
        public required int EffectId, IntervalMs, PerTick, Remaining;
        public required long NextAt;
    }

    static readonly Dictionary<string, EffectHandler> handlers = new(StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyDictionary<string, EffectHandler> Handlers => handlers;

    static AbnormalEngine()
    {
        RegisterCoreHandlers();
    }

    static void Register(string type, EffectHandler handler) => handlers[type] = handler;

    static void RegisterCoreHandlers()
    {
        Register("UseRebirth", ctx => ctx.Buff.SetRebirth(true));
        Register("Polymorph", ctx => ctx.Buff.SetPolymorph(true));
        Register("PetPolymorph", ctx => ctx.Buff.SetPolymorph(true));
        Register("StatChange", ctx =>
        {
            if (ctx.Values.Count < 2 || !int.TryParse(ctx.Values[1], out var amount)) return;
            ctx.Buff.Stats[ctx.Values[0]] = ctx.Buff.Stats.GetValueOrDefault(ctx.Values[0]) + amount;
        });
        Register("Slow", ctx =>
        {
            if (ctx.Values.Count < 1 || !int.TryParse(ctx.Values[0], out var amount)) return;
            ctx.Buff.Stats["MoveSpeed"] = ctx.Buff.Stats.GetValueOrDefault("MoveSpeed") - amount;
        });
        Register("ChangeRelationshipEntity", ctx =>
        {
            if (ctx.Values.Count >= 2 && MapEventTables.Relationship(ctx.Values[1]) is { } relation)
                ctx.Buff.SetRelationship(relation);
        });
    }

    /// <summary>Applies one abnormal effect row through the handler registry, then combat rows, logging unknown types.</summary>
    internal static void ApplyEffectRow(AbnormalEffectContext ctx)
    {
        if (handlers.TryGetValue(ctx.Type, out var handler))
        {
            handler(ctx);
            return;
        }
        switch (ctx.Type)
        {
            case "Hot_Item" or "Hot" when Value(ctx.Values, 0) > 0:
            case "HotRatio_Item" or "Hot_Ratio" when Value(ctx.Values, 0) > 0:
            case "Dot_NormalCalc" when Value(ctx.Values, 0) > 0:
            case "Dot_TargetMaxHP" when Value(ctx.Values, 0) > 0:
            case "KnockdownInstant":
                // GameSession.Buffs fills heals and NPC-specific dots in its adapter (needs session context).
                return;
        }
        ctx.Buff.EnsureCombat();
        if (ctx.Buff.Combat is { } combat)
        {
            var before = combat.Held || combat.Silence || combat.Dots.Count > 0;
            AbnormalCombat.ApplyRow(ctx.EffectId, ctx.Type, ctx.Values, combat, ctx.DurationMs, ctx.Now, ctx.Rng, ctx.HpMax);
            if (!before && !combat.Held && !combat.Silence && combat.Dots.Count == 0
                && combat.BarrierAmount == 0 && combat.ReflectPermille == 0 && combat.ImmuneEffectTypes.Count == 0 && !combat.ImmuneDebuffCategory)
                NoteUnknownEffectType(ctx.Buff.AbnormalId, ctx.Type);
        }
        else NoteUnknownEffectType(ctx.Buff.AbnormalId, ctx.Type);
    }

    static int Value(IReadOnlyList<string> values, int index) =>
        index < values.Count && int.TryParse(values[index], out var v) ? v : 0;
}