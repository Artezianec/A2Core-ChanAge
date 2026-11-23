using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class AchievementEventBusTests
{
    [Theory]
    [InlineData("KillNpc", "12345", true)]
    [InlineData("KillNpc", "99999", false)]
    public void KillNpc_matcher_checks_npc_ids(string type, string key, bool expect)
    {
        var entry = new AchievementTables.Entry("G", type, [], [[1, 1]], 0, 0, Npcs: [12345]) { Id = 1 };
        var match = AchievementEmitters.Matcher(new AchievementEvent(type, Keys: [key]));
        Assert.Equal(expect, match(entry));
    }

    [Fact]
    public void State_types_include_login_backfill_kinds()
    {
        Assert.Contains("PCLevel", AchievementEmitters.StateTypes);
        Assert.Contains("VehicleCollect", AchievementEmitters.StateTypes);
        Assert.Contains("EquipItemLevel", AchievementEmitters.StateTypes);
    }
}