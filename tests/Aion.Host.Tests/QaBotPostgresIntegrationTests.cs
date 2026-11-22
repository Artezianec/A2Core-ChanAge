using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Npgsql;
using Xunit;

namespace Aion.Host.Tests;

/// <summary>
/// Boots Aion.Host against Postgres as aion_app with QA AutoRun and asserts onlineBots &gt; 0.
/// Requires AION_QA_BOT_INTEGRATION=1, ConnectionStrings__Aion/Auth, and migrated game data (starters).
/// </summary>
public sealed class QaBotPostgresIntegrationTests : IAsyncLifetime
{
    const int TimeoutSeconds = 180;
    Process? host;
    int localPort;
    string repoRoot = "";

    public Task InitializeAsync()
    {
        repoRoot = FindRepoRoot();
        if (!IntegrationEnabled()) return Task.CompletedTask;
        localPort = FreePort();
        var dll = HostDll();
        var aion = Environment.GetEnvironmentVariable("ConnectionStrings__Aion");
        var auth = Environment.GetEnvironmentVariable("ConnectionStrings__Auth");
        if (string.IsNullOrWhiteSpace(aion) || string.IsNullOrWhiteSpace(auth) || !File.Exists(dll))
            return Task.CompletedTask;

        RunSeedScript();
        var loginPort = FreePort();
        var gamePort = FreePort();
        host = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = dll,
            WorkingDirectory = Path.GetDirectoryName(dll)!,
            UseShellExecute = false,
            Environment =
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
                ["AION_SKIP_WEB"] = "1",
                ["ConnectionStrings__Aion"] = aion,
                ["ConnectionStrings__Auth"] = auth,
                ["Aion__LocalPort"] = localPort.ToString(),
                ["Aion__Native__LoginPort"] = loginPort.ToString(),
                ["Aion__Native__GamePort"] = gamePort.ToString(),
                ["Aion__Native__Logins"] = "accounts",
                ["Aion__HeadlessClients__Enabled"] = "false",
                ["Aion__QaBots__Enabled"] = "true",
                ["Aion__QaBots__AutoRun__Enabled"] = "true",
                ["Aion__QaBots__AutoRun__Count"] = "1",
                ["Aion__QaBots__MaxBots"] = "1",
                ["Aion__QaBots__HardMaxBots"] = "2",
                ["Aion__Bots__Enabled"] = "false",
            },
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (host is null) return;
        host.Kill(entireProcessTree: true);
        await host.WaitForExitAsync();
    }

    [SkippableFact]
    public async Task Postgres_qa_autorun_puts_bot_in_world_and_progresses()
    {
        Skip.If(!IntegrationEnabled(), "Set AION_QA_BOT_INTEGRATION=1");
        Skip.If(host is null, "Host not started (missing ConnectionStrings or build)");
        var aion = Environment.GetEnvironmentVariable("ConnectionStrings__Aion");
        Skip.If(string.IsNullOrWhiteSpace(aion), "ConnectionStrings__Aion required");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(TimeoutSeconds);
        var onlineBots = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{localPort}/api/status");
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("onlineBots", out var bots))
                        onlineBots = bots.GetInt32();
                    if (doc.RootElement.TryGetProperty("gameReady", out var ready) && ready.GetBoolean() && onlineBots > 0)
                        break;
                }
            }
            catch (HttpRequestException) { }
            catch (SocketException) { }
            await Task.Delay(1000);
        }
        Assert.True(onlineBots > 0, $"Expected onlineBots > 0 within {TimeoutSeconds}s (last={onlineBots})");

        var baseline = await ReadQaSnapshotAsync(aion!);
        var progressed = false;
        var heroMilestone = false;
        var progressDeadline = DateTimeOffset.UtcNow.AddSeconds(TimeoutSeconds);
        while (DateTimeOffset.UtcNow < progressDeadline)
        {
            await Task.Delay(2000);
            var snap = await ReadQaSnapshotAsync(aion!);
            if (snap.IncompleteQuests > 0 || snap.Experience > baseline.Experience || snap.Position != baseline.Position)
                progressed = true;
            if (snap.Hq1101010Step >= 3 || snap.Hq1101011Step >= 3 || snap.Hq1101012Step >= 2 || snap.RewardedQuests > 0 || snap.Experience > baseline.Experience)
            {
                heroMilestone = true;
                break;
            }
        }
        var final = await ReadQaSnapshotAsync(aion!);
        Assert.True(progressed,
            $"Expected quest accept, XP, or position change within {TimeoutSeconds}s (xp {baseline.Experience}->{final.Experience})");
        heroMilestone |= final.Hq1101010Step >= 3 || final.Hq1101011Step >= 3 || final.Hq1101012Step >= 2 || final.RewardedQuests > 0 || final.Experience > baseline.Experience;
        Assert.True(heroMilestone,
            $"Expected HQ1101010/1011 step 3+ (use_env supply), reward, or XP (1010={final.Hq1101010Step}, 1011={final.Hq1101011Step}, rewarded={final.RewardedQuests}, xp={final.Experience})");
    }

    static async Task<QaDbSnapshot> ReadQaSnapshotAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT COALESCE(MAX(c.experience),0),
                   COALESCE(MIN(c.position::text), ''),
                   COALESCE((SELECT COUNT(*) FROM game.character_quests q
                             JOIN game.characters c2 ON c2.character_id=q.character_id
                             WHERE c2.name LIKE 'QA%' AND q.status=2),0),
                   COALESCE((SELECT MAX(q.step) FROM game.character_quests q
                             JOIN game.characters c2 ON c2.character_id=q.character_id
                             WHERE c2.name LIKE 'QA%' AND q.quest_code='HQ1101010' AND q.status=2),0),
                   COALESCE((SELECT COUNT(*) FROM game.character_quests q
                             JOIN game.characters c2 ON c2.character_id=q.character_id
                             WHERE c2.name LIKE 'QA%' AND q.status>=4),0),
                   COALESCE((SELECT MAX(q.step) FROM game.character_quests q
                             JOIN game.characters c2 ON c2.character_id=q.character_id
                             WHERE c2.name LIKE 'QA%' AND q.quest_code='HQ1101011' AND q.status=2),0),
                   COALESCE((SELECT MAX(q.step) FROM game.character_quests q
                             JOIN game.characters c2 ON c2.character_id=q.character_id
                             WHERE c2.name LIKE 'QA%' AND q.quest_code='HQ1101012' AND q.status=2),0)
            FROM game.characters c
            WHERE c.name LIKE 'QA%'
            """, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return new(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6));
    }

    readonly record struct QaDbSnapshot(long Experience, string Position, int IncompleteQuests, int Hq1101010Step, int RewardedQuests, int Hq1101011Step, int Hq1101012Step);

    static bool IntegrationEnabled() => Environment.GetEnvironmentVariable("AION_QA_BOT_INTEGRATION") == "1";

    static void RunSeedScript()
    {
        var root = FindRepoRoot();
        var script = Path.Combine(root, "tools/qa_bot_integration_seed.py");
        if (!File.Exists(script)) return;
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "python3",
            Arguments = script,
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        process?.WaitForExit(60_000);
    }

    static string HostDll()
    {
        var root = FindRepoRoot();
        var release = Path.Combine(root, "server/Aion.Host/bin/Release/net10.0/Aion.Host.dll");
        var debug = Path.Combine(root, "server/Aion.Host/bin/Debug/net10.0/Aion.Host.dll");
        return File.Exists(release) ? release : debug;
    }

    static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "Aion2-repo.sln")) || Directory.Exists(Path.Combine(dir, "server/Aion.Host")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName ?? "";
        }
        return Directory.GetCurrentDirectory();
    }

    static int FreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}