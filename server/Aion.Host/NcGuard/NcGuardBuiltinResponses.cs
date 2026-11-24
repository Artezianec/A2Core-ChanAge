using System.Text;
using System.Text.Json;

namespace Aion.Host.NcGuard;

/// <summary>
/// Fallback NCGuard TLS answers when the deployed fixture has no rule (local dev and partial fixtures).
/// Matches the JSON field names used on other GPA OAuth token replies (snake_case access_token, token_type, expires_in).
/// </summary>
public static class NcGuardBuiltinResponses
{
    sealed record BuiltinRule(string Host, string? Method, string? Path, string? PathPrefix, string ContentType, byte[] Body);

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = null };

    static readonly BuiltinRule[] Rules =
    [
        // Observed official: POST / with binary telemetry → 200 text/html, empty body (research/e259-ncguard.md).
        new("bladnoch.ncsoft.com", "POST", "/", null, "text/html", []),
        // authz/authn/token: dynamic JWT replies via <see cref="GpaOAuthTokens"/> in <see cref="NcGuardSink"/>.
        // CANDIDATE: periodic GPA telemetry (~7 min without this → 404 and the client self-disconnects). Shape is
        // inferred from other NC GPA JSON (errorCode 0); refine when an official sdk.gpa capture is in-repo.
        new("sdk.gpa.ncsoft.com", "POST", null, "/api/device/", "application/json", DeviceApiSuccessBody()),
    ];

    public static bool TryReply(string host, string method, string path, out byte[] body, out string contentType)
    {
        foreach (var rule in Rules)
        {
            if (!string.Equals(rule.Host, host, StringComparison.OrdinalIgnoreCase)) continue;
            if (rule.Method is not null && !string.Equals(rule.Method, method, StringComparison.OrdinalIgnoreCase)) continue;
            if (rule.Path is not null && path != rule.Path) continue;
            if (rule.Path is null && rule.PathPrefix is not null && !path.StartsWith(rule.PathPrefix, StringComparison.Ordinal)) continue;
            body = rule.Body;
            contentType = rule.ContentType;
            return true;
        }
        body = [];
        contentType = "text/plain";
        return false;
    }

    static byte[] DeviceApiSuccessBody() => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        errorCode = 0,
        message = "success",
    }, Json));
}
