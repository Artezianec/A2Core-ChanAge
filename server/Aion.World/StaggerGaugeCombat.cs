using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Boss stagger gauge (client 그로기 / thin bar under HP): skills deal gauge damage; at max the target enters Stagger (EAbnormalEffectType).
/// The client reads <see cref="GamePackets.UpdateGroggyInfo"/> (0xe005); guard energy drains as gauge fills.
/// Values from tools/data/stagger-gauge-overrides.json on skills; improvised when tables are missing.
/// </summary>
public static class StaggerGaugeCombat
{
    /// <summary>Regular mob / sandbag internal gauge capacity (field mobs lazy-init this).</summary>
    public const int DefaultGaugeMax = 100;

    /// <summary>Capture 233052 wire _groggy_guard_max_energy (boss key 898702).</summary>
    public const int BossCaptureGroggyMax = 50;

    public const int StaggerAbnormalId = 100_000_401;
    public const int StaggerDurationMs = 5000;
    public const int StaggerBossReactionAbnormalId = 10_000_002;
    public const int StaggerPlayerReactionAbnormalId = 10_000_099;
    public const int StaggerBreakFollowupDelayMs = 98;

    /// <summary>Capture 233052: typical 0xe005 energy step (50→43→36…); skills may reach 10 (Jointstrike table).</summary>
    public const int BossCaptureMaxGroggyDrainPerHit = 10;

    /// <summary>Capture 233052: six drain packets before Jointstrike break (energy 5 remaining on wire).</summary>
    public const int BossCaptureDrainStepsBeforeBreak = 6;

    /// <summary>Regular field mobs: groggy exists but fills slowly; stagger breaks should be rare.</summary>
    public const int RegularMobMaxGroggyDrainPerHit = 1;

    /// <summary>HP damage needed to break a normal mob groggy bar (~5× max HP before one break).</summary>
    public const double RegularMobHpLossFractionPerBreak = 5.0;

    /// <summary>SkillEffect debuff abnormal on the boss at groggy break (e.g. 16140000 → 161400001).</summary>
    public static int StaggerBreakAbnormalId(int breakingSkillId) =>
        breakingSkillId > 0 ? breakingSkillId * 10 + 1 : StaggerAbnormalId;

    static int uidSeed = 3_000_000;

    /// <summary>
    /// True only for the groggy-gauge break debuff (161400001, legacy 100000401, …), not generic Stun/CC rows.
    /// SkillCond HasAbnormalEffectType Stagger and reactive spam must match this, not any held mob.
    /// </summary>
    public static bool IsGroggyBreakStaggerAbnormal(int abnormalId, int sourceSkillId = 0)
    {
        if (sourceSkillId > 0 && abnormalId == StaggerBreakAbnormalId(sourceSkillId))
            return true;
        if (!AbnormalSpecs.Types(abnormalId).Any(t => t.Equals("Stagger", StringComparison.OrdinalIgnoreCase)))
            return false;
        if (abnormalId == StaggerAbnormalId) return true;
        var skillRoot = abnormalId / 10;
        return skillRoot * 10 + 1 == abnormalId;
    }

    public static int GaugeMax(Target target) =>
        target.StaggerGaugeMax > 0 ? target.StaggerGaugeMax
        : target.GroggyMax > 0 ? target.GroggyMax
        : DefaultGaugeMax;

    /// <summary>Whether this NPC should accumulate player stagger gauge (boss/elite types or explicit template max).</summary>
    public static bool HasStaggerGauge(Target target) =>
        target.StaggerGaugeMax > 0 || target.GroggyMax > 0;

    static bool CanReceivePlayerStaggerGauge(Target target)
    {
        if (target.OwnerKey != 0 || target.Dead) return false;
        InitBossGauge(target);
        if (!HasStaggerGauge(target) && HasRegularMobGroggy(target))
            InitRegularMobGauge(target);
        return HasStaggerGauge(target);
    }

    static bool HasRegularMobGroggy(Target target) =>
        target.OwnerKey == 0 && target.SubType == "NormalMonster";

    static void InitRegularMobGauge(Target target)
    {
        if (target.GroggyMax > 0 || target.StaggerGaugeMax > 0) return;
        target.StaggerGaugeMax = DefaultGaugeMax;
        target.StaggerGauge = 0;
        SyncGroggyFromGauge(target);
    }

    internal static void InitRegularMobGaugeForTests(Target target) => InitRegularMobGauge(target);

    /// <summary>Reference HP budget for exported stagger_gauge table values (5000 HP sandbag × elite break fraction).</summary>
    public const int ReferenceHpBudgetForTable = 1750;

    /// <summary>
    /// Share of max HP dealt (per break) before the groggy bar empties. Bosses: capture 233052 / prior HP bands.
    /// Regular mobs: very large budget so stagger skills stay rare.
    /// </summary>
    public static double HpLossFractionPerStaggerBreak(Target target) => (target.SubType, target.Grade) switch
    {
        ("NormalMonster", _) => RegularMobHpLossFractionPerBreak,
        ("LegendMonster", _) => 0.175,
        ("HeroMonster", >= 5) => 0.20,
        ("HeroMonster", _) => 0.25,
        ("EliteMonster", _) => 0.35,
        _ when target.Grade >= 5 => 0.20,
        _ => 0.35,
    };

