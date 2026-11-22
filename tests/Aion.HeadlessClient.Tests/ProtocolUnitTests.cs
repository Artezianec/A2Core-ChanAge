using Aion.HeadlessClient.Protocol;
using Aion.Protocol.Native;
using Xunit;

namespace Aion.HeadlessClient.Tests;

public sealed class ProtocolUnitTests
{
    [Fact]
    public void Client_handshake_round_trip_matches_native_checks()
    {
        var rsa = ClientRsaKey.Generate(20261120);
        var server = new RsaPublicKey(rsa.Modulus, rsa.PublicExponent);
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var reply = NativeHandshake.GameReply(server, key);
        var bodies = NativeFraming.Split(reply, out _);
        var session = rsa.UnwrapSessionKey(bodies[0]);
        Assert.Equal(key, session);
    }

    [Fact]
    public void Headless_token_format_is_stable()
    {
        Assert.Equal("headless:secret:3", ClientPackets.HeadlessAuthToken("secret", 3));
    }

    [Fact]
    public void Scenario_runner_parses_server_endpoint()
    {
        var endpoints = ScenarioRunner.ParseServer("127.0.0.1:14000", 3701, null);
        Assert.Equal(14000, endpoints.Game.Port);
        Assert.Equal(3701, endpoints.Login.LoginPort);
    }
}