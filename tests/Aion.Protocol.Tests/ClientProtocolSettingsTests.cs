using Aion.Core;
using Xunit;

namespace Aion.Protocol.Tests;

public sealed class ClientProtocolSettingsTests
{
    [Fact]
    public void Default_server_protocol_stays_on_2_0_9()
    {
        var settings = ClientProtocolSettings.Parse(null);
        Assert.Equal(ClientProtocolKind.V209, settings.Kind);
        Assert.Equal(ClientBuildProfiles.DefaultVersionId, settings.VersionId);
        Assert.Equal("26262663", settings.ProtocolProfileId);
        Assert.False(settings.UsesModernGameHandshake);
        Assert.False(settings.PreferClearOpcodeFraming);
    }

    [Theory]
    [InlineData("2.0.12", ClientProtocolKind.V2012, "2.0.12.0", "26728906")]
    [InlineData("2.0.12.0", ClientProtocolKind.V2012, "2.0.12.0", "26728906")]
    [InlineData("26728906", ClientProtocolKind.V2012, "2.0.12.0", "26728906")]
    [InlineData("2.0.13", ClientProtocolKind.V2013, "2.0.13", "26262663")]
    [InlineData("auto", ClientProtocolKind.Auto, "2.0.9.0", "26262663")]
    public void Parse_maps_tokens_to_profiles(string token, ClientProtocolKind kind, string versionId, string profile)
    {
        var settings = ClientProtocolSettings.Parse(token);
        Assert.Equal(kind, settings.Kind);
        Assert.Equal(versionId, settings.VersionId);
        Assert.Equal(profile, settings.ProtocolProfileId);
    }
}