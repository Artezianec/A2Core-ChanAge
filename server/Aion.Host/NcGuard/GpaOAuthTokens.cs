using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aion.Host.NcGuard;

/// <summary>
/// Synthetic OAuth token replies for <c>sdk.gpa.ncsoft.com</c> <c>/authz/token</c> and <c>/authn/token</c>.
/// The client's GPA SDK parses <c>access_token</c> as a JWT and checks <c>exp</c>/<c>iat</c> (opaque strings
/// correlate with mid-session self-disconnect). Signature verification against NC keys is not assumed on a
/// private sink — HS256 with a local key until an official capture refines claims.
/// </summary>
public static class GpaOAuthTokens
{
    public const int DefaultLifetimeSeconds = 3600;
    static readonly byte[] SigningKey = "aion-local-gpa-hs256-key-v1"u8.ToArray();
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = null };

    public static byte[] TokenResponse(DateTimeOffset now, string? grantType)
    {
        var access = CreateJwt(now, DefaultLifetimeSeconds, "access");
        var refresh = CreateJwt(now, 86_400, "refresh");
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            access_token = access,
            token_type = "Bearer",
            expires_in = DefaultLifetimeSeconds,
            refresh_token = refresh,
            scope = "openid profile",
        }, Json);
    }

    public static bool IsOAuthTokenPath(string host, string method, string path) =>
        string.Equals(host, "sdk.gpa.ncsoft.com", StringComparison.OrdinalIgnoreCase)
        && method.Equals("POST", StringComparison.OrdinalIgnoreCase)
        && path is "/authz/token" or "/authn/token";

    public static string? ParseGrantType(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty) return null;
        foreach (var segment in Encoding.UTF8.GetString(body).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.StartsWith("grant_type=", StringComparison.Ordinal))
                return Uri.UnescapeDataString(segment["grant_type=".Length..]);
        }
        return null;
    }

    static string CreateJwt(DateTimeOffset now, int lifetimeSeconds, string use)
    {
        var iat = now.ToUnixTimeSeconds();
        var exp = iat + lifetimeSeconds;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = "https://sdk.gpa.ncsoft.com/",
            ["sub"] = "aion-local-gpa",
            ["aud"] = "gpa-sdk",
            ["iat"] = iat,
            ["exp"] = exp,
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["token_use"] = use,
        }, Json);
        return SignHs256(payload);
    }

    static string SignHs256(ReadOnlySpan<byte> payloadJson)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }, Json));
        var payload = Base64Url(payloadJson);
        var signingInput = $"{header}.{payload}";
        var sig = HMACSHA256.HashData(SigningKey, Encoding.UTF8.GetBytes(signingInput));
        return $"{signingInput}.{Base64Url(sig)}";
    }

    static string Base64Url(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}