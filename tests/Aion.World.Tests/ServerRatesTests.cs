using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Configurable host rates (<see cref="ServerRates"/>), ported from ckrealm/A2Core.</summary>
public sealed class ServerRatesTests
{
    [Fact]
    public void Defaults_match_official_one_x_profile()
    {
        var official = new RatesOptions().Resolve();
        Assert.Equal(ServerRates.Official, official);
        Assert.Equal(1, official.Experience);
        Assert.Equal(1, official.Kinah);
        Assert.Equal(1, official.Drop);
        Assert.Equal(1, official.QuestExperience);
    }

    [Fact]
    public void Accelerated_profile_applies_presets()
    {
        var fast = new RatesOptions { Profile = "accelerated" }.Resolve();
        Assert.Equal(5, fast.Experience);
        Assert.Equal(3, fast.Kinah);
        Assert.Equal(3, fast.Drop);
        Assert.Equal(5, fast.QuestExperience);
    }

    [Fact]
    public void Explicit_values_override_profile()
    {
        var tuned = new RatesOptions { Profile = "accelerated", Experience = 2, Drop = 1.5 }.Resolve();
        Assert.Equal(2, tuned.Experience);
        Assert.Equal(1.5, tuned.Drop);
        Assert.Equal(3, tuned.Kinah);
        Assert.Equal(2, tuned.QuestExperience);
    }

    [Fact]
    public void Quest_experience_can_differ_from_kill_experience()
    {
        var rates = new RatesOptions { Experience = 3, QuestExperience = 1 }.Resolve();
        Assert.Equal(3, rates.Experience);
        Assert.Equal(1, rates.QuestExperience);
        Assert.Equal(1, rates.Kinah);
    }

    [Fact]
    public void Invalid_profile_throws()
    {
        Assert.Throws<InvalidOperationException>(() => new RatesOptions { Profile = "turbo" }.Resolve());
    }

    [Fact]
    public void Out_of_range_rates_throw()
    {
        Assert.Throws<InvalidOperationException>(() => new RatesOptions { Experience = -1 }.Resolve());
        Assert.Throws<InvalidOperationException>(() => new RatesOptions { Drop = 101 }.Resolve());
        Assert.Throws<InvalidOperationException>(() => new RatesOptions { Kinah = double.NaN }.Resolve());
    }

    [Fact]
    public void KillExperience_multiplies_base_and_event_rates()
    {
        var rates = new RatesOptions { Experience = 2 }.Resolve();
        Assert.Equal(200, rates.KillExperience(100, 1));
        Assert.Equal(500, rates.KillExperience(100, 2.5));
        Assert.Equal(0, rates.KillExperience(0, 5));
    }

    [Fact]
    public void QuestReward_scales_quest_exp_only()
    {
        var rates = new RatesOptions { QuestExperience = 4 }.Resolve();
        Assert.Equal(400, rates.QuestReward(100));
        Assert.Equal(0, rates.QuestReward(0));
        Assert.Equal(-5, rates.QuestReward(-5));
    }

    [Theory]
    [InlineData(1000, 3, 3000)]
    [InlineData(1000, 0.5, 500)]
    [InlineData(1000, 0, 0)]
    [InlineData(0, 5, 0)]
    public void KinahAmount_scales_and_clamps(long kinah, double rate, long expected)
    {
        var rates = new RatesOptions { Kinah = rate }.Resolve();
        Assert.Equal(expected, rates.KinahAmount(kinah));
    }

    [Fact]
    public void WithKinah_scales_only_kinah_item_lines()
    {
        const int other = 110150028;
        var rates = new RatesOptions { Kinah = 3 }.Resolve();
        var scaled = rates.WithKinah([(GameSession.KinaItemId, 100), (other, 2)]);
        Assert.Equal([(GameSession.KinaItemId, 300L), (other, 2L)], scaled);
    }

    [Fact]
    public void WithKinah_drops_zero_kinah_line()
    {
        var rates = new RatesOptions { Kinah = 0 }.Resolve();
        var scaled = rates.WithKinah([(GameSession.KinaItemId, 1000)]);
        Assert.Empty(scaled);
    }

    [Fact]
    public void LootRolls_respects_drop_and_event_rates_and_caps()
    {
        var rng = new Random(7);
        var high = new RatesOptions { Drop = 100 }.Resolve();
        Assert.Equal(ServerRates.MaxLootRolls, high.LootRolls(10, rng));
        var none = new RatesOptions { Drop = 0 }.Resolve();
        Assert.Equal(0, none.LootRolls(1, rng));
        var doubled = new RatesOptions { Drop = 2 }.Resolve();
        Assert.True(doubled.LootRolls(1, rng) >= 2);
    }
}