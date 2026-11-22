using System.CommandLine;
using System.Text.Json;
using Aion.HeadlessClient.Protocol;
using Aion.Protocol.Native;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

var root = FindRepoRoot(Directory.GetCurrentDirectory());
var decoder = ServerPacketDecoder.LoadFromFile(ScenarioRunner.OpcodeJsonPath(root));
var runner = new ScenarioRunner(decoder);

var run = new Command("run", "Run a headless scenario against our native server");
var server = new Option<string>("--server", () => "127.0.0.1:13328", "Game host, or host:port");
var accounts = new Option<int>("--accounts", () => 1, "Concurrent synthetic clients");
var scenario = new Option<FileInfo>("--scenario", "Scenario JSON or YAML file") { IsRequired = true };
var loginPort = new Option<int?>("--login-port", () => null, "Login port when not using development mode");
var gamePort = new Option<int?>("--game-port", () => null, "Game port override");
var report = new Option<FileInfo?>("--report", () => null, "Write JSON report here");

run.AddOption(server);
run.AddOption(accounts);
run.AddOption(scenario);
run.AddOption(loginPort);
run.AddOption(gamePort);
run.AddOption(report);

run.SetHandler(async (host, count, file, login, game, reportPath) =>
{
    var text = await File.ReadAllTextAsync(file.FullName);
    var model = file.Extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
        || file.Extension.Equals(".yml", StringComparison.OrdinalIgnoreCase)
        ? new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build().Deserialize<ScenarioFile>(text)
        : JsonSerializer.Deserialize<ScenarioFile>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Scenario file is empty");
  var endpoints = ScenarioRunner.ParseServer(host, login, game);
  var aggregate = await runner.RunConcurrentAsync(model, endpoints, count, CancellationToken.None);
  var json = JsonSerializer.Serialize(aggregate, new JsonSerializerOptions { WriteIndented = true });
  if (reportPath is not null) await File.WriteAllTextAsync(reportPath.FullName, json);
  Console.WriteLine(json);
}, server, accounts, scenario, loginPort, gamePort, report);

var cmd = new RootCommand("AION2 headless native-protocol test client (our server only)");
cmd.AddCommand(run);
return await cmd.InvokeAsync(args);

static string FindRepoRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "tools", "data", "opcode-names.json")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return start;
}