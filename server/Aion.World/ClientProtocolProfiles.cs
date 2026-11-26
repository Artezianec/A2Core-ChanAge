using Aion.Core;

namespace Aion.World;

/// <summary>
/// Opcode / wire layouts for the active server client protocol (see <see cref="ClientProtocolSettings"/>).
/// </summary>
public static class ClientProtocolProfiles
{
    public const string PinnedProtocolId = "26262663";
    public const string Build2012ProtocolId = "26728906";

    public static string ActiveProtocolProfileId =>
        ClientProtocolRuntime.Current.ProtocolProfileId;

    public static bool IsProtocolVerified(string? selectedClientVersion) =>
        ClientBuildProfiles.ProtocolIsVerified(selectedClientVersion);

    /// <summary>Legacy alias: launcher-selected version maps to a profile when explicitly overridden in tooling.</summary>
    public static string ProtocolProfileIdForLauncher(string? selectedClientVersion) =>
        ClientBuildProfiles.ProtocolProfileId(selectedClientVersion);
}