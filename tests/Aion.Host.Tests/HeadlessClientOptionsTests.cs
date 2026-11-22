using System.Net;
using Aion.Host.Native;
using Xunit;

namespace Aion.Host.Tests;

public sealed class HeadlessClientOptionsTests
{
    [Fact]
    public void Disabled_options_reject_tokens()
    {
        var options = new HeadlessClientOptions { Enabled = false, SharedSecret = "test-secret-key" };
        Assert.False(options.AcceptsToken("headless:test-secret-key:0"));
    }

    [Fact]
    public void Enabled_loopback_accepts_matching_secret()
    {
        var options = new HeadlessClientOptions
        {
            Enabled = true,
            SharedSecret = "test-secret-key",
            AllowedNetworks = ["127.0.0.1"],
        };
        Assert.True(options.Allows(IPAddress.Loopback));
        Assert.True(options.AcceptsToken("headless:test-secret-key:2"));
        Assert.Equal("9000002", options.WireAccountId("headless:test-secret-key:2"));
    }
}