using System.Net;
using System.Net.Sockets;
using Aion.Host;
using Aion.Host.Native;
using Xunit;

namespace Aion.Host.Tests;

public sealed class NativePortReadinessTests
{
    static readonly NetworkOptions LoopbackNetwork = new();

    [Fact]
    public async Task No_ports_configured_is_not_ready()
    {
        var readiness = new NativePortReadiness(new NativeOptions(), LoopbackNetwork, TimeProvider.System);
        Assert.False(await readiness.GameReadyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task All_configured_ports_must_accept()
    {
        using var login = Listen(out var loginPort);
        var gamePort = loginPort + 1;
        using var game = new TcpListener(IPAddress.Loopback, gamePort);
        game.Start();
        var readiness = new NativePortReadiness(new NativeOptions { LoginPort = loginPort, GamePort = gamePort }, LoopbackNetwork, TimeProvider.System);
        Assert.True(await readiness.GameReadyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Missing_one_configured_port_is_not_ready()
    {
        using var login = Listen(out var loginPort);
        var readiness = new NativePortReadiness(new NativeOptions { LoginPort = loginPort, GamePort = loginPort + 1 }, LoopbackNetwork, TimeProvider.System);
        Assert.False(await readiness.GameReadyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Game_only_world_role_ready_when_game_port_accepts()
    {
        using var game = Listen(out var gamePort);
        var readiness = new NativePortReadiness(new NativeOptions { GamePort = gamePort }, LoopbackNetwork, TimeProvider.System);
        Assert.True(await readiness.GameReadyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Closed_port_is_not_ready()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var readiness = new NativePortReadiness(new NativeOptions { GamePort = port }, LoopbackNetwork, TimeProvider.System);
        Assert.False(await readiness.GameReadyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Result_is_cached_for_a_few_seconds()
    {
        using var game = Listen(out var gamePort);
        var readiness = new NativePortReadiness(new NativeOptions { GamePort = gamePort }, LoopbackNetwork, TimeProvider.System);
        Assert.True(await readiness.GameReadyAsync(CancellationToken.None));
        game.Stop();
        Assert.True(await readiness.GameReadyAsync(CancellationToken.None));
    }

    static TcpListener Listen(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }
}