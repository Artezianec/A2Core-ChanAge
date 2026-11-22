using Aion.Protocol.Native;

namespace Aion.HeadlessClient.Protocol;

public sealed class ScenarioRunner(ServerPacketDecoder decoder)
{
    public async Task<RunReport> RunAsync(ScenarioFile scenario, ConnectionEndpoints endpoints, int accountIndex, CancellationToken ct)
    {
        var account = BuildAccount(scenario, accountIndex);
        var session = new HeadlessSession(decoder, ClientRsaKey.Generate(20261120 + accountIndex));
        var started = DateTimeOffset.UtcNow;
        try
        {
            GameWorldClient world = scenario.Mode.Equals("development", StringComparison.OrdinalIgnoreCase)
                ? await session.ConnectDevelopmentGameAsync(endpoints.Game, account, ct)
                : await session.LoginAndEnterWorldAsync(endpoints.Login, endpoints.Game, account, ct);
            await using (world)
            {
                foreach (var step in scenario.Steps)
                    await ExecuteStepAsync(world, step, ct);
            }
        }
        catch (Exception error)
        {
            session.Metrics.RecordError(error.GetType().Name + ": " + error.Message);
            session.Metrics.RecordDisconnect();
        }
        return session.Metrics.Report(scenario.Name, DateTimeOffset.UtcNow - started);
    }

    public async Task<AggregateReport> RunConcurrentAsync(ScenarioFile scenario, ConnectionEndpoints endpoints, int accounts, CancellationToken ct)
    {
        var tasks = Enumerable.Range(0, accounts).Select(i => RunAsync(scenario, endpoints, i, ct));
        var reports = await Task.WhenAll(tasks);
        var aggregate = new AggregateReport();
        aggregate.Accounts.AddRange(reports);
        return aggregate;
    }

    async Task ExecuteStepAsync(GameWorldClient world, ScenarioStep step, CancellationToken ct)
    {
        switch (step.Action)
        {
            case "idle":
            case "login-and-idle":
                await world.IdleAsync(TimeSpan.FromSeconds(step.Seconds), ct);
                break;
            case "walk-route":
                var route = step.Route();
                if (route.Count == 0)
                {
                    var start = world.Spawn.Position;
                    route = [start, start with { X = start.X + 250, Y = start.Y + 120 }];
                }
                await world.WalkRouteAsync(route, (float)step.Speed, step.ReportRate, step.MaxSeconds, ct);
                break;
            case "kill-mob":
            case "kill-n-mobs":
                await world.AutoAttackAsync(step.TargetKey ?? 1000, step.SkillId ?? 11020000, ct);
                break;
            case "talk-npc":
                await world.TalkToNpcAsync(step.NpcKey ?? 0, ct);
                break;
            case "chat":
                await world.ChatAsync(step.Text ?? "headless ping", ct);
                break;
            case "quest-accept" when step.QuestId is { } quest:
                await world.AcceptQuestAsync(quest, ct);
                break;
            default:
                await world.IdleAsync(TimeSpan.FromSeconds(1), ct);
                break;
        }
    }

    static HeadlessAccount BuildAccount(ScenarioFile scenario, int index)
    {
        var name = $"Headless{index:0000}";
        var token = scenario.Mode.Equals("development", StringComparison.OrdinalIgnoreCase)
            ? ClientPackets.DevelopmentAuthToken
            : ClientPackets.HeadlessAuthToken(scenario.SharedSecret ?? "", index);
        return new HeadlessAccount(name, token);
    }

    public static ConnectionEndpoints ParseServer(string server, int? loginPort, int? gamePort)
    {
        var split = server.Split(':', 2);
        var host = split[0];
        var port = split.Length > 1 && int.TryParse(split[1], out var parsed) ? parsed : gamePort ?? 13328;
        return new ConnectionEndpoints(
            new LoginTarget(host, loginPort ?? 3700),
            new GameTarget(host, gamePort ?? port));
    }

    public static string OpcodeJsonPath(string repoRoot) => Path.Combine(repoRoot, "tools/data/opcode-names.json");
}

public sealed record ConnectionEndpoints(LoginTarget Login, GameTarget Game);