using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Aion.HeadlessClient.Protocol;
using Aion.Protocol.Native;
using Xunit;

namespace Aion.HeadlessClient.Tests;

public sealed class HostIntegrationTests : IAsyncLifetime
{
    Process? host;
    int loginPort;
    int gamePort;
    string repoRoot = "";

    public async Task InitializeAsync()
    {
        repoRoot = FindRepoRoot();
        loginPort = FreePort();
        gamePort = FreePort();
        var dll = Path.Combine(repoRoot, "server/Aion.Host/bin/Release/net10.0/Aion.Host.dll");
        if (!File.Exists(dll)) dll = Path.Combine(repoRoot, "server/Aion.Host/bin/Debug/net10.0/Aion.Host.dll");
        if (!File.Exists(dll)) return;
        var localHttp = FreePort();
        host = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = dll,
            WorkingDirectory = Path.GetDirectoryName(dll)!,
            UseShellExecute = false,
            Environment =
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["AION_SKIP_WEB"] = "1",
                ["Aion__LocalPort"] = localHttp.ToString(),
                ["Aion__Native__LoginPort"] = loginPort.ToString(),
                ["Aion__Native__GamePort"] = gamePort.ToString(),
                ["Aion__Native__Logins"] = "development",
                ["Aion__Native__DevelopmentStartMap"] = "1110",
                ["ConnectionStrings__Aion"] = "",
                ["ConnectionStrings__Auth"] = "",
            },
        });
        await WaitForPort(gamePort, TimeSpan.FromSeconds(60));
        await Task.Delay(1500);
    }

    public async Task DisposeAsync()
    {
        if (host is null) return;
        host.Kill(entireProcessTree: true);
        await host.WaitForExitAsync();
    }

    [SkippableFact]
    public async Task Development_login_and_idle_scenario_succeeds()
    {
        Skip.If(host is null, "Aion.Host is not built");
        var decoder = ServerPacketDecoder.LoadFromFile(ScenarioRunner.OpcodeJsonPath(repoRoot));
        var scenario = new ScenarioFile
        {
            Name = "login-and-idle",
            Mode = "development",
            Steps = [new ScenarioStep { Action = "login-and-idle", Seconds = 2 }],
        };
        var runner = new ScenarioRunner(decoder);
        var endpoints = new ConnectionEndpoints(new LoginTarget("127.0.0.1", loginPort), new GameTarget("127.0.0.1", gamePort));
        var report = await runner.RunAsync(scenario, endpoints, 0, CancellationToken.None);
        Assert.Empty(report.Errors);
        Assert.True(report.WorldEntryMs.Count > 0 || report.PingMs.Count > 0);
    }

    [SkippableFact]
    public async Task Walk_route_scenario_moves_without_errors()
    {
        Skip.If(host is null, "Aion.Host is not built");
        var decoder = ServerPacketDecoder.LoadFromFile(ScenarioRunner.OpcodeJsonPath(repoRoot));
        var scenario = new ScenarioFile
        {
            Name = "walk-route",
            Mode = "development",
            Steps =
            [
                new ScenarioStep
                {
                    Action = "walk-route",
                    Speed = 500,
                    ReportRate = 4,
                    MaxSeconds = 5,
                    Points = [],
                },
            ],
        };
        var runner = new ScenarioRunner(decoder);
        var endpoints = new ConnectionEndpoints(new LoginTarget("127.0.0.1", loginPort), new GameTarget("127.0.0.1", gamePort));
        var report = await runner.RunAsync(scenario, endpoints, 1, CancellationToken.None);
        Assert.Empty(report.Errors);
    }

    static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    static async Task WaitForPort(int port, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(200);
            }
        }
        throw new TimeoutException("Native game port did not open");
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tools/data/opcode-names.json")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}