    static bool UsesBossGroggyDrainRules(Target target) =>
        HasBossGauge(target) || target.Key == Combat.TrainingDummyKey;

    static int FinalGaugeStep(Target target, int gauge)
    {
        gauge = Math.Max(1, gauge);
        if (UsesBossGroggyDrainRules(target)) return Math.Min(gauge, BossCaptureMaxGroggyDrainPerHit);
        if (HasRegularMobGroggy(target)) return Math.Min(gauge, RegularMobMaxGroggyDrainPerHit);
        return gauge;
    }

    /// <summary>Boss groggy per hit from Skill.dat stagger table (capture: 2–10), else typical 7-step drain.</summary>
    static int BossGaugeFromSkill(SkillDefinition def) => def.StaggerGauge > 0 ? def.StaggerGauge : 7;

    public static int HpBudgetPerStaggerBreak(Target target)
    {
        var hpMax = target.HpMax > 0 ? target.HpMax : 1;
        return Math.Max(1, (int)Math.Round(hpMax * HpLossFractionPerStaggerBreak(target)));
    }

    static bool UsesHpScaledGauge(Target target) =>
        HasBossGauge(target) || HasRegularMobGroggy(target) || target.StaggerGaugeMax > 0 || target.GroggyMax > 0;

    public static int GaugeDamage(GameData data, int skillId, Target? target = null, int hitDamage = 0)
    {
        if (data.Skill(skillId) is not { } def) return 0;
        if (target is not null && !HasStaggerGauge(target) && !HasBossGauge(target) && !HasRegularMobGroggy(target)) return 0;
        var gaugeMax = target is not null ? GaugeMax(target) : DefaultGaugeMax;
        var hpBudget = target is not null ? HpBudgetPerStaggerBreak(target) : ReferenceHpBudgetForTable;

        if (hitDamage > 0 && target is not null && UsesHpScaledGauge(target))
        {
            if (UsesBossGroggyDrainRules(target))
                return FinalGaugeStep(target, BossGaugeFromSkill(def));
            return FinalGaugeStep(target, (int)Math.Round(gaugeMax * (double)hitDamage / hpBudget));
        }

        int baseGauge;
        if (def.StaggerGauge > 0)
            baseGauge = def.StaggerGauge;
        else if (!def.Type.Equals("Active", StringComparison.OrdinalIgnoreCase) || def.Damage.Length == 0)
            return 0;
        else
        {
            // SkillEffect GroggyGuardDecreaseEnergy rows are not fully exported; MP bands from wiki (OURS).
            baseGauge = def.Cost.Mp switch
            {
                <= 50 => 3,
                <= 200 => 7,
                _ => 10,
            };
        }

        if (target is null) return baseGauge;
        if (hpBudget == ReferenceHpBudgetForTable) return FinalGaugeStep(target, baseGauge);
        return FinalGaugeStep(target, (int)Math.Round(baseGauge * (double)ReferenceHpBudgetForTable / hpBudget));
    }

    /// <summary>0xe005 _groggy_guard_energy: remaining bar (max when untouched, 0 when the bar breaks).</summary>
    public static int WireGroggyEnergy(Target target) =>
        target.StaggerGaugeMax > 0 || target.GroggyMax > 0
            ? Math.Max(0, GaugeMax(target) - target.StaggerGauge)
            : 0;

    /// <summary>Enable stagger gauge on elite/hero/legend bosses and optional NpcData template max.</summary>
    public static void InitBossGauge(Target target)
    {
        if (target.GroggyMax > 0) return;
        var templateMax = target.TemplateGroggyMax > 0 ? target.TemplateGroggyMax : 0;
        if (!HasBossGauge(target) && templateMax <= 0) return;
        target.StaggerGaugeMax = templateMax > 0 ? templateMax : BossCaptureGroggyMax;
        target.StaggerGauge = 0;
        SyncGroggyFromGauge(target);
    }

    public static bool HasBossGauge(Target target) =>
        target.OwnerKey == 0 && target.SubType is "EliteMonster" or "HeroMonster" or "LegendMonster";

    /// <summary>Client guard energy is full at 0 internal gauge and empty at break.</summary>
    public static void SyncGroggyFromGauge(Target target)
    {
        if (target.StaggerGaugeMax <= 0) return;
        target.GroggyMax = target.StaggerGaugeMax;
        target.GroggyEnergy = WireGroggyEnergy(target);
    }

    public static void SyncGaugeFromGroggy(Target target)
    {
        if (target.GroggyMax <= 0) return;
        target.StaggerGaugeMax = target.GroggyMax;
        target.StaggerGauge = Math.Max(0, target.GroggyMax - target.GroggyEnergy);
    }

    public static void ResetGauge(Target target)
    {
        if (target.GroggyMax <= 0 && target.StaggerGaugeMax <= 0) return;
        target.StaggerGauge = 0;
        if (target.StaggerGaugeMax <= 0) target.StaggerGaugeMax = DefaultGaugeMax;
        SyncGroggyFromGauge(target);
    }

