using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>Reshanta abyss rift phase scripts (tools/data/abyss-rift-phases.json, tools/abyss_rift_phases.py).</summary>
public sealed class AbyssRiftPhases
{
    public sealed record PhaseEnd(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("value")] string Value,
        [property: JsonPropertyName("message_key")] string? MessageKey);

    public sealed record Phase(
        [property: JsonPropertyName("phase")] int Index,
        [property: JsonPropertyName("spawner_group")] string? SpawnerGroup,
        [property: JsonPropertyName("spawners")] string[] Spawners,
        [property: JsonPropertyName("ends")] PhaseEnd[] Ends,
        [property: JsonPropertyName("abnormal_ids")] int[] AbnormalIds);

    sealed record File([property: JsonPropertyName("rifts")] Dictionary<string, Phase[]> Rifts);

    readonly Dictionary<int, Phase[]> byRift;

    AbyssRiftPhases(File file) =>
        byRift = file.Rifts.ToDictionary(p => int.Parse(p.Key, CultureInfo.InvariantCulture), p => p.Value);

    public static AbyssRiftPhases Parse(string json)
    {
        var file = JsonSerializer.Deserialize<File>(json) ?? throw new InvalidOperationException("Empty abyss-rift-phases.json");
        return new(file);
    }

    static readonly Lazy<AbyssRiftPhases> embedded = new(() =>
    {
        using var stream = typeof(AbyssRiftPhases).Assembly.GetManifestResourceStream("abyss-rift-phases.json")
            ?? throw new InvalidOperationException("abyss-rift-phases.json is not embedded");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    public static AbyssRiftPhases Embedded => embedded.Value;

    public Phase[]? PhasesOf(int abyssRiftId) => byRift.GetValueOrDefault(abyssRiftId);
    public int PhaseCount(int abyssRiftId) => PhasesOf(abyssRiftId)?.Length ?? 0;
}