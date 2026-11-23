namespace Aion.World;

/// <summary>
/// Every client ESkillEffectType (SkillEffect.EffectType) and EAbnormalEffectType (SkillAbnormalEffect.AbnormalEffectType) the
/// skill effect engine knows, with the handler family that resolves it. The universe of types and their row counts is
/// tools/data/skill-effect-engine.json (skill_effect_types / abnormal_effect_types); tools/skill_effect_coverage.py reads the two
/// tables below between their marker comments, so keep one entry per line in the (type, handler, legacy) shape.
/// <c>legacy</c> marks a type the server already resolved before the engine (coverage "before"). A type that is not listed is
/// unknown: the first row of it seen at run time is logged once (PlaytestDebug action skill_effect_unknown) with the skill name.
/// </summary>
public static class SkillEffectRegistry
{
    public enum Handler
    {
        /// <summary>Damage rows (class-skills.json damage, NPC damage, drain and HP-ratio columns).</summary>
        Damage,
        /// <summary>HP / MP / SP / AP restored at once (flat or of the maximum).</summary>
        Heal,
        /// <summary>MP / AP taken from an enemy.</summary>
        Burn,
        /// <summary>An abnormal put on the recipients (AbnormalEngine).</summary>
        Abnormal,
        /// <summary>A learned passive's abnormal (hidden buff).</summary>
        Passive,
        /// <summary>Cleanse / purge.</summary>
        Dispel,
        /// <summary>Running cooldowns changed.</summary>
        Cooldown,
        /// <summary>Hate moved.</summary>
        Aggro,
        /// <summary>Caster / target moved (teleports, pulls, knockbacks).</summary>
        Movement,
        /// <summary>NPCs or objects summoned / removed.</summary>
        Spawn,
        /// <summary>Item use rows (potions, scrolls); the item path resolves them.</summary>
        Item,
        /// <summary>Stagger gauge energy.</summary>
        Stagger,
        /// <summary>Stat modifiers.</summary>
        Stat,
        /// <summary>Heal / damage / resource over time.</summary>
        OverTime,
        /// <summary>Crowd control: held, rooted, slowed, taunted.</summary>
        Control,
        /// <summary>Casting blocked.</summary>
        Silence,
        /// <summary>Immunities and invulnerability.</summary>
        Immunity,
        /// <summary>Damage absorbed.</summary>
        Shield,
        /// <summary>Damage returned to the attacker.</summary>
        Reflect,
        /// <summary>Conditional procs (TriggerSkillBy*).</summary>
        Trigger,
        /// <summary>The abnormal's own life cycle: removed after hits, follow-up abnormal at expiry, cooldown on kill.</summary>
        Lifecycle,
        /// <summary>Form / relationship changes the client draws.</summary>
        Transform,
        /// <summary>Visual or client-side only: the server has nothing to do (NoEffect, animations, icons).</summary>
        ClientOnly,
    }

    public sealed record Entry(string Type, Handler Handler, bool Legacy);

    // skill-effect-types:begin
    static readonly Entry[] SkillEffectTypes =
    [
        new("Damage", Handler.Damage, true),
        new("Abnormal_True", Handler.Abnormal, true),
        new("Abnormal_Cc", Handler.Abnormal, true),
        new("Abnormal", Handler.Abnormal, true),
        new("Abnormal_Passive", Handler.Passive, true),
        new("Dispel_True", Handler.Dispel, true),
        new("Dispel", Handler.Dispel, true),
        new("Dispel_Cc", Handler.Dispel, true),
        new("HpHeal", Handler.Heal, true),
        new("HpHealRatio", Handler.Heal, true),
        new("MpHeal", Handler.Heal, true),
        new("MpHealRatio", Handler.Heal, true),
        new("SpHeal", Handler.Heal, true),
        new("SpHealRatio", Handler.Heal, true),
        new("APHeal", Handler.Heal, false),
        new("APHealRatio", Handler.Heal, false),
        new("MPBurn", Handler.Burn, true),
        new("APBurn", Handler.Burn, true),
        new("MPBurnRatio", Handler.Burn, false),
        new("DecreaseCooltime", Handler.Cooldown, true),
        new("IncreaseCooltime", Handler.Cooldown, true),
        new("AggroTransfer", Handler.Aggro, true),
        new("Teleport_Caster", Handler.Movement, true),
        new("Pre_SkillStart_Teleport_Caster", Handler.Movement, true),
        new("Pre_Cast_Teleport_Caster", Handler.Movement, true),
        new("Spawn", Handler.Spawn, true),
        new("Despawn", Handler.Spawn, true),
        new("GroggyGuardDecreaseEnergy", Handler.Stagger, true),
        new("HpHeal_Item", Handler.Item, true),
        new("HpHealRatio_Item", Handler.Item, true),
        new("MpHeal_Item", Handler.Item, true),
        new("FpHeal_Item", Handler.Item, true),
        new("CasterSkill", Handler.Item, true),
        new("NoEffect", Handler.ClientOnly, true),
        new("PlayAct", Handler.ClientOnly, true),
    ];
    // skill-effect-types:end

    // abnormal-effect-types:begin
    static readonly Entry[] AbnormalEffectTypes =
    [
        new("StatChange", Handler.Stat, true),
        new("Slow", Handler.Control, true),
        new("Snare", Handler.Control, true),
        new("Taunt", Handler.Control, true),
        new("Stun", Handler.Control, true),
        new("Hold", Handler.Control, true),
        new("Stone", Handler.Control, true),
        new("Sleep", Handler.Control, true),
        new("Aerial", Handler.Control, true),
        new("Fear", Handler.Control, true),
        new("Knockdown", Handler.Control, true),
        new("KnockdownInstant", Handler.Control, true),
        new("Frozen", Handler.Control, true),
        new("Root", Handler.Control, true),
        new("BlockMovePosition", Handler.Control, true),
        new("Paralysis", Handler.Control, true),
        new("Bind", Handler.Control, true),
        new("Stagger", Handler.Control, true),
        new("Silence", Handler.Silence, true),
        new("Blockade", Handler.Silence, true),
        new("BlockSkill", Handler.Silence, true),
        new("BlockActive", Handler.Silence, true),
        new("Hot", Handler.OverTime, true),
        new("Hot_Ratio", Handler.OverTime, true),
        new("Hot_Item", Handler.OverTime, true),
        new("HotRatio_Item", Handler.OverTime, true),
        new("Dot_NormalCalc", Handler.OverTime, true),
        new("Dot_TargetMaxHP", Handler.OverTime, true),
        new("Dot_TargetHP", Handler.OverTime, true),
        new("Dot_Dmg", Handler.OverTime, true),
        new("Mot", Handler.OverTime, false),
        new("Mot_Ratio", Handler.OverTime, false),
        new("Sot", Handler.OverTime, false),
        new("Sot_Ratio", Handler.OverTime, false),
        new("HpBarrier", Handler.Shield, true),
        new("DamageReflect", Handler.Reflect, true),
        new("ImmuneAbnormal", Handler.Immunity, true),
        new("ImmuneDecreaseHP", Handler.Immunity, true),
        new("ImmuneSkillEffectGroup", Handler.Immunity, true),
        new("ImmuneBind", Handler.Immunity, false),
        new("BlockTargeting", Handler.Immunity, true),
        new("UseRebirth", Handler.Lifecycle, true),
        new("AbnormalClear", Handler.Lifecycle, true),
        new("OnExpireAbnormal", Handler.Lifecycle, false),
        new("DecreaseCooltimeByKill", Handler.Lifecycle, false),
        new("CastSkill", Handler.Trigger, false),
        new("TriggerSkillByAttackAll", Handler.Trigger, false),
        new("TriggerSkillByGetDamage", Handler.Trigger, false),
        new("TriggerSkillByKill", Handler.Trigger, false),
        new("TriggerSkillByGetAbnormal", Handler.Trigger, false),
        new("TriggerSkillByAddAbnormal", Handler.Trigger, false),
        new("TriggerSkillBySelfBlock", Handler.Trigger, false),
        new("TriggerSkillBySelfEvade", Handler.Trigger, false),
        new("TriggerSkillBySelfWeaponBlock", Handler.Trigger, false),
        new("TriggerSkillBySelfShieldBlock", Handler.Trigger, false),
        new("Polymorph", Handler.Transform, true),
        new("PetPolymorph", Handler.Transform, true),
        new("ChangeRelationshipEntity", Handler.Transform, true),
        new("WeaponEquipable", Handler.Passive, true),
        new("NoEffect", Handler.ClientOnly, true),
        new("None", Handler.ClientOnly, true),
        new("IgnoreCollision", Handler.ClientOnly, false),
        new("OverrideIconType", Handler.ClientOnly, false),
        new("BlockAniHit", Handler.ClientOnly, false),
        new("EndMotion", Handler.ClientOnly, false),
        new("Rotate", Handler.ClientOnly, false),
        new("BlockRotate", Handler.ClientOnly, false),
    ];
    // abnormal-effect-types:end

    static readonly Dictionary<string, Entry> skillEffects = SkillEffectTypes.ToDictionary(e => e.Type, StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, Entry> abnormalEffects = AbnormalEffectTypes.ToDictionary(e => e.Type, StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> loggedSkill = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> loggedAbnormal = new(StringComparer.OrdinalIgnoreCase);
    static readonly object gate = new();

    public static IReadOnlyCollection<Entry> SkillEffectEntries => SkillEffectTypes;
    public static IReadOnlyCollection<Entry> AbnormalEffectEntries => AbnormalEffectTypes;

    public static Entry? SkillEffect(string type) => skillEffects.GetValueOrDefault(type);
    public static Entry? AbnormalEffect(string type) => abnormalEffects.GetValueOrDefault(type);
    public static bool IsHandledSkillEffect(string type) => skillEffects.ContainsKey(type);
    public static bool IsHandledAbnormalEffect(string type) => abnormalEffects.ContainsKey(type);

    /// <summary>A skill effect row the registry has no handler for: logged once per type with the skill that carried it.</summary>
    public static bool NoteSkillEffect(int skillId, string type)
    {
        if (skillEffects.ContainsKey(type)) return true;
        lock (gate)
            if (!loggedSkill.Add(type)) return false;
        PlaytestDebug.Action("skill_effect_unknown", ("table", "SkillEffect"), ("effectType", type), ("skillId", skillId),
            ("skill", SkillEffectEngine.SkillName(skillId)));
        return false;
    }

    /// <summary>An abnormal effect row the registry has no handler for: logged once per type with the abnormal and its skill.</summary>
    public static bool NoteAbnormalEffect(int abnormalId, string type, int skillId = 0)
    {
        if (abnormalEffects.ContainsKey(type)) return true;
        lock (gate)
            if (!loggedAbnormal.Add(type)) return false;
        PlaytestDebug.Action("skill_effect_unknown", ("table", "SkillAbnormalEffect"), ("effectType", type), ("abnormalId", abnormalId),
            ("skillId", skillId), ("skill", SkillEffectEngine.SkillName(skillId)));
        return false;
    }

    internal static void ResetLogForTests()
    {
        lock (gate)
        {
            loggedSkill.Clear();
            loggedAbnormal.Clear();
        }
    }
}
