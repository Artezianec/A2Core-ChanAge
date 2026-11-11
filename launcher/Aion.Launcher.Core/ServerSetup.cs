// Server discovery adapted from KiskEmu (AGPL-3.0, https://github.com/Doxylamin/KiskEmu).
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Aion.Launcher;

/// <summary>What a server's GET /api/launcher/config returns (Aion.Host LauncherEndpoints).</summary>
public sealed record ServerReply(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("serverAddress")] string ServerAddress,
    [property: JsonPropertyName("bootstrapPort")] int BootstrapPort,
    [property: JsonPropertyName("savedDirSuffix")] string? SavedDirSuffix = null,
    [property: JsonPropertyName("clientVersions")] ClientVersion[]? ClientVersions = null,
    [property: JsonPropertyName("selectedClientVersion")] string? SelectedClientVersion = null);

/// <summary>
/// A server found at an address the player typed (or an a2corelauncher:// link pre-filled).
/// <paramref name="UntrustedThumbprint"/>: the certificate is not trusted by Windows; the player confirms before pinning.
/// </summary>
public sealed record ServerProbe(Uri AccountUrl, ServerReply Reply, string? UntrustedThumbprint);

public static class ServerSetup
{
    public const string ConfigPath = "/api/launcher/config";
    public const string LinkScheme = "a2corelauncher";

    /// <summary>
    /// Account site address from what the player typed. HTTPS is required except loopback HTTP (local Aion.Host).
    /// Bare host and host:port try port 8780 when no port was given (<see cref="Candidates"/>).
    /// </summary>
    public static Uri Normalize(string text)
    {
        text = text.Trim().TrimEnd('/');
        if (text.Length == 0) throw new InvalidDataException(LauncherLoc.ServerEnterAddress);
        var hadScheme = text.Contains("://", StringComparison.Ordinal);
        if (!hadScheme) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            throw new InvalidDataException(LauncherLoc.ServerBadAddress);
        if (uri.UserInfo.Length > 0) throw new InvalidDataException(LauncherLoc.ServerNoUserInUrl);
        if (uri.Scheme == Uri.UriSchemeHttps)
            return new UriBuilder(Uri.UriSchemeHttps, uri.Host, uri.Port).Uri;
        if (uri.Scheme == Uri.UriSchemeHttp && LauncherConfig.IsLoopbackAccountHost(uri.Host))
            return new UriBuilder(Uri.UriSchemeHttp, uri.Host, uri.Port).Uri;
        throw new InvalidDataException(LauncherLoc.ServerHttpsRequired);
    }

    /// <summary>The addresses to try: as typed, then HTTPS on 8780 when no port was typed.</summary>
    public static IReadOnlyList<Uri> Candidates(string text)
    {
        var uri = Normalize(text);
        var typedPort = text.Contains("://", StringComparison.Ordinal) ? text.Split("://", 2)[1] : text;
        var hostPart = typedPort.Split('/')[0];
        var hasPort = (hostPart.Contains(':', StringComparison.Ordinal) && !hostPart.StartsWith('['))
            || (hostPart.StartsWith('[') && hostPart.Contains("]:", StringComparison.Ordinal));
        if (uri.Scheme == Uri.UriSchemeHttp || hasPort || uri.Port == 8780) return [uri];
        return [uri, new UriBuilder(Uri.UriSchemeHttps, uri.Host, 8780).Uri];
    }

    /// <summary>a2corelauncher://add?url=... carries the address; null for any other link.</summary>
    public static string? AddressFromLink(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || !uri.Scheme.Equals(LinkScheme, StringComparison.OrdinalIgnoreCase)) return null;
        if (!uri.Host.Equals("add", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == "url") return Uri.UnescapeDataString(parts[1]);
        }
        return null;
    }

    /// <summary>
    /// Asks the address for public launcher settings. Untrusted certificates are still read (thumbprint returned);
    /// nothing is sent except the public config request.
    /// </summary>
    public static async Task<ServerProbe> ProbeAsync(string text, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var candidate in Candidates(text))
        {
            string? thumbprint = null;
            var untrusted = false;
            using var own = handler is null ? new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                {
                    thumbprint = certificate?.Thumbprint;
                    untrusted = errors != SslPolicyErrors.None;
                    return certificate is not null;
                },
            } : null;
            using var http = new HttpClient(handler ?? own!, disposeHandler: false) { BaseAddress = candidate, Timeout = TimeSpan.FromSeconds(15) };
            try
            {
                using var response = await http.GetAsync(ConfigPath, ct);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new AccountException(LauncherLoc.ServerNoLauncherApi);
                if (!response.IsSuccessStatusCode) throw new AccountException(LauncherLoc.ErrAccountServerCode((int)response.StatusCode));
                var reply = await response.Content.ReadFromJsonAsync<ServerReply>(ct) ?? throw new AccountException(LauncherLoc.ServerNoSettings);
                Apply(null, candidate, reply, null);
                return new ServerProbe(candidate, reply, untrusted ? thumbprint : null);
            }
            catch (HttpRequestException error) { last = new AccountException(LauncherLoc.ServerReachFailed(candidate.Authority, error.Message)); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { last = new AccountException(LauncherLoc.ServerTimeout(candidate.Authority)); }
            catch (System.Text.Json.JsonException) { last = new AccountException(LauncherLoc.ServerNotA2Core(candidate.Authority)); }
            catch (InvalidDataException error) { last = new AccountException(LauncherLoc.ServerBadSettings(error.Message)); }
        }
        throw last ?? new AccountException(LauncherLoc.ServerUnreachable);
    }

    /// <summary>
    /// Server settings applied to what the launcher keeps: address, port, versions and name follow the server;
    /// savedDirSuffix and certificate pin stay as first set.
    /// </summary>
    public static LauncherConfig Apply(LauncherConfig? kept, Uri accountUrl, ServerReply reply, string? pin)
    {
        var name = reply.Name?.Trim() is { Length: > 0 } n ? n[..Math.Min(n.Length, 40)] : accountUrl.Host;
        var authority = accountUrl.GetLeftPart(UriPartial.Authority);
        var config = new LauncherConfig(name, authority, reply.ServerAddress, reply.BootstrapPort,
            kept?.SavedDirSuffix ?? (reply.SavedDirSuffix is { Length: > 0 } suffix ? suffix : DerivedSuffix(accountUrl)),
            kept?.CertificateThumbprint ?? pin,
            reply.ClientVersions is { Length: > 0 } versions ? versions : kept?.ClientVersions,
            reply.SelectedClientVersion ?? kept?.SelectedClientVersion,
            kept?.SteamClientPath,
            kept?.RedirectMode);
        config.Validate();
        return config;
    }

    /// <summary>A saved-folder suffix of 32 letters and digits from the account address when the server sets none.</summary>
    public static string DerivedSuffix(Uri accountUrl)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("a2corelauncher:" + accountUrl.GetLeftPart(UriPartial.Authority).ToLowerInvariant()));
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return new string(hash.Take(32).Select(b => alphabet[b % alphabet.Length]).ToArray());
    }

    public static string Readable(string thumbprint) =>
        string.Join(":", Enumerable.Range(0, thumbprint.Length / 2).Select(i => thumbprint.Substring(i * 2, 2))).ToUpperInvariant();
}
