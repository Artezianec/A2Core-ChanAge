using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.Launcher;

/// <summary>
/// The server this launcher plays on (embedded launcher.json; a launcher.json next to the exe overrides it).
/// </summary>
public sealed record LauncherConfig(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("accountUrl")] string AccountUrl,
    [property: JsonPropertyName("serverAddress")] string ServerAddress,
    [property: JsonPropertyName("bootstrapPort")] int BootstrapPort,
    [property: JsonPropertyName("savedDirSuffix")] string SavedDirSuffix,
    [property: JsonPropertyName("certificateThumbprint")] string? CertificateThumbprint = null,
    [property: JsonPropertyName("clientVersions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ClientVersion[]? ClientVersions = null,
    [property: JsonPropertyName("selectedClientVersion"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SelectedClientVersion = null,
    [property: JsonPropertyName("steamClientPath"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SteamClientPath = null,
    [property: JsonPropertyName("redirectMode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RedirectMode = null)
{
    /// <summary>Embedded launcher.json (the default server baked into this build).</summary>
    public static LauncherConfig Load()
    {
        using var stream = typeof(LauncherConfig).Assembly.GetManifestResourceStream("launcher.json")
            ?? throw new InvalidOperationException("launcher.json is not embedded");
        var config = JsonSerializer.Deserialize<LauncherConfig>(stream) ?? throw new InvalidDataException("launcher.json");
        config.Validate();
        return config;
    }

    /// <summary>A launcher.json on disk (download ZIP sidecar). Derives savedDirSuffix when omitted.</summary>
    public static LauncherConfig Load(string path)
    {
        var config = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(path)) ?? throw new InvalidDataException("launcher.json");
        if (string.IsNullOrEmpty(config.SavedDirSuffix) && Uri.TryCreate(config.AccountUrl, UriKind.Absolute, out var account))
            config = config with { SavedDirSuffix = ServerSetup.DerivedSuffix(account) };
        config.Validate();
        return config;
    }

    /// <summary>launcher.json next to the exe; null when absent. Invalid file throws.</summary>
    public static LauncherConfig? LoadDefault(string path) => File.Exists(path) ? Load(path) : null;

    [JsonIgnore] public IPAddress Server => IPAddress.Parse(ServerAddress);

    public void Validate()
    {
        if (!IPAddress.TryParse(ServerAddress, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidDataException("serverAddress must be an IPv4 address");
        if (!Uri.TryCreate(AccountUrl, UriKind.Absolute, out var account) || !IsValidAccountUrl(account))
            throw new InvalidDataException("accountUrl must be an https URL");
        if (BootstrapPort is < 1 or > 65535) throw new InvalidDataException("bootstrapPort");
        if (SavedDirSuffix.Length is < 1 or > 32 || !SavedDirSuffix.All(char.IsAsciiLetterOrDigit))
            throw new InvalidDataException("savedDirSuffix must be 1-32 letters or digits");
        if (SavedDirSuffix.Equals("Steam", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("savedDirSuffix must not be the official game's (Steam)");
        if (CertificateThumbprint is { } pin && (pin.Length != 40 || !pin.All(char.IsAsciiHexDigit)))
            throw new InvalidDataException("certificateThumbprint must be 40 hex characters");
        foreach (var version in ClientVersions ?? [])
        {
            if (string.IsNullOrWhiteSpace(version.Id) || string.IsNullOrWhiteSpace(version.Name))
                throw new InvalidDataException("clientVersions entries need an id and a name");
            if (!version.Kind.Equals(ClientVersion.PrivateKind, StringComparison.OrdinalIgnoreCase) && !version.IsSteam)
                throw new InvalidDataException("clientVersions kind must be private or steam");
        }
        if (RedirectMode is { } mode && !mode.Equals(RedirectModeSelection.Inject, StringComparison.OrdinalIgnoreCase)
            && !mode.Equals(RedirectModeSelection.Hosts, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("redirectMode must be inject or hosts");
    }

    /// <summary>The bootstrap URL for one launch: the launch authorization is the code in the path.</summary>
    public string BootstrapUrl(string authorization) => $"http://{ServerAddress}:{BootstrapPort}/c/{authorization}";

    /// <summary>HTTPS everywhere; plain HTTP only for loopback account hosts (local Aion.Host without TLS).</summary>
    static bool IsValidAccountUrl(Uri account)
    {
        if (account.Scheme == Uri.UriSchemeHttps) return true;
        if (account.Scheme != Uri.UriSchemeHttp) return false;
        return IsLoopbackAccountHost(account.Host);
    }

    public static bool IsLoopbackAccountHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }
}
