using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.Core;

/// <summary>
/// Known AION2 client builds for launcher selection, data export (pak AES), and dumper offsets.
/// Source of truth: tools/data/client-build-profiles.json (embedded in Aion.Core).
/// </summary>
public static class ClientBuildProfiles
{
    public const string DefaultVersionId = "2.0.9.0";
    public const string ExperimentalVersionId = "2.0.13";

    public sealed record DumperOffsets(
        [property: JsonPropertyName("FNamePool")] string? FNamePool,
        [property: JsonPropertyName("GUObjectArray")] string? GUObjectArray);

    public sealed record Profile(
        [property: JsonPropertyName("displayVersion")] string DisplayVersion,
        [property: JsonPropertyName("steamBuildId")] string? SteamBuildId,
        [property: JsonPropertyName("engineVersion")] string? EngineVersion,
        [property: JsonPropertyName("pinnedDefault")] bool PinnedDefault,
        [property: JsonPropertyName("aesPakKeyHex")] string? AesPakKeyHex,
        [property: JsonPropertyName("dumperOffsets")] DumperOffsets? DumperOffsets,
        [property: JsonPropertyName("protocolProfileId")] string ProtocolProfileId,
        [property: JsonPropertyName("protocolVerified")] bool ProtocolVerified,
        [property: JsonPropertyName("steamBuildIdNote")] string? SteamBuildIdNote = null);

    sealed record Table(
        [property: JsonPropertyName("defaultVersionId")] string DefaultVersionId,
        [property: JsonPropertyName("profiles")] Dictionary<string, Profile> Profiles);

    static readonly Lazy<Table> Loaded = new(Load);

    static Table Load()
    {
        using var stream = typeof(ClientBuildProfiles).Assembly.GetManifestResourceStream("client-build-profiles.json")
            ?? throw new InvalidDataException("client-build-profiles.json is not embedded");
        return JsonSerializer.Deserialize<Table>(stream, JsonOptions) ?? throw new InvalidDataException("client-build-profiles.json is empty");
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string DefaultId => Loaded.Value.DefaultVersionId;

    public static IReadOnlyDictionary<string, Profile> All => Loaded.Value.Profiles;

    public static Profile Default => Get(DefaultId);

    public static Profile Get(string? versionId)
    {
        var id = NormalizeId(versionId);
        if (Loaded.Value.Profiles.TryGetValue(id, out var profile)) return profile;
        return Default;
    }

    public static Profile? TryGet(string? versionId)
    {
        var id = versionId?.Trim();
        if (string.IsNullOrEmpty(id)) return null;
        return Loaded.Value.Profiles.TryGetValue(id, out var profile) ? profile : null;
    }

    /// <summary>Maps launcher ids (including legacy aliases) to a profile id.</summary>
    public static string NormalizeId(string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return DefaultId;
        var id = versionId.Trim();
        if (id.Equals("2.0.6.0", StringComparison.OrdinalIgnoreCase)) return DefaultId;
        if (id.Equals("steam-latest", StringComparison.OrdinalIgnoreCase)) return DefaultId;
        if (Loaded.Value.Profiles.ContainsKey(id)) return id;
        return DefaultId;
    }

    /// <summary>Opcode / wire tables to use for a launcher-selected client version.</summary>
    public static string ProtocolProfileId(string? selectedClientVersion) => Get(selectedClientVersion).ProtocolProfileId;

    public static bool ProtocolIsVerified(string? selectedClientVersion) => Get(selectedClientVersion).ProtocolVerified;
}