    public static byte[]? GroggyUpdatePacket(Target target)
    {
        if (target.GroggyMax <= 0 && target.StaggerGaugeMax <= 0) return null;
        var max = GaugeMax(target);
        if (target.GroggyMax <= 0) target.GroggyMax = max;
        var energy = WireGroggyEnergy(target);
        target.GroggyEnergy = energy;
        return GamePackets.UpdateGroggyInfo(target.Key, max, energy);
    }

    public static void AppendGroggyWire(Target target, List<byte[]> replies) =>
        AppendGroggyDrainOrBreak(target, replies, breaking: false);

    public static void AppendGroggyBreakWire(Target target, List<byte[]> replies) =>
        AppendGroggyDrainOrBreak(target, replies, breaking: true);

    static void AppendGroggyDrainOrBreak(Target target, List<byte[]> replies, bool breaking)
    {
        if (target.GroggyMax <= 0 && target.StaggerGaugeMax <= 0) return;
        if (breaking)
            replies.Add(GamePackets.UpdateGroggyInfoBreak(target.Key));
        else if (GroggyUpdatePacket(target) is { } pkt)
            replies.Add(pkt);
    }

    public static List<byte[]> NpcAppearPackets(NpcAppearance appear, Target target)
    {
        var replies = new List<byte[]> { GamePackets.NpcAppear(appear) };
        AppendGroggyWire(target, replies);
        return replies;
    }

    /// <summary>Add gauge on a hostile NPC hit (row 0 only). Returns packets to broadcast.</summary>
    public static List<byte[]> AddGaugeDamage(
        Target victim,
        int gaugeDamage,
        int casterKey,
        long now,
        GameSession? session,
        bool firstRow,
        int breakingSkillId = 0)
    {
        if (gaugeDamage <= 0 || !firstRow || victim.Dead || victim.OwnerKey != 0) return [];
        if (victim.StaggerUntil > now) return [];
        if (!CanReceivePlayerStaggerGauge(victim)) return [];
        victim.StaggerGaugeMax = GaugeMax(victim);
        victim.StaggerGauge = Math.Min(GaugeMax(victim), victim.StaggerGauge + gaugeDamage);
        if (victim.GroggyMax > 0) SyncGroggyFromGauge(victim);
        if (victim.StaggerGauge < GaugeMax(victim))
            return GroggyUpdatePacket(victim) is { } pkt ? [pkt] : [];
        // Bar depleted (energy 0 on wire) before stagger; refill only after StaggerUntil in StaggerTick.
        var breakReplies = session is not null
            ? session.BreakNpcStagger(victim, now, breakingSkillId)
            : BreakStaggerShared(victim, casterKey, now, breakingSkillId);
        AppendGroggyBreakWire(victim, breakReplies);
        return breakReplies;
    }

    static List<byte[]> BreakStaggerShared(Target victim, int casterKey, long now, int breakingSkillId)
    {
        victim.StaggerUntil = now + StaggerDurationMs;
        victim.Held = true;
        var uid = Interlocked.Increment(ref uidSeed);
        var abnormalId = StaggerBreakAbnormalId(breakingSkillId);
        return
        [
            PlayerPackets.AbnormalAdd(victim.Key,
            [
                new AbnormalWire(uid, abnormalId, StaggerDurationMs, now + StaggerDurationMs,
                    CasterKey: casterKey, SkillId: breakingSkillId > 0 ? breakingSkillId : null, RangeBase: victim.Position),
            ]),
        ];
    }

    /// <summary>Clear stagger state and refill the client gauge when the window ends.</summary>
    public static List<byte[]> StaggerTick(IReadOnlyDictionary<int, Target>? targets, long now)
    {
        if (targets is null) return [];
        var replies = new List<byte[]>();
        foreach (var t in targets.Values)
        {
            if (t.StaggerUntil <= 0 || now < t.StaggerUntil) continue;
            t.StaggerUntil = 0;
            if (t.Held) t.Held = false;
            ResetGauge(t);
            AppendGroggyWire(t, replies);
        }
        return replies;
    }

    /// <summary>PvP player target: gauge toward Stagger abnormal (row 0 hits only).</summary>
    public static List<byte[]> AddPlayerGaugeDamage(GameSession victim, int gaugeDamage, int casterKey, long now, bool firstRow, GameSession? caster = null)
    {
        if (gaugeDamage <= 0 || !firstRow || victim.Dead || victim.StaggerUntil > now) return [];
        victim.StaggerGaugeMax = victim.StaggerGaugeMax > 0 ? victim.StaggerGaugeMax : DefaultGaugeMax;
        victim.StaggerGauge = Math.Min(victim.StaggerGaugeMax, victim.StaggerGauge + gaugeDamage);
        if (victim.StaggerGauge < victim.StaggerGaugeMax) return [];
        victim.StaggerGauge = 0;
        return victim.BreakPlayerStagger(now, casterKey);
    }
}
