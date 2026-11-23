using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QaBombBoxPickTests
{
    [Fact]
    public void Camp_respawn_prefers_supply_lane_box_over_soldier_camp_pickup()
    {
        var camp = new Vector3d(-2516, -16076, 20375);
        var soldierBox = new Vector3d(7679.8, -30204.3, 22954.8);
        var supplyBox = new Vector3d(11450, -38533, 21429);
        var tower = new Vector3d(11766.6, -37577.7, 21429);
        var soldierScore = QaMapEventRunner.CarryBombBoxScore(camp, soldierBox, tower);
        var supplyScore = QaMapEventRunner.CarryBombBoxScore(camp, supplyBox, tower);
        Assert.True(supplyScore < soldierScore);
    }
}