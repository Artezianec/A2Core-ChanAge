using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// Client NpcBehaviorCase rows (tools/npc_behavior_cases.py -> npc-behavior-cases.json): combat barks keyed by
/// spawn name and NpcData id; GimmickGauge thresholds; MonsterPartsData breakable parts.
/// </summary>
public static class NpcBehaviorCases
{
    public sealed record CaseEntry(
        [property: JsonPropertyName("case")] string Case,
        [property: JsonPropertyName("value")] string Value,
        [property: JsonPropertyName("prob")] int Prob,
        [property: JsonPropertyName("talk")] string Talk,
        [property: JsonPropertyName("skill")] int? Skill = null,
        [property: JsonPropertyName("abnormal")] int? Abnormal = null);

    public sealed record MonsterPartEntry(
        [property: JsonPropertyName("parts_id")] int PartsId,
        [property: JsonPropertyName("max_hp")] int MaxHp,
        [property: JsonPropertyName("desc_key")] string? DescKey);

    public sealed record GimmickEntry(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("show")] string? Show,
        [property: JsonPropertyName("thresholds")] int[] Thresholds);

    sealed record File(
        [property: JsonPropertyName("by_spawn_name")] Dictionary<string, List<CaseEntry>>? BySpawn,
        [property: JsonPropertyName("by_npc_id")] Dictionary<string, List<CaseEntry>>? ByNpc,
        [property: JsonPropertyName("monster_parts")] Dictionary<string, List<MonsterPartEntry>>? Parts,
        [property: JsonPropertyName("gimmick_gauges")] Dictionary<string, GimmickEntry>? Gimmicks);

    public static NpcBehaviorCasesIndex Embedded { get; } = Load();

    static NpcBehaviorCasesIndex Load()
    {
        using var stream = typeof(NpcBehaviorCases).Assembly.GetManifestResourceStream("npc-behavior-cases.json")
            ?? throw new InvalidOperationException("Missing embedded npc-behavior-cases.json");
        var file = JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("npc-behavior-cases.json");
        return new NpcBehaviorCasesIndex(
            file.BySpawn ?? new(StringComparer.OrdinalIgnoreCase),
            file.ByNpc ?? new(),
            file.Parts ?? new(),
            file.Gimmicks ?? new());
    }
}

public sealed class NpcBehaviorCasesIndex
{
    readonly Dictionary<string, List<NpcBehaviorCases.CaseEntry>> bySpawn;
    readonly Dictionary<string, List<NpcBehaviorCases.CaseEntry>> byNpc;
    readonly Dictionary<string, List<NpcBehaviorCases.MonsterPartEntry>> parts;
    readonly Dictionary<string, NpcBehaviorCases.GimmickEntry> gimmicks;

    public NpcBehaviorCasesIndex(
        Dictionary<string, List<NpcBehaviorCases.CaseEntry>> bySpawn,
        Dictionary<string, List<NpcBehaviorCases.CaseEntry>> byNpc,
        Dictionary<string, List<NpcBehaviorCases.MonsterPartEntry>> parts,
        Dictionary<string, NpcBehaviorCases.GimmickEntry> gimmicks)
    {
        this.bySpawn = bySpawn;
        this.byNpc = byNpc;
        this.parts = parts;
        this.gimmicks = gimmicks;
    }

    public IReadOnlyList<NpcBehaviorCases.CaseEntry> For(Target target) =>
        (bySpawn.TryGetValue(target.Name, out var spawn) ? spawn : null)
        ?? (byNpc.TryGetValue(target.NpcId.ToString(), out var byId) ? byId : []);

    public IReadOnlyList<NpcBehaviorCases.CaseEntry> Matching(Target target, string caseKind, int? skill = null, int? abnormal = null) =>
        For(target).Where(c => string.Equals(c.Case, caseKind, StringComparison.OrdinalIgnoreCase)
            && (skill is null || c.Skill == skill)
            && (abnormal is null || c.Abnormal == abnormal)).ToList();

    public IReadOnlyList<NpcBehaviorCases.MonsterPartEntry> PartsFor(int npcId) =>
        parts.TryGetValue(npcId.ToString(), out var list) ? list : [];

    public NpcBehaviorCases.GimmickEntry? GimmickFor(int gaugeId) =>
        gimmicks.TryGetValue(gaugeId.ToString(), out var g) ? g : null;
}