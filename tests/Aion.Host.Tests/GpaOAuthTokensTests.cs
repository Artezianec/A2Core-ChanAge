using System.Text;
using System.Text.Json;
using Aion.Host.NcGuard;

namespace Aion.Host.Tests;

public sealed class GpaOAuthTokensTests
{
    [Fact]
    public void Token_response_uses_jwt_access_and_refresh_with_matching_expires_in()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        using var doc = JsonDocument.Parse(GpaOAuthTokens.TokenResponse(now, "client_credentials"));
        var root = doc.RootElement;
        Assert.Equal("Bearer", root.GetProperty("token_type").GetString());
        Assert.Equal(GpaOAuthTokens.DefaultLifetimeSeconds, root.GetProperty("expires_in").GetInt32());
        var access = root.GetProperty("access_token").GetString()!;
        Assert.Equal(2, access.Count(c => c == '.'));
        var refresh = root.GetProperty("refresh_token").GetString()!;
        Assert.Equal(2, refresh.Count(c => c == '.'));
        Assert.NotEqual(access, refresh);
        var payload = JsonDocument.Parse(Base64Url(access.Split('.')[1])).RootElement;
        Assert.Equal(now.ToUnixTimeSeconds(), payload.GetProperty("iat").GetInt64());
        Assert.Equal(now.ToUnixTimeSeconds() + GpaOAuthTokens.DefaultLifetimeSeconds, payload.GetProperty("exp").GetInt64());
    }

    [Fact]
    public void Grant_type_parser_reads_form_without_logging_secrets()
    {
        var body = Encoding.UTF8.GetBytes("grant_type=refresh_token&refresh_token=secret&client_secret=x");
        Assert.Equal("refresh_token", GpaOAuthTokens.ParseGrantType(body));
    }

    [Fact]
    public void OAuth_paths_match_authz_and_authn_only()
    {
        Assert.True(GpaOAuthTokens.IsOAuthTokenPath("sdk.gpa.ncsoft.com", "POST", "/authz/token"));
        Assert.True(GpaOAuthTokens.IsOAuthTokenPath("sdk.gpa.ncsoft.com", "POST", "/authn/token"));
        Assert.False(GpaOAuthTokens.IsOAuthTokenPath("sdk.gpa.ncsoft.com", "POST", "/api/device/data"));
    }

    static byte[] Base64Url(string data)
    {
        var padded = data.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}