using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// The data side of the skill effect engine (docs/SKILL-EFFECT-ENGINE.md): tools/data/skill-effect-engine.json, generated from
/// the client export by tools/skill_effect_engine.py. Conditional procs (TriggerSkillBy* rows) by abnormal, the proc skills'
/// SkillEffect rows, life steal (Damage row drain columns), NPC HP-ratio damage, NPC abnormal rows the ported table lacks, the
/// SkillAbnormalEffect rows of abnormals no other table carries, and SkillEffectLv / SkillAbnormalEffectLv values per level.
/// The session side is GameSession.SkillEffectEngine.cs; type coverage is SkillEffectRegistry.
/// </summary>
public static class SkillEffectEngine
{
    /// <summary>One TriggerSkillBy* row of an abnormal.</summary>
    public sealed record TriggerSpec(
        [property: JsonPropertyName("effect_id")] int EffectId,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("on")] string On,
        [property: JsonPropertyName("proc_skill")] int ProcSkill,
        [property: JsonPropertyName("chance")] int Chance = 10_000,
        [property: JsonPropertyName("cooldown_ms")] int CooldownMs = 0,
        [property: JsonPropertyName("max_count")] int MaxCount = -1,
        [property: JsonPropertyName("element")] string? Element = null,
        [property: JsonPropertyName("results")] string[]? Results = null,
        [property: JsonPropertyName("boss_only")] bool BossOnly = false,
        [property: JsonPropertyName("target_effect")] string? TargetEffect = null,
        [property: JsonPropertyName("victim")] string? Victim = null,
        [property: JsonPropertyName("category")] string? Category = null,
        [property: JsonPropertyName("match")] int Match = 0,
        [property: JsonPropertyName("level_group")] string? LevelGroup = null,
        [property: JsonPropertyName("skill")] int Skill = 0,
        [property: JsonPropertyName("variant")] bool Variant = false);

    /// <summary>One SkillEffect row of a proc skill; <c>side</c> is self (the owner) or target (the hit / attacker).</summary>
    public sealed record ProcRow(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("side")] string Side,
        [property: JsonPropertyName("values")] string[] Values,
        [property: JsonPropertyName("level_group")] string? LevelGroup = null)
    {
        public bool OnSelf => Side == "self";
    }

    public sealed record ProcSkill(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("rows")] ProcRow[] Rows);

    /// <summary>Damage row drain columns [13] ratio (1/10000 of the damage), [14] cap (1/10000 of the caster's max HP), [25] hit results.</summary>
    public sealed record Drain(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("ratio_min")] int RatioMin,
        [property: JsonPropertyName("ratio_max")] int RatioMax,
        [property: JsonPropertyName("cap")] int Cap,
        [property: JsonPropertyName("results")] string[]? Results,
        [property: JsonPropertyName("effect_id")] int EffectId);

    /// <summary>Damage row [10] MaxHPRatio / CurrentHPRatio with [11] ratio (1/10000) and [12] cap (-1: none) on an NPC skill.</summary>
    public sealed record RatioDamage(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("ratio")] int Ratio,
        [property: JsonPropertyName("cap")] int Cap,
        [property: JsonPropertyName("effect_id")] int EffectId);

    sealed record ExtraAbnormal(
        [property: JsonPropertyName("group")] int Group,
        [property: JsonPropertyName("effects")] JsonElement[][] Effects);

    sealed record EngineFile(
        [property: JsonPropertyName("skill_effect_types")] Dictionary<string, int>? SkillEffectTypes,
        [property: JsonPropertyName("abnormal_effect_types")] Dictionary<string, int>? AbnormalEffectTypes,
        [property: JsonPropertyName("passives")] Dictionary<string, int[]>? Passives,
        [property: JsonPropertyName("triggers")] Dictionary<string, TriggerSpec[]>? Triggers,
        [property: JsonPropertyName("proc_skills")] Dictionary<string, ProcSkill>? ProcSkills,
        [property: JsonPropertyName("drains")] Dictionary<string, Drain>? Drains,
        [property: JsonPropertyName("ratio_damage")] Dictionary<string, RatioDamage>? RatioDamage,
        [property: JsonPropertyName("npc_abnormals")] Dictionary<string, int[][]>? NpcAbnormals,
        [property: JsonPropertyName("skill_elements")] Dictionary<string, string>? SkillElements,
        [property: JsonPropertyName("abnormal_effects")] Dictionary<string, ExtraAbnormal>? AbnormalEffects,
        [property: JsonPropertyName("effect_levels")] Dictionary<string, Dictionary<string, string[]>>? EffectLevels,
        [property: JsonPropertyName("abnormal_effect_levels")] Dictionary<string, Dictionary<string, string[]>>? AbnormalEffectLevels);

    sealed class Tables
    {
        public required EngineFile File { get; init; }
        public required Dictionary<int, TriggerSpec[]> Triggers { get; init; }
        public required Dictionary<int, ProcSkill> Procs { get; init; }
        public required Dictionary<int, Drain> Drains { get; init; }
        public required Dictionary<int, RatioDamage> Ratio { get; init; }
        public required Dictionary<int, AbnormalInfo> Abnormals { get; init; }
        public required Dictionary<int, IReadOnlyList<(int Abnormal, int MinMs, int MaxMs, int Chance, int Level)>> NpcAbnormals { get; init; }
        public required Dictionary<int, int[]> Passives { get; init; }
        public required Dictionary<int, string> Elements { get; init; }
    }

    static readonly Lazy<Tables> tables = new(Load);

    static int Key(string s) => int.Parse(s, CultureInfo.InvariantCulture);

    static Tables Load()
    {
        using var stream = typeof(SkillEffectEngine).Assembly.GetManifestResourceStream("skill-effect-engine.json");
        var file = (stream is null ? null : JsonSerializer.Deserialize<EngineFile>(stream))
            ?? new EngineFile(null, null, null, null, null, null, null, null, null, null, null, null);
        var abnormals = new Dictionary<int, AbnormalInfo>();
        foreach (var (id, extra) in file.AbnormalEffects ?? [])
        {
            var rows = extra.Effects.Select(e => (
                Id: e[0].ValueKind == JsonValueKind.Number ? e[0].GetInt32() : int.Parse(e[0].GetString()!, CultureInfo.InvariantCulture),
                Type: e[1].GetString() ?? "",
                Values: (IReadOnlyList<string>)e.Skip(2).Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString()).ToList()))
                .ToList();
            abnormals[Key(id)] = new AbnormalInfo(extra.Group, rows);
        }
        return new Tables
        {
            File = file,
            Triggers = (file.Triggers ?? []).ToDictionary(kv => Key(kv.Key), kv => kv.Value),
            Procs = (file.ProcSkills ?? []).ToDictionary(kv => Key(kv.Key), kv => kv.Value),
            Drains = (file.Drains ?? []).ToDictionary(kv => Key(kv.Key), kv => kv.Value),
            Ratio = (file.RatioDamage ?? []).ToDictionary(kv => Key(kv.Key), kv => kv.Value),
            Abnormals = abnormals,
            NpcAbnormals = (file.NpcAbnormals ?? []).ToDictionary(kv => Key(kv.Key),
                kv => (IReadOnlyList<(int, int, int, int, int)>)kv.Value.Where(r => r.Length >= 5).Select(r => (r[0], r[1], r[2], r[3], r[4])).ToList()),
            Passives = (file.Passives ?? []).ToDictionary(kv => Key(kv.Key), kv => kv.Value),
            Elements = (file.SkillElements ?? []).ToDictionary(kv => Key(kv.Key), kv => kv.Value),
        };
    }

    static bool Flag(string name, bool fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return raw.Trim() is not ("0" or "false" or "False" or "off" or "no");
    }

    /// <summary>
    /// AION_ABNORMAL_ENGINE_STACKING (default on): GameSession.AddBuff resolves stacking through AbnormalEngine.Resolve
    /// (Priority / Time replace rules). Off: the earlier same-id refresh / same-group replace path.
    /// </summary>
    public static bool AbnormalStacking { get; set; } = Flag("AION_ABNORMAL_ENGINE_STACKING", true);

    /// <summary>
    /// AION_SKILL_ENGINE_TRIGGERS (default on): the client TriggerSkillBy* rows of active abnormals (passives included) fire
    /// their proc skills, and Damage rows drain HP. Off: only tools/data/passive-triggers.json.
    /// </summary>
    public static bool ClientTriggers { get; set; } = Flag("AION_SKILL_ENGINE_TRIGGERS", true);

    /// <summary>AION_NPC_RATIO_DAMAGE (default on): NPC skills whose Damage row is MaxHPRatio / CurrentHPRatio hit for that share of the victim's HP.</summary>
    public static bool NpcRatioDamage { get; set; } = Flag("AION_NPC_RATIO_DAMAGE", true);

    public static IReadOnlyDictionary<string, int> SkillEffectTypeCounts => tables.Value.File.SkillEffectTypes ?? [];
    public static IReadOnlyDictionary<string, int> AbnormalEffectTypeCounts => tables.Value.File.AbnormalEffectTypes ?? [];

    public static IReadOnlyList<TriggerSpec> Triggers(int abnormalId) => tables.Value.Triggers.GetValueOrDefault(abnormalId) ?? [];
    public static bool HasTriggers(int abnormalId) => tables.Value.Triggers.ContainsKey(abnormalId);
    public static IEnumerable<int> TriggerAbnormals => tables.Value.Triggers.Keys;

    public static ProcSkill? Proc(int skillId) => tables.Value.Procs.GetValueOrDefault(skillId);
    public static Drain? DrainOf(int skillId) => tables.Value.Drains.GetValueOrDefault(skillId);
    public static RatioDamage? RatioOf(int skillId) => tables.Value.Ratio.GetValueOrDefault(skillId);
    public static string? ElementOf(int skillId) => tables.Value.Elements.GetValueOrDefault(skillId);

    /// <summary>The passive abnormals of a learned passive skill (client Abnormal_Passive rows).</summary>
    public static IReadOnlyList<int> PassiveAbnormals(int skillId) => tables.Value.Passives.GetValueOrDefault(skillId) ?? [];

    /// <summary>The abnormal rows of an NPC skill the ported table lacks: (abnormal, duration min, max ms, chance /10000, level).</summary>
    public static IReadOnlyList<(int Abnormal, int MinMs, int MaxMs, int Chance, int Level)> NpcAbnormals(int skillId) =>
        tables.Value.NpcAbnormals.GetValueOrDefault(skillId) ?? [];

    /// <summary>SkillAbnormalEffect rows of an abnormal no other table carries (AbnormalSpecs.Info falls back here).</summary>
    public static AbnormalInfo? Abnormal(int abnormalId) => tables.Value.Abnormals.GetValueOrDefault(abnormalId);

    /// <summary>A skill's client string key for logs (proc skills), else its class-skills name.</summary>
    public static string SkillName(int skillId)
    {
        if (skillId <= 0) return "";
        if (Proc(skillId) is { Name.Length: > 0 } proc) return proc.Name;
        return GameData.Embedded.Skill(skillId)?.Name ?? skillId.ToString(CultureInfo.InvariantCulture);
    }

    static string[]? LevelRow(Dictionary<string, Dictionary<string, string[]>>? groups, string? group, int level)
    {
        if (level <= 1 || string.IsNullOrEmpty(group) || groups?.TryGetValue(group, out var levels) != true || levels is null) return null;
        string[]? best = null;
        var bestLevel = 0;
        foreach (var (key, values) in levels)
            if (int.TryParse(key, out var lv) && lv <= level && lv > bestLevel)
                (best, bestLevel) = (values, lv);
        return best;
    }

    /// <summary>A SkillEffect row's values at a skill level: the SkillEffectLv row of its group (the highest stored level ≤ level), else the base.</summary>
    public static IReadOnlyList<string> EffectValuesAt(string? levelGroup, int level, IReadOnlyList<string> baseValues) =>
        LevelRow(tables.Value.File.EffectLevels, levelGroup, level) ?? baseValues;

    /// <summary>A SkillAbnormalEffect row's values at an abnormal level (SkillAbnormalEffectLv), else the base.</summary>
    public static IReadOnlyList<string> AbnormalValuesAt(string? levelGroup, int level, IReadOnlyList<string> baseValues) =>
        LevelRow(tables.Value.File.AbnormalEffectLevels, levelGroup, level) ?? baseValues;

    /// <summary>
    /// A trigger at a level: SkillAbnormalEffectLv rows carry the whole row ([0] proc skill, [1] chance, [2] cooldown, [3] count).
    /// </summary>
    public static TriggerSpec TriggerAt(TriggerSpec spec, int level)
    {
        var row = LevelRow(tables.Value.File.AbnormalEffectLevels, spec.LevelGroup, level);
        if (row is null || row.Length < 4) return spec;
        int V(int i, int fallback) => int.TryParse(row[i], out var v) ? v : fallback;
        return spec with { ProcSkill = V(0, spec.ProcSkill), Chance = V(1, spec.Chance), CooldownMs = V(2, spec.CooldownMs), MaxCount = V(3, spec.MaxCount) };
    }

    /// <summary>HP a hit drains: damage × ratio / 10000, capped at cap / 10000 of the caster's max HP (0 when the hit result does not drain).</summary>
    public static int DrainAmount(Drain drain, int damage, int casterHpMax, bool critical, Random rng)
    {
        if (damage <= 0) return 0;
        var result = critical ? "Critical" : "Normal";
        if (drain.Results is { Length: > 0 } results && !results.Contains(result, StringComparer.OrdinalIgnoreCase)) return 0;
        var ratio = rng.Next(Math.Min(drain.RatioMin, drain.RatioMax), Math.Max(drain.RatioMin, drain.RatioMax) + 1);
        var amount = (long)damage * ratio / 10_000;
        if (drain.Cap > 0) amount = Math.Min(amount, (long)casterHpMax * drain.Cap / 10_000);
        return (int)Math.Clamp(amount, 0, int.MaxValue);
    }

    /// <summary>An NPC skill's HP-ratio damage against a victim (MaxHPRatio of max HP, CurrentHPRatio of current HP), or null.</summary>
    public static int? RatioDamageAgainst(int skillId, int victimHp, int victimHpMax)
    {
        if (!NpcRatioDamage || RatioOf(skillId) is not { Ratio: > 0 } ratio) return null;
        var basis = ratio.Kind == "CurrentHPRatio" ? victimHp : victimHpMax;
        if (basis <= 0) return null;
        var amount = (long)basis * ratio.Ratio / 10_000;
        if (ratio.Cap > 0) amount = Math.Min(amount, ratio.Cap);
        return (int)Math.Clamp(amount, 1, int.MaxValue);
    }
}
