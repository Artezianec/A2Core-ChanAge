using System.Net;
using System.Net.Sockets;
using Aion.Host;

namespace Aion.Host.Native;

/// <summary>
/// Whether the configured native login/game TCP ports accept connections (cached briefly for /api/status).
/// </summary>
public sealed class NativePortReadiness(NativeOptions options, NetworkOptions network, TimeProvider clock)
{
    static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);
    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    readonly SemaphoreSlim gate = new(1, 1);
    bool? cached;
    DateTimeOffset cachedAt = DateTimeOffset.MinValue;

    /// <summary>True when every configured native port (login and/or game) answers a TCP connect on this host.</summary>
    public async Task<bool> GameReadyAsync(CancellationToken ct)
    {
        var ports = PortsToProbe();
        if (ports.Count == 0) return false;
        var now = clock.GetUtcNow();
        if (cached is { } ready && now - cachedAt < CacheFor) return ready;

        await gate.WaitAsync(ct);
        try
        {
            now = clock.GetUtcNow();
            if (cached is { } again && now - cachedAt < CacheFor) return again;
            var host = ProbeHost(network);
            var probes = ports.Select(port => ProbeAsync(host, port, ct)).ToArray();
            ready = (await Task.WhenAll(probes)).All(static ok => ok);
            cached = ready;
            cachedAt = now;
            return ready;
        }
        finally { gate.Release(); }
    }

    internal IReadOnlyList<int> PortsToProbe()
    {
        var ports = new List<int>(2);
        if (options.LoginPort is { } login) ports.Add(login);
        if (options.GamePort is { } game) ports.Add(game);
        return ports;
    }

    internal static string ProbeHost(NetworkOptions network) =>
        IPAddress.IsLoopback(network.Bind) || network.Bind.Equals(IPAddress.Any) ? "127.0.0.1" : network.BindAddress;

    static async Task<bool> ProbeAsync(string host, int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException) { return false; }
    }
}