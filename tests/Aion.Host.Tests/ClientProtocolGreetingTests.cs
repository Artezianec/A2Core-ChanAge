using Aion.Core;
using Aion.Host.Native;
using Aion.Protocol.Native;
using Xunit;

namespace Aion.Host.Tests;

/// <summary>A pinned Aion:ClientProtocol must accept only its own game greeting; auto accepts any.</summary>
public sealed class ClientProtocolGreetingTests
{
    [Theory]
    [InlineData(ClientProtocolKind.V209, NativeHandshake.GameGreeting)]
    [InlineData(ClientProtocolKind.V2012, NativeHandshake.WorldGreeting)]
    [InlineData(ClientProtocolKind.V2013, NativeHandshake.WorldGreetingV13)]
    public void Pinned_protocols_require_their_game_greeting(ClientProtocolKind kind, ushort expected) =>
        Assert.Equal(expected, NativeProtocolGate.RequiredGameGreeting(kind));

    [Fact]
    public void Auto_accepts_any_game_greeting() =>
        Assert.Null(NativeProtocolGate.RequiredGameGreeting(ClientProtocolKind.Auto));
}
