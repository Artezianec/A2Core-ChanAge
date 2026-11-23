using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class AbnormalEngineV2Tests
{
    [Fact]
    public void Reject_weaker_stat_buff_in_same_group()
    {
        var active = new List<(int AbnormalId, int Group, Dictionary<string, int> Stats)>
        {
            (1, 100, new Dictionary<string, int> { ["Attack"] = 500 }),
        };
        var def = new AbnormalEngine.Definition(2, 100, AbnormalEngine.StackKind.RejectWeaker, 1, 0, 5000, 0, false, true, []);
        var incoming = new Dictionary<string, int> { ["Attack"] = 300 };
        var (action, _) = AbnormalEngine.ResolveStack(active, def, incoming);
        Assert.Equal(AbnormalEngine.StackAction.Reject, action);
    }

    [Fact]
    public void Stronger_stat_buff_replaces_group()
    {
        var active = new List<(int AbnormalId, int Group, Dictionary<string, int> Stats)>
        {
            (1, 100, new Dictionary<string, int> { ["Attack"] = 300 }),
        };
        var def = new AbnormalEngine.Definition(2, 100, AbnormalEngine.StackKind.Replace, 1, 0, 5000, 0, false, true, []);
        var incoming = new Dictionary<string, int> { ["Attack"] = 500 };
        var (action, remove) = AbnormalEngine.ResolveStack(active, def, incoming);
        Assert.Equal(AbnormalEngine.StackAction.RemoveSameGroup, action);
        Assert.Contains(1, remove);
    }

    [Fact]
    public void Passive_triggers_load_improvised_rows()
    {
        Assert.NotEmpty(PassiveTriggers.All);
        Assert.Contains(PassiveTriggers.All, t => t.Kind == PassiveTriggers.Kind.OnCrit);
    }

    [Fact]
    public void Wish_of_concentration_stat_in_modifiers()
    {
        var stats = AbnormalEngine.StatModifiers(153100001);
        Assert.True(stats.ContainsKey("MPUseDecrease"));
    }

    [Fact]
    public async Task Passive_hidden_buff_applies_stat_modifier()
    {
        const long now = 5_000_000;
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, now, level: 55);
        var replies = new List<byte[]>();
        session.AddPassiveHiddenBuffForTests(153100001, now, replies);
        Assert.Equal(3000, session.BuffStatForTests("MPUseDecrease"));
        Assert.DoesNotContain(153100001, session.ActiveBuffs);
    }
}