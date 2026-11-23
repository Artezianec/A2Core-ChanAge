using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>WaveStepData-driven wave parts (tools/data/client-waves.json, tools/wave_data.py).</summary>
public sealed class ClientWaves
{
    sealed record File(
        [property: JsonPropertyName("maps")] Dictionary<string, Dictionary<string, string[]>> Maps);

    readonly Dictionary<int, Dictionary<string, string[]>> byMap;

    ClientWaves(File file)
    {
        byMap = file.Maps.ToDictionary(
            p => int.Parse(p.Key, CultureInfo.InvariantCulture),
            p => new Dictionary<string, string[]>(p.Value, StringComparer.OrdinalIgnoreCase),
            EqualityComparer<int>.Default);
    }

    public static ClientWaves Parse(string json)
    {
        var file = JsonSerializer.Deserialize<File>(json) ?? throw new InvalidOperationException("Empty client-waves.json");
        return new(file);
    }

    static readonly Lazy<ClientWaves> embedded = new(() =>
    {
        using var stream = typeof(ClientWaves).Assembly.GetManifestResourceStream("client-waves.json")
            ?? throw new InvalidOperationException("client-waves.json is not embedded");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    public static ClientWaves Embedded => embedded.Value;

    /// <summary>Wave names on this map that have WaveStepData parts in client-waves.json.</summary>
    public IEnumerable<string> NamesOnMap(int map) =>
        byMap.TryGetValue(map, out var waves) ? waves.Keys : [];

    /// <summary>Ordered spawner parts for a wave on this map, or false when unknown.</summary>
    public bool TryGetParts(int map, string wave, out string[] parts)
    {
        parts = [];
        if (!byMap.TryGetValue(map, out var waves) || !waves.TryGetValue(wave, out var list) || list.Length == 0)
            return false;
        parts = list;
        return true;
    }

    public IReadOnlyDictionary<string, string[]> WavesOnMap(int map) =>
        byMap.TryGetValue(map, out var waves) ? waves : new Dictionary<string, string[]>();
}