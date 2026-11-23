using System.Globalization;
using System.Text.Json;
using Aion.World;
using Xunit;
using Xunit.Abstractions;

namespace Aion.World.Tests;

public sealed class AchievementEmitterCoverageTests
{
    readonly ITestOutputHelper output;

    public AchievementEmitterCoverageTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void UseEnvObjSpawnerName_gates_field_content_unlock_groups()
    {
        var field = AchievementTables.Embedded.All
            .Where(e => e.Type == "UseEnvObjSpawnerName" && e.Group.StartsWith("Group_Unlock_FieldContents_", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(field);
        output.WriteLine($"{field.Count} UseEnvObjSpawnerName entries in Group_Unlock_FieldContents_*");
    }

    [Fact]
    public void Coverage_lists_uncovered_objective_types_with_entry_counts()
    {
        using var stream = typeof(AchievementTables).Assembly.GetManifestResourceStream("achievements.json")!;
        var file = JsonSerializer.Deserialize<AchievementTablesFixture>(stream)!;
        var byType = file.Achievements.Values.GroupBy(e => e.T).ToDictionary(g => g.Key, g => g.Count());
        var registered = AchievementEmitters.RegisteredTypes.Count > 0
            ? AchievementEmitters.RegisteredTypes
            : CollectDeclaredEmitterTypes();
        var missing = byType.Keys.Except(registered, StringComparer.OrdinalIgnoreCase).OrderBy(t => t).ToList();
        foreach (var type in missing)
            output.WriteLine($"{byType[type],6} {type}");
        output.WriteLine($"Emitters: {registered.Count}, types in data: {byType.Count}, uncovered types: {missing.Count}");
        Assert.Contains("UseEnvObjSpawnerName", registered);
        Assert.Contains("PCLevel", registered);
        Assert.Contains("LoginCharacter", registered);
        Assert.Contains("GetDungeonReward", registered);
        Assert.Contains("ArenaKill", registered);
        Assert.Contains("CompleteFieldEvent", registered);
    }

    [Fact]
    public void Emit_applies_default_value_matcher()
    {
        var entry = new AchievementTables.Entry("G", "UseEnvObjSpawnerName", ["E_L1_HQ_GodFragment_01"], [[1, 1]], 0, 0) { Id = 1 };
        var match = AchievementEmitters.Matcher(new AchievementEvent("UseEnvObjSpawnerName", Keys: ["E_L1_HQ_GodFragment_01"]));
        Assert.True(match(entry));
        Assert.False(match(entry with { Values = ["Other"] }));
    }

    static HashSet<string> CollectDeclaredEmitterTypes()
    {
        var root = FindRepoRoot();
        var dir = Path.Combine(root, "server", "Aion.World");
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         text, """(?:Achieve(?:Set)?\(\s*"([^"]+)"|Emit\(new\s+AchievementEvent\(\s*"([^"]+)"|new\s+AchievementEvent\(\s*"([^"]+)")"""))
            {
                for (var i = 1; i < m.Groups.Count; i++)
                    if (m.Groups[i].Success && m.Groups[i].Value.Length > 0)
                        types.Add(m.Groups[i].Value);
            }
        }
        return types;
    }

    static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "tools", "data", "achievements.json")))
                return dir;
            dir = Path.GetDirectoryName(dir)!;
        }
        throw new InvalidOperationException("Repo root not found");
    }

    sealed class AchievementTablesFixture
    {
        public Dictionary<string, EntryFixture> Achievements { get; set; } = [];
    }

    sealed class EntryFixture
    {
        public string G { get; set; } = "";
        public string T { get; set; } = "";
    }
}