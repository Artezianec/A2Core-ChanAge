using System.Text.Json;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QuestAuditFixTests
{
    static QuestDefinition Quest(int id, string repeat) => new(
        Id: id, Title: "Scroll", Type: "DutyScroll", Race: "Light", Level: 1, Map: 0,
        Acquire: "Item", StartNpc: null, Complete: "Auto", FinishNpc: null, GiveUp: true,
        Prerequisites: [], Previous: [], Next: null,
        Steps: [new QuestStep(1, [new QuestObjective("PCLevel", Count: 1)])], Rewards: null,
        Repeat: repeat);

    [Fact]
    public void Infinitely_repeatable_quest_can_be_reaccepted_after_reward()
    {
        var quest = Quest(9000001, "Infinitely");
        var book = new QuestBook(new Dictionary<string, QuestDefinition> { ["DQ9000001"] = quest });
        var log = new QuestLog(book, race: 1);
        log.Set(QuestLog.New("DQ9000001", quest) with { Status = QuestLog.Incomplete });
        Assert.Equal("already_active", log.AcceptBlockReason(quest, 1));
        log.Set(QuestLog.New("DQ9000001", quest) with { Status = QuestLog.Complete });
        Assert.Equal("already_active", log.AcceptBlockReason(quest, 1));
        log.Set(QuestLog.New("DQ9000001", quest) with { Status = QuestLog.Rewarded });
        Assert.True(log.CanAccept("DQ9000001", 1));
    }

    [Fact]
    public void Non_repeatable_quest_cannot_be_reaccepted_after_reward()
    {
        var quest = Quest(9000002, "None");
        var book = new QuestBook(new Dictionary<string, QuestDefinition> { ["DQ9000002"] = quest });
        var log = new QuestLog(book, race: 1);
        log.Set(QuestLog.New("DQ9000002", quest) with { Status = QuestLog.Rewarded });
        Assert.Equal("already_active", log.AcceptBlockReason(quest, 1));
    }

    [Theory]
    [InlineData("2959363")]
    [InlineData("2959365")]
    [InlineData("2959366")]
    public void Quest_talk_npcs_are_not_aggressive(string id)
    {
        using var stream = typeof(GameData).Assembly.GetManifestResourceStream("world-npcs.json")
            ?? throw new InvalidOperationException("world-npcs.json");
        using var doc = JsonDocument.Parse(stream);
        var t = doc.RootElement.GetProperty("templates").GetProperty(id);
        Assert.False(t.GetProperty("aggressive").GetBoolean());
        Assert.Equal(3, t.GetProperty("relationship").GetInt32());
    }
}
