namespace Aion.Core;

/// <summary>
/// Server-wide client protocol selection (handshake, framing, login parsing, opcode profile).
/// Config: <c>Aion:ClientProtocol</c> or <c>Aion:ClientBuild</c>; env <c>AION_CLIENT_PROTOCOL</c> overrides.
/// </summary>
public sealed record ClientProtocolSettings
{
    public const string DefaultToken = "2.0.9";

    public required ClientProtocolKind Kind { get; init; }
    /// <summary>Launcher / profile id (<see cref="ClientBuildProfiles"/>).</summary>
    public required string VersionId { get; init; }
  public required string ProtocolProfileId { get; init; }

    public static ClientProtocolSettings Default => ForKind(ClientProtocolKind.V209);

    public static ClientProtocolSettings Parse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return Default;
        var id = token.Trim().ToLowerInvariant() switch
        {
            "2.0.9" or "2.0.9.0" or "209" or "26262663" => ClientProtocolKind.V209,
            "2.0.12" or "2.0.12.0" or "2012" or "26728906" => ClientProtocolKind.V2012,
            "2.0.13" or "2013" => ClientProtocolKind.V2013,
            "auto" => ClientProtocolKind.Auto,
            _ => ClientProtocolKind.V209,
        };
        return ForKind(id);
    }

    static ClientProtocolSettings ForKind(ClientProtocolKind kind) => kind switch
    {
        ClientProtocolKind.V209 => new ClientProtocolSettings
        {
            Kind = kind,
            VersionId = ClientBuildProfiles.DefaultVersionId,
            ProtocolProfileId = ClientBuildProfiles.Default.ProtocolProfileId,
        },
        ClientProtocolKind.V2012 => new ClientProtocolSettings
        {
            Kind = kind,
            VersionId = "2.0.12.0",
            ProtocolProfileId = "26728906",
        },
        ClientProtocolKind.V2013 => new ClientProtocolSettings
        {
            Kind = kind,
            VersionId = ClientBuildProfiles.ExperimentalVersionId,
            ProtocolProfileId = ClientBuildProfiles.Get(ClientBuildProfiles.ExperimentalVersionId).ProtocolProfileId,
        },
        ClientProtocolKind.Auto => Default with { Kind = ClientProtocolKind.Auto },
        _ => Default,
    };

    public bool IsPinned209 => Kind == ClientProtocolKind.V209;
    public bool UsesModernGameHandshake => Kind is ClientProtocolKind.V2012 or ClientProtocolKind.V2013 or ClientProtocolKind.Auto;
    public bool PreferClearOpcodeFraming => Kind == ClientProtocolKind.V2012;
    public bool AcceptsVarintNpLogin => Kind is ClientProtocolKind.V2012 or ClientProtocolKind.V2013 or ClientProtocolKind.Auto;
    public bool Uses2012WorldEntryOpcodes => ProtocolProfileId == "26728906";
}

public enum ClientProtocolKind
{
    V209,
    V2012,
    V2013,
    Auto,
}

/// <summary>Process-wide active settings, set once at host startup.</summary>
public static class ClientProtocolRuntime
{
    static ClientProtocolSettings active = ClientProtocolSettings.Default;

    public static ClientProtocolSettings Current => active;

    public static void Initialize(ClientProtocolSettings settings) => active = settings;
}