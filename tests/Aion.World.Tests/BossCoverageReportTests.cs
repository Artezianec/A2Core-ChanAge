using System.Text.Json;
using Xunit;

namespace Aion.World.Tests;

public sealed class BossCoverageReportTests
{
    [Fact]
    public void Boss_phases_and_behavior_cases_cover_more_expedition_families()
    {
        var root = FindRepoRoot();
        var phases = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tools/data/boss-phases.json")));
        var count = phases.RootElement.GetProperty("bosses").EnumerateObject().Count();
        Assert.True(count >= 120, $"expected expanded boss scripts, got {count}");
        var behavior = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tools/data/npc-behavior-cases.json")));
        var npcWith = behavior.RootElement.GetProperty("by_npc_id").EnumerateObject().Count();
        Assert.True(npcWith > 100);
    }

    static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "tools/data/boss-phases.json"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("repo root not found");
    }
}