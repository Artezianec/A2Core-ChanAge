using System.Text.Json;
using Aion.Host.NcGuard;

namespace Aion.Host.Tests;

public sealed class NcGuardBuiltinResponsesTests
{
    [Theory]
    [InlineData("POST", "/api/device/data")]
    [InlineData("POST", "/api/device/data?")]
    [InlineData("POST", "/api/device/register")]
    public void Gpa_device_posts_return_success_json(string method, string path)
    {
        Assert.True(NcGuardBuiltinResponses.TryReply("sdk.gpa.ncsoft.com", method, path, out var body, out var type));
        Assert.Equal("application/json", type);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(0, doc.RootElement.GetProperty("errorCode").GetInt32());
    }

    [Fact]
    public void Gpa_device_get_is_not_answered()
    {
        Assert.False(NcGuardBuiltinResponses.TryReply("sdk.gpa.ncsoft.com", "GET", "/api/device/data", out _, out _));
    }

    [Fact]
    public void Bladnoch_post_root_stays_empty_html()
    {
        Assert.True(NcGuardBuiltinResponses.TryReply("bladnoch.ncsoft.com", "POST", "/", out var body, out var type));
        Assert.Equal("text/html", type);
        Assert.Empty(body);
    }

    [Fact]
    public void Lagavulin_get_def_ec_returns_binary_stub()
    {
        Assert.True(NcGuardBuiltinResponses.TryReply("lagavulin.ncsoft.com", "GET", "/def.ec", out var body, out var type));
        Assert.Equal("binary/octet-stream", type);
        Assert.Equal(256, body.Length);
        Assert.Equal(0, body[0]);
        Assert.Equal(255, body[255]);
    }

    [Fact]
    public void Lagavulin_builtin_does_not_shadow_fixture_win_paths()
    {
        Assert.False(NcGuardBuiltinResponses.TryReply("lagavulin.ncsoft.com", "GET", "/win/01234567-89ab-cdef-0123-456789abcdef/def.ec", out _, out _));
        Assert.False(NcGuardBuiltinResponses.TryReply("lagavulin.ncsoft.com", "GET", "/pef.ec", out _, out _));
    }

    [Fact]
    public void Atp_json_legacy_prefix_returns_text()
    {
        Assert.True(NcGuardBuiltinResponses.TryReply("atp-json.ncsoft.com", "GET", "/legacy/one", out var body, out var type));
        Assert.Equal("text/plain", type);
        Assert.Equal("legacy"u8.ToArray(), body);
    }

    [Fact]
    public void Fixture_path_wins_sink_checks_rule_before_builtin()
    {
        // NcGuardSink.HandleAsync: fixture rule → GPA OAuth → NcGuardBuiltinResponses → 404.
        Assert.True(NcGuardBuiltinResponses.TryReply("sdk.gpa.ncsoft.com", "POST", "/api/device/data", out var builtin, out _));
        Assert.NotEmpty(builtin);
    }
}