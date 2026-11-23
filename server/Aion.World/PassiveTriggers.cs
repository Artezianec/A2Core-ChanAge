using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// Conditional class passives: proc subscriptions (OnHit, OnCrit, OnDamaged, OnHpBelow) that fire skill effect rows.
/// Data: tools/data/passive-triggers.json (improvised until client SkillEffect exports cover passives).
/// </summary>
public static class PassiveTriggers
{
    public enum Kind { OnHit, OnCrit, OnDamaged, OnHpBelow }

    public sealed record Trigger(int SkillId, Kind Kind, int HpPermilleThreshold, IReadOnlyList<string[]> EffectRows);

    sealed record Row(
        [property: JsonPropertyName("on")] string? On,
        [property: JsonPropertyName("hp_permille")] int HpPermille,
        [property: JsonPropertyName("effects")] string[][]? Effects);

    sealed record File([property: JsonPropertyName("triggers")] Dictionary<string, Row[]>? Triggers);

    static readonly Lazy<IReadOnlyList<Trigger>> all = new(Load);

    public static IReadOnlyList<Trigger> All => all.Value;

    static IReadOnlyList<Trigger> Load()
    {
        using var stream = typeof(PassiveTriggers).Assembly.GetManifestResourceStream("passive-triggers.json");
        if (stream is null) return [];
        var file = JsonSerializer.Deserialize<File>(stream) ?? new File(null);
        if (file.Triggers is null) return [];
        var list = new List<Trigger>();
        foreach (var (skillKey, rows) in file.Triggers)
        {
            if (!int.TryParse(skillKey, out var skillId)) continue;
            foreach (var row in rows)
            {
                if (row.Effects is not { Length: > 0 }) continue;
                var kind = row.On?.ToLowerInvariant() switch
                {
                    "onhit" or "hit" => Kind.OnHit,
                    "oncrit" or "crit" => Kind.OnCrit,
                    "ondamaged" or "damaged" => Kind.OnDamaged,
                    "onhpbelow" or "hpbelow" => Kind.OnHpBelow,
                    _ => Kind.OnHit,
                };
                list.Add(new Trigger(skillId, kind, Math.Clamp(row.HpPermille, 0, 10_000), row.Effects));
            }
        }
        return list;
    }

    public static IEnumerable<Trigger> ForSkill(int skillId) => All.Where(t => t.SkillId == skillId);

    public static IEnumerable<Trigger> ForLearned(IEnumerable<int> learned) =>
        All.Where(t => learned.Contains(t.SkillId));
}