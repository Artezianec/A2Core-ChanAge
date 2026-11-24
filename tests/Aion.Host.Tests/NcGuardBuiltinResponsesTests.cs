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
}