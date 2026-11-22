using System.Net;
using System.Security.Cryptography;

namespace Aion.Host.Native;

/// <summary>
/// Optional native test clients (tools/Aion.HeadlessClient). Off by default; when enabled, loopback
/// peers with a configured shared secret authenticate without platform tickets. Production account
/// logins are unchanged when this stays disabled.
/// </summary>
public sealed record HeadlessClientOptions
{
    public bool Enabled { get; init; }
    public string SharedSecret { get; init; } = "";
    /// <summary>Exact match or CIDR (e.g. 10.0.0.0/8). Empty defaults to loopback only.</summary>
    public string[] AllowedNetworks { get; init; } = ["127.0.0.1", "::1"];

    public bool Allows(IPAddress? address)
    {
        if (address is null) return false;
        if (AllowedNetworks is not { Length: > 0 }) return IPAddress.IsLoopback(address);
        foreach (var rule in AllowedNetworks)
        {
            if (string.IsNullOrWhiteSpace(rule)) continue;
            if (rule.Contains('/'))
            {
                if (CidrContains(rule, address)) return true;
            }
            else if (IPAddress.TryParse(rule, out var allowed) && address.Equals(allowed))
                return true;
        }
        return false;
    }

    public bool AcceptsToken(string authnToken)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(SharedSecret)) return false;
        if (!authnToken.StartsWith("headless:", StringComparison.Ordinal)) return false;
        var parts = authnToken.Split(':', 3);
        if (parts.Length != 3) return false;
        if (parts[1].Length is < 8 or > 64) return false;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(parts[1]),
            System.Text.Encoding.UTF8.GetBytes(SharedSecret))
            && int.TryParse(parts[2], out var index) && index is >= 0 and < 10_000;
    }

    public string WireAccountId(string authnToken)
    {
        var index = int.Parse(authnToken.Split(':', 3)[2], System.Globalization.CultureInfo.InvariantCulture);
        return (9_000_000 + index).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    static bool CidrContains(string cidr, IPAddress address)
    {
        var split = cidr.Split('/', 2);
        if (!IPAddress.TryParse(split[0], out var network)) return false;
        if (!int.TryParse(split.Length > 1 ? split[1] : "32", out var prefix)) return false;
        var networkBytes = network.GetAddressBytes();
        var addressBytes = address.GetAddressBytes();
        if (networkBytes.Length != addressBytes.Length) return false;
        var fullBytes = prefix / 8;
        var remainingBits = prefix % 8;
        for (var i = 0; i < fullBytes; i++)
            if (networkBytes[i] != addressBytes[i]) return false;
        if (remainingBits == 0 || fullBytes >= networkBytes.Length) return true;
        var mask = (byte)(0xff << (8 - remainingBits));
        return (networkBytes[fullBytes] & mask) == (addressBytes[fullBytes] & mask);
    }
}