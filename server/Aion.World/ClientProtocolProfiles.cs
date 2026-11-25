using Aion.Core;

namespace Aion.World;

/// <summary>
/// Selects opcode / wire layouts per launcher client version. Unverified profiles reuse the pinned build.
/// </summary>
public static class ClientProtocolProfiles
{
    public const string PinnedProtocolId = "26262663";

    public static string ActiveProtocolProfileId(string? selectedClientVersion) =>
        ClientBuildProfiles.ProtocolProfileId(selectedClientVersion);

    public static bool IsProtocolVerified(string? selectedClientVersion) =>
        ClientBuildProfiles.ProtocolIsVerified(selectedClientVersion);
}