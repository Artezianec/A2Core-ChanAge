using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class LootTablesEnvTests
{
    [Fact]
    public void Env_loot_maps_godfragment_ids_by_object_name()
    {
        var loot = LootTables.Embedded;
        Assert.True(loot.HasEnvLoot(1201290));
        Assert.True(loot.HasEnvLoot(1301134));
        Assert.False(loot.HasEnvLoot(1201291));
        var light = loot.RollEnv(1201290, 1, new Random(1));
        Assert.Contains(light, d => d.ItemId == 631900001 && d.Count >= 1);
        var dark = loot.RollEnv(1301134, 2, new Random(1));
        Assert.Contains(dark, d => d.ItemId == 631900007 && d.Count >= 1);
    }

    [Fact]
    public void Field_treasure_S1_env_resolves_by_convention()
    {
        var loot = LootTables.Embedded;
        Assert.True(loot.HasEnvLoot(1100130));
        var rolled = loot.RollEnv(1100130, 1, new Random(42));
        Assert.NotEmpty(rolled);
    }
}
