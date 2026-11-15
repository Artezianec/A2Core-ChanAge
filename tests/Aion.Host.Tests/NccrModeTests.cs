using Aion.Host.Platform;
using Xunit;

namespace Aion.Host.Tests;

public sealed class NccrModeTests
{
    [Theory]
    [InlineData(null, NccrMode.NotFound)]
    [InlineData("", NccrMode.NotFound)]
    [InlineData("notfound", NccrMode.NotFound)]
    [InlineData("Json", NccrMode.Json)]
    [InlineData("json", NccrMode.Json)]
    [InlineData("Empty200", NccrMode.Empty200)]
    [InlineData("empty200", NccrMode.Empty200)]
    public void ParseNccrMode_defaults_to_NotFound(string? value, NccrMode expected) =>
        Assert.Equal(expected, PlatformBootstrapEndpoints.ParseNccrMode(value));

    [Fact]
    public void NccrReply_NotFound_is_404_text_plain()
    {
        var (status, contentType, body) = PlatformBootstrapEndpoints.NccrReply(NccrMode.NotFound, "/cr/1/app/properties");
        Assert.Equal(404, status);
        Assert.Equal("text/plain", contentType);
        Assert.Empty(body);
    }

    [Fact]
    public void NccrReply_Empty200_is_empty()
    {
        var (status, contentType, body) = PlatformBootstrapEndpoints.NccrReply(NccrMode.Empty200, "/cr/1/run-info");
        Assert.Equal(200, status);
        Assert.Equal("text/plain", contentType);
        Assert.Empty(body);
    }

    [Fact]
    public void NccrReply_Json_matches_official_shapes()
    {
        var properties = PlatformBootstrapEndpoints.NccrReply(NccrMode.Json, "/cr/1/app/properties");
        Assert.Equal(200, properties.Status);
        Assert.Equal("application/json; charset=utf-8", properties.ContentType);
        Assert.Contains("samplingRate", System.Text.Encoding.UTF8.GetString(properties.Body));

        var runInfo = PlatformBootstrapEndpoints.NccrReply(NccrMode.Json, "/cr/1/run-info");
        Assert.Equal("""{"result":"success"}""", System.Text.Encoding.UTF8.GetString(runInfo.Body));

        var other = PlatformBootstrapEndpoints.NccrReply(NccrMode.Json, "/cr/other");
        Assert.Equal("{}", System.Text.Encoding.UTF8.GetString(other.Body));
    }
}
