using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// Abnormal rows extracted from Addiboii/A2Core build 26262663
/// (90af6b09, 5754e6fb, 58f13c69, 0ebedaa9, c75128f0, 74fed9b4) into tools/data/ported-skill-abnormals.json.
/// NPC skills' abnormals on players, class self-buffs that have no Damage rows, and target abnormals our skill-areas
/// export does not already apply. Effect rows fill <see cref="AbnormalSpecs.Info"/> only when boxes, skill-areas and
/// the dungeon buffs do not already know the id, so improvised overrides stay in place.
/// </summary>
public static class PortedSkillAbnormals
{
    public readonly record struct Row(int Abnormal, int MinMs, int MaxMs, int Level, int Chance, int EffectRow);

    public readonly record struct ScaledDot(int EffectId, int IntervalMs, int FlatMin, int FlatMax, int RatioMin, int RatioMax);

    public readonly record struct CombatFlags(bool Held, bool Immune, bool Silence, int SlowPermille);

    sealed record FileModel(
        [property: JsonPropertyName("npc_skills")] Dictionary<string, int[][]>? NpcSkills,
        [property: JsonPropertyName("self_skills")] Dictionary<string, int[][]>? SelfSkills,
        [property: JsonPropertyName("target_skills")] Dictionary<string, int[][]>? TargetSkills,
        [property: JsonPropertyName("abnormals")] Dictionary<string, AbnormalModel>? Abnormals);

    sealed record AbnormalModel([property: JsonPropertyName("g")] int G, [property: JsonPropertyName("e")] JsonElement[][] E);

    static readonly Lazy<FileModel> file = new(Load);

    static FileModel Load()
    {
        using var stream = typeof(PortedSkillAbnormals).Assembly.GetManifestResourceStream("ported-skill-abnormals.json")
            ?? throw new InvalidOperationException("ported-skill-abnormals.json is not embedded");
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<FileModel>(stream, opts) ?? new FileModel(null, null, null, null);
    }

    /// <summary>An NPC skill's abnormal rows; skills the ported table lacks fall back to the skill effect engine's (client SkillEffect rows).</summary>
    public static IReadOnlyList<Row> NpcRows(int skillId) => Rows(file.Value.NpcSkills, skillId) is { Count: > 0 } rows ? rows
        : SkillEffectEngine.NpcAbnormals(skillId).Select(r => new Row(r.Abnormal, r.MinMs, r.MaxMs, Math.Clamp(r.Level, 1, 255), r.Chance, 0)).ToList();

    public static IReadOnlyList<Row> SelfRows(int skillId) => Rows(file.Value.SelfSkills, skillId);

    public static IReadOnlyList<Row> TargetRows(int skillId) => Rows(file.Value.TargetSkills, skillId);

    /// <summary>Client SkillAbnormal group, only when no earlier table already owns the id (overrides keep their own stacking).</summary>
    public static int? GroupOf(int abnormalId)
    {
        if (RewardBoxes.Embedded.Abnormal(abnormalId) is not null || SkillAreas.Abnormal(abnormalId) is not null
            || DungeonBuffAbnormals.Of(abnormalId) is not null) return null;
        return file.Value.Abnormals?.TryGetValue(Key(abnormalId), out var row) == true ? row.G : null;
    }

    public static AbnormalInfo? Info(int abnormalId)
    {
        if (file.Value.Abnormals?.TryGetValue(Key(abnormalId), out var row) != true || row is null || row.E is not { Length: > 0 }) return null;
        var effects = row.E.Select(e => (
            e[0].GetInt32(),
            e[1].GetString() ?? "",
            (IReadOnlyList<string>)e.Skip(2).Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString()).ToList())).ToList();
        return new AbnormalInfo(row.G, effects);
    }

    public static IReadOnlyList<ScaledDot> ScaledDots(int abnormalId)
    {
        var list = new List<ScaledDot>();
        foreach (var (id, type, values) in Info(abnormalId)?.Effects ?? [])
        {
            if (!type.Equals("Dot_NormalCalc", StringComparison.OrdinalIgnoreCase)) continue;
            int V(int i) => i < values.Count && int.TryParse(values[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            if (V(0) <= 0) continue;
            list.Add(new ScaledDot(id, V(0), V(3), V(4), V(5), V(6)));
        }
        return list;
    }

    public static CombatFlags Combat(int abnormalId)
    {
        bool held = false, immune = false, silence = false;
        var slow = 0;
        foreach (var (_, type, values) in Info(abnormalId)?.Effects ?? [])
        {
            if (AbnormalSpecs.HoldTypes.Contains(type)) held = true;
            if (AbnormalSpecs.SilenceTypes.Contains(type)) silence = true;
            switch (type)
            {
                case "Slow" when values.Count > 0 && int.TryParse(values[0], out var permille):
                    slow = Math.Max(slow, permille);
                    break;
                case "Snare":
                    slow = 10_000;
                    break;
                case "ImmuneSkillEffectGroup" or "ImmuneDecreaseHP" or "BlockTargeting" or "Invincible":
                    immune = true;
                    break;
            }
        }
        return new CombatFlags(held, immune, silence, Math.Min(10_000, slow));
    }

    static IReadOnlyList<Row> Rows(Dictionary<string, int[][]>? table, int skillId)
    {
        if (table is null) return [];
        foreach (var key in SkillSideData.LookupKeys(skillId))
            if (TryRows(table, key, out var rows)) return rows;
        return [];
    }

    static bool TryRows(Dictionary<string, int[][]> table, int skillId, out IReadOnlyList<Row> parsed)
    {
        parsed = [];
        if (!table.TryGetValue(Key(skillId), out var rows) || rows is null) return false;
        parsed = rows.Where(r => r.Length >= 5 && r[0] > 0).Select(r =>
            new Row(r[0], r[1], r[2], Math.Clamp(r[3], 1, 255), r[4], r.Length > 5 ? r[5] : 0)).ToList();
        return true;
    }

    static string Key(int id) => id.ToString(CultureInfo.InvariantCulture);
}
