using Aion.Core;
using Aion.Launcher;

namespace Aion.Launcher.Core.Tests;

public sealed class ClientBuildProfilesTests
{
    [Fact]
    public void Profile_table_keeps_pinned_2_0_9_0_and_optional_2_0_13_without_published_key()
    {
        Assert.Equal(ClientVersion.DefaultId, ClientBuildProfiles.DefaultVersionId);
        Assert.Equal("26262663", ClientBuildProfiles.Default.SteamBuildId);
        Assert.True(ClientBuildProfiles.Default.PinnedDefault);
        Assert.Null(ClientBuildProfiles.Default.AesPakKeyHex);

        var experimental = ClientBuildProfiles.Get("2.0.13");
        Assert.True(string.IsNullOrEmpty(experimental.AesPakKeyHex));
        Assert.Equal("0x14f22d540", experimental.DumperOffsets?.FNamePool);
        Assert.Equal("0x14ed729a0", experimental.DumperOffsets?.GUObjectArray);
        Assert.Null(experimental.SteamBuildId);
        Assert.False(experimental.ProtocolVerified);
    }

    [Fact]
    public void Default_launcher_selection_stays_on_2_0_9_0()
    {
        var config = SampleConfig() with { SelectedClientVersion = null };
        var selected = ClientVersions.Selected(config);
        Assert.Equal(ClientBuildProfiles.DefaultVersionId, selected.Id);
        Assert.False(selected.IsSteam);
        Assert.Equal(ClientBuildProfiles.DefaultVersionId, ClientVersions.Selected(config, "nope").Id);
    }

    [Fact]
    public void Experimental_client_is_not_offered_without_a_pak_key()
    {
        var config = SampleConfig();
        var versions = ClientVersions.All(config);
        Assert.Equal(ClientBuildProfiles.DefaultVersionId, versions[0].Id);
        Assert.DoesNotContain(versions, v => v.Id == ClientBuildProfiles.ExperimentalVersionId);
        Assert.Equal(ClientBuildProfiles.DefaultVersionId, ClientVersions.Selected(config, ClientBuildProfiles.ExperimentalVersionId).Id);
        Assert.Equal(ClientBuildProfiles.DefaultVersionId, ClientVersions.Selected(config).Id);
    }

    [Fact]
    public void Unknown_client_version_falls_back_to_pinned_profile()
    {
        Assert.Equal(ClientBuildProfiles.DefaultVersionId, ClientBuildProfiles.NormalizeId("steam-latest"));
        Assert.Equal(ClientBuildProfiles.DefaultVersionId, ClientBuildProfiles.NormalizeId("2.0.99"));
        Assert.Equal("2.0.13", ClientBuildProfiles.NormalizeId("2.0.13"));
    }

    static LauncherConfig SampleConfig() =>
        new("Test", "https://example", "127.0.0.1", 8087, "test");
}