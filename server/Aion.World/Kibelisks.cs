using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>A placed Kibelisk (tools/teleport_artifacts.py): its EnvObjData object, the artifact it engraves and where it stands.</summary>
public sealed record KibeliskPlacement(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("env_obj")] int EnvObj,
    [property: JsonPropertyName("artifact")] string Artifact,
    [property: JsonPropertyName("artifact_id")] int ArtifactId,
    [property: JsonPropertyName("unique_key")] int UniqueKey,
    [property: JsonPropertyName("position")] double[] Position,
    [property: JsonPropertyName("yaw")] double Yaw,
    // EnvObjData RelationshipEntity (8 EnvObj_Light, 9 EnvObj_Dark): the client offers the bind only on its race's.
    [property: JsonPropertyName("relationship")] byte Relationship = 8);

/// <summary>Kibelisks by map (embedded teleport-artifacts.json).</summary>
public static class Kibelisks
{
    /// <summary>Entity keys of the spawned Kibelisks: FirstKey + index (NPCs start at 2000).</summary>
    public const int FirstKey = 900_000;
    /// <summary>EnvObjData InteractRange of the Poeta Kibelisk (300), with room for latency.</summary>
    public const double InteractRange = 600;

    sealed record File([property: JsonPropertyName("maps")] Dictionary<string, KibeliskPlacement[]> Maps);

    static readonly Lazy<Dictionary<string, KibeliskPlacement[]>> maps = new(() =>
    {
        using var stream = typeof(Kibelisks).Assembly.GetManifestResourceStream("teleport-artifacts.json")
            ?? throw new InvalidOperationException("teleport-artifacts.json is not embedded");
        return (JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("teleport-artifacts.json")).Maps;
    });

    public static IReadOnlyList<KibeliskPlacement> OnMap(int mapId) =>
        maps.Value.GetValueOrDefault(mapId.ToString(CultureInfo.InvariantCulture)) ?? [];

    static readonly Lazy<Dictionary<int, (int MapId, KibeliskPlacement Placement)>> byArtifact = new(() =>
    {
        var index = new Dictionary<int, (int, KibeliskPlacement)>();
        foreach (var (mapKey, placements) in maps.Value)
        {
            if (!int.TryParse(mapKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mapId)) continue;
            foreach (var placement in placements)
                index[placement.ArtifactId] = (mapId, placement);
        }
        return index;
    });

    /// <summary>World map and stand position for a bound TeleportArtifact id (from teleport-artifacts.json).</summary>
    public static bool TryPlacement(int artifactId, out int mapId, out Vector3d position)
    {
        if (TryGetPlacement(artifactId, out mapId, out var placement))
        {
            position = KibeliskPosition(placement);
            return true;
        }
        mapId = 0;
        position = default;
        return false;
    }

    public static bool TryGetPlacement(int artifactId, out int mapId, out KibeliskPlacement placement)
    {
        if (byArtifact.Value.TryGetValue(artifactId, out var hit))
        {
            mapId = hit.MapId;
            placement = hit.Placement;
            return true;
        }
        mapId = 0;
        placement = null!;
        return false;
    }

    public static Vector3d KibeliskPosition(KibeliskPlacement placement) =>
        new(placement.Position[0], placement.Position[1], placement.Position[2]);

    /// <summary>Map load destination for a bound Kibelisk (named TeleportData point, else the object stand).</summary>
    public static Vector3d ArrivalPoint(int mapId, KibeliskPlacement placement, IReadOnlyDictionary<string, double[]> mapPoints, double nearestRange = 3000)
    {
        foreach (var name in ArrivalPointNames(placement))
        {
            if (mapPoints.TryGetValue(name, out var p) && p.Length >= 3)
                return new Vector3d(p[0], p[1], p[2]);
        }
        var kibelisk = KibeliskPosition(placement);
        var best = kibelisk;
        var bestDistance = nearestRange;
        foreach (var (name, p) in mapPoints)
        {
            if (!name.StartsWith("TeleportArtifact", StringComparison.OrdinalIgnoreCase) || p.Length < 3) continue;
            var d = Math.Sqrt(Math.Pow(p[0] - kibelisk.X, 2) + Math.Pow(p[1] - kibelisk.Y, 2) + Math.Pow(p[2] - kibelisk.Z, 2));
            if (d < bestDistance) { bestDistance = d; best = new Vector3d(p[0], p[1], p[2]); }
        }
        return best;
    }

    static IEnumerable<string> ArrivalPointNames(KibeliskPlacement placement)
    {
        const string envPrefix = "EnvObj_TeleportArtifact_";
        var name = placement.Name;
        if (name.StartsWith(envPrefix, StringComparison.Ordinal))
        {
            var suffix = name[envPrefix.Length..];
            yield return "TeleportArtifact_" + suffix;
            yield return "TeleportArtifact_" + suffix + "_1";
            if (suffix.StartsWith("Altgard_", StringComparison.Ordinal))
            {
                var tail = suffix["Altgard_".Length..];
                yield return "WorldMapTelpo_" + tail;
                yield return "WorldMapTelpo_" + tail + "_1";
            }
        }
        if (!string.IsNullOrEmpty(placement.Artifact))
            yield return placement.Artifact;
    }
}
