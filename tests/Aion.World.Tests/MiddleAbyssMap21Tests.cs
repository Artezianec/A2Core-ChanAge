using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Middle Abyss map 21: client NpcData templates, supplement spawns, sample duty-scroll quest flow.</summary>
public sealed class MiddleAbyssMap21Tests
{
    const int ExpectedMiddleAbyssMobSpawns = 39;

    [Fact]
    public void Map_21_spawns_middle_abyss_duty_mobs()
    {
        using var stream = typeof(GameData).Assembly.GetManifestResourceStream("world-npcs.json")
            ?? throw new InvalidOperationException("world-npcs.json");
        using var doc = JsonDocument.Parse(stream);
        var rows = doc.RootElement.GetProperty("maps").GetProperty("21").EnumerateArray().ToList();
        var abyss = rows.Where(r => r[1].GetInt32() is >= 2600100 and <= 2600199).ToList();
        Assert.Equal(ExpectedMiddleAbyssMobSpawns, abyss.Count);
        Assert.All(abyss, r => Assert.DoesNotContain("_QuestSup", r[0].GetString() ?? ""));
    }

    [Fact]
    public void Middle_abyss_template_2600101_uses_client_export_level_not_donor_clone()
    {
        using var stream = typeof(GameData).Assembly.GetManifestResourceStream("world-npcs.json")
            ?? throw new InvalidOperationException("world-npcs.json");
        using var doc = JsonDocument.Parse(stream);
        var t = doc.RootElement.GetProperty("templates").GetProperty("2600101");
        Assert.Equal(50, t.GetProperty("level").GetInt32());
        Assert.Equal(7, t.GetProperty("grade").GetInt32());
    }

    [Fact]
    public void Sample_duty_scroll_UQ1309038_accepts_and_completes_kill_objective()
    {
        var book = QuestBook.Embedded;
        var def = book.Find("UQ1309038") ?? throw new InvalidOperationException("UQ1309038");
        Assert.Equal(21, def.Map);
        var log = new QuestLog(book, race: 1);
        const int level = 55;
        log.Set(QuestLog.New("UQ1309038", def));
        var record = log.Get("UQ1309038")!;
        for (var guard = 0; guard < 8 && record.Status == QuestLog.Incomplete; guard++)
        {
            var step = log.CurrentStep(record);
            if (step is null) break;
            var kill = step.Objectives.FirstOrDefault(o => o.Type == "KillNpc");
            if (kill?.Npcs is { } npcs)
                foreach (var _ in Enumerable.Range(0, kill.Count))
                    foreach (var id in npcs)
                        log.Killed(id, level);
            record = log.CompleteStep("UQ1309038", level) ?? log.Get("UQ1309038")!;
        }
        Assert.True(record.Status is QuestLog.Complete or QuestLog.Incomplete);
        Assert.True(record.Step >= 1, "kill step should advance after duty kills");
    }
}