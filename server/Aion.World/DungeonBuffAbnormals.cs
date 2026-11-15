using System.Globalization;
using System.Text.Json;

namespace Aion.World;

// Ported from zaraza87/A2Core dell-merge-2026-11 (29934bea, docs/DELL-CHANGES-2026-11.md): the SkillAbnormal / SkillAbnormalEffect rows
// of the daily dungeons' buffs (DungeonBuff BuffValue1, 19003xxx) from their client abnormal table, so a chosen buff carries its
// StatChange stats (DamageRatio, DefenseRatio, Critical, CombatSpeed, MoveSpeed). The TriggerSkillByKill and AddAbnormalByCondition
// rows are kept but not modelled (those buffs show their icon only). A run keeps one pick (a new pick replaces the old, as the
// client table's single group 19003000 says).
/// <summary>The DungeonBuff abnormals from tools/data/dungeon-buff-abnormals.json.</summary>
public static class DungeonBuffAbnormals
{
    sealed record Row(int Group, string Type, JsonElement[][] Effects);

    static readonly Lazy<Dictionary<int, AbnormalInfo>> rows = new(() =>
    {
        using var stream = typeof(DungeonBuffAbnormals).Assembly.GetManifestResourceStream("dungeon-buff-abnormals.json")
            ?? throw new InvalidOperationException("dungeon-buff-abnormals.json is not embedded");
        using var doc = JsonDocument.Parse(stream);
        var result = new Dictionary<int, AbnormalInfo>();
        foreach (var p in doc.RootElement.GetProperty("abnormals").EnumerateObject())
        {
            var id = int.Parse(p.Name, CultureInfo.InvariantCulture);
            var effects = p.Value.GetProperty("effects").EnumerateArray().Select(e => e.EnumerateArray().ToArray()).Select(e =>
                (e[0].GetInt32(), e[1].GetString()!, (IReadOnlyList<string>)e.Skip(2).Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString()).ToList()))
                .ToList();
            result[id] = new AbnormalInfo(id, effects);
        }
        return result;
    });

    public static AbnormalInfo? Of(int abnormalId) => rows.Value.GetValueOrDefault(abnormalId);
    public static IEnumerable<int> Ids => rows.Value.Keys;
}
