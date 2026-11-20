using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class MonsterRewardsTests
{
    [Fact]
    public void Split_kinah_evenly_with_remainder_to_first_shares()
    {
        Assert.Equal([51L, 50L], MonsterRewards.Split(101, 2));
        Assert.Equal([1L, 1L, 0L], MonsterRewards.Split(2, 3));
        Assert.All(MonsterRewards.Split(0, 2), s => Assert.Equal(0, s));
        Assert.Equal([7L], MonsterRewards.Split(7, 0));
    }

    [Fact]
    public void Kinah_range_scales_by_npc_subtype()
    {
        var rewards = MonsterRewards.Embedded;
        var (normalLow, normalHigh) = rewards.KinahRange("NormalMonster", 10);
        var (eliteLow, eliteHigh) = rewards.KinahRange("EliteMonster", 10);
        Assert.True(eliteLow >= normalLow);
        Assert.True(eliteHigh >= normalHigh);
        Assert.Equal((0, 0), rewards.KinahRange("NormalCitizen", 10));
        Assert.Equal((0, 0), rewards.KinahRange("", 10));
    }

    [Fact]
    public void Fallback_band_covers_level_and_only_grants_table_items()
    {
        var rewards = MonsterRewards.Embedded;
        var data = GameData.Embedded;
        foreach (var level in new[] { 1, 15, 45, 90 })
        {
            var band = rewards.FallbackBand(level);
            Assert.NotEmpty(band);
            foreach (var itemId in band)
            {
                var item = data.Item(itemId);
                Assert.NotNull(item);
                Assert.True(item.PermitLevel <= level, $"item {itemId} permit {item.PermitLevel} > level {level}");
            }
        }
    }

    [Fact]
    public void Fallback_roll_respects_subtype_and_rng()
    {
        var rewards = MonsterRewards.Embedded;
        var rng = new Random(42);
        Assert.Empty(rewards.Fallback("NormalCitizen", 30, rng));
        var heroRolls = Enumerable.Range(0, 400)
            .SelectMany(_ => rewards.Fallback("HeroMonster", 30, rng))
            .ToList();
        Assert.InRange(heroRolls.Count, 100, 300);
        Assert.All(heroRolls, r => Assert.Equal(1, r.Count));
        Assert.All(heroRolls, r => Assert.Contains(r.ItemId, rewards.FallbackBand(30)));
    }
}