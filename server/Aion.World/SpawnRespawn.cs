using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// Per-placement field mob respawn delays (tools/spawn_respawn.py -> tools/data/spawn-respawn.json). When missing,
/// <see cref="GameSession.RespawnMs"/> is used (OURS fallback). Field bosses and named elites keep
/// <see cref="FieldBosses"/> / <see cref="FieldBossTimers"/> handling.
/// </summary>
public sealed class SpawnRespawn
{
    sealed record File(
        [property: JsonPropertyName("maps")] Dictionary<string, Dictionary<string, long>>? Maps,
        [property: JsonPropertyName("by_unique_key")] Dictionary<string, Dictionary<string, long>>? ByUniqueKey);

    readonly Dictionary<int, Dictionary<string, long>> byName = [];
    readonly Dictionary<int, Dictionary<uint, long>> byUnique = [];

    SpawnRespawn(File file)
    {
        foreach (var (map, names) in file.Maps ?? [])
            if (int.TryParse(map, out var mapId))
                byName[mapId] = new Dictionary<string, long>(names, StringComparer.Ordinal);
        foreach (var (map, keys) in file.ByUniqueKey ?? [])
            if (int.TryParse(map, out var mapId))
            {
                var table = new Dictionary<uint, long>();
                foreach (var (key, ms) in keys)
                    if (uint.TryParse(key, out var uk)) table[uk] = ms;
                byUnique[mapId] = table;
            }
    }

    public static SpawnRespawn Empty { get; } = new(new File(null, null));

    public static SpawnRespawn LoadEmbedded()
    {
        try
        {
            return new(Load<File>("spawn-respawn.json"));
        }
        catch (InvalidOperationException)
        {
            return Empty;
        }
    }

    static T Load<T>(string name)
    {
        var asm = typeof(GameData).Assembly;
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' not found.");
        return JsonSerializer.Deserialize<T>(stream)
            ?? throw new InvalidOperationException($"Failed to deserialize embedded '{name}'.");
    }

    /// <summary>Client-derived respawn delay in ms, or null to use the session fallback.</summary>
    public long? RespawnMs(int mapId, string spawnName, uint uniqueKey)
    {
        if (byUnique.TryGetValue(mapId, out var keys) && uniqueKey != 0 && keys.TryGetValue(uniqueKey, out var byKey))
            return Math.Max(0, byKey);
        if (byName.TryGetValue(mapId, out var names) && names.TryGetValue(spawnName, out var byNameMs))
            return Math.Max(0, byNameMs);
        return null;
    }
}