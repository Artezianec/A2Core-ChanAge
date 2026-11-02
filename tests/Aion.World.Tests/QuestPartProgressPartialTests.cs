using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QuestPartProgressPartialTests
{
    const long Now = 1_760_005_000_000;

    static QuestDefinition Quest(int id, string race, string part, int count = 10) => new(
        Id: id, Title: "Q", Type: "Hero", Race: race, Level: 1, Map: 0,
        Acquire: "Npc", StartNpc: null, Complete: "Npc", FinishNpc: null, GiveUp: true,
        Prerequisites: [], Previous: [], Next: null,
        Steps: [new QuestStep(1, [new QuestObjective("KillNpc", Count: count)])], Rewards: null, Part: part);

    [Fact]
    public void Partial_kill_progress_raises_part_percent()
    {
        var q1 = Quest(9100001, "Light", "Ep1");
        var q2 = Quest(9100002, "Light", "Ep1");
        var book = new QuestBook(new Dictionary<string, QuestDefinition> { ["HQ9100001"] = q1, ["HQ9100002"] = q2 });
        var log = new QuestLog(book, race: 1);
        log.Set(QuestLog.New("HQ9100001", q1) with { Status = QuestLog.Incomplete });
        Assert.Equal(0, log.PartProgressPercent("Ep1"));
        log.Set(QuestLog.New("HQ9100001", q1) with { Status = QuestLog.Incomplete, Goals = [3, 0, 0, 0, 0, 0] });
        Assert.Equal(15, log.PartProgressPercent("Ep1")); // 0.3 of 1 quest out of 2
        log.Set(QuestLog.New("HQ9100001", q1) with { Status = QuestLog.Incomplete, Goals = [99, 0, 0, 0, 0, 0] });
        Assert.Equal(50, log.PartProgressPercent("Ep1"));
    }

    [Theory]
    [InlineData(3, 10, 0.3)]
    [InlineData(20, 10, 1.0)]
    [InlineData(1, 0, 1.0)]
    [InlineData(0, 0, 0.0)]
    public void Objective_fraction(int have, int count, double expected) =>
        Assert.Equal(expected, QuestLog.ObjectiveFraction(have, count), 6);

    [Fact]
    public void Part_grouping_is_filtered_by_race()
    {
        var light = Quest(9100011, "Light", "Shared");
        var dark1 = Quest(9100010, "Dark", "Shared");
        var dark2 = Quest(9100012, "Dark", "Shared");
        var book = new QuestBook(new Dictionary<string, QuestDefinition>
            { ["HQ9100011"] = light, ["DQ9100010"] = dark1, ["DQ9100012"] = dark2 });
        Assert.Equal(new[] { "HQ9100011" }, book.PartQuests("Shared", 1));
        Assert.Equal(new[] { "DQ9100010", "DQ9100012" }, book.PartQuests("Shared", 2));
        Assert.Equal(1, book.PartRollIndex("HQ9100011"));
        Assert.Equal(2, book.PartRollIndex("DQ9100012"));

        var log = new QuestLog(book, race: 1);
        log.Set(QuestLog.New("HQ9100011", light) with { Status = QuestLog.Rewarded });
        Assert.Equal(100, log.PartProgressPercent("Shared"));
    }

    [Fact]
    public async Task Quest_change_sends_add_before_progress()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterFreshSharedWorldAsync(store, Now, race: 2, level: 3);
        var code = session.Quests!.All.First(q => q.Status == QuestLog.Incomplete && q.Uid != 0).Code;
        var (_, packets) = await session.QuestCommandAsync("QuestCompleteStep", code, CancellationToken.None);
        var ops = packets.Select(GameSessionTestHelpers.Op).ToList();
        var add = ops.IndexOf(0x8d11);
        var progress = ops.IndexOf(0x8d10);
        Assert.True(add >= 0 && progress >= 0, $"ops: {string.Join(',', ops.Select(o => o.ToString("x4")))}");
        Assert.True(add < progress, "0x8d11 QuestAdd must precede 0x8d10 QuestProgress");
    }
}
