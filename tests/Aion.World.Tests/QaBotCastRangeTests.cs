using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QaBotCastRangeTests
{
    [Fact]
    public void InCastRange_rejects_seven_kilometer_gap()
    {
        var data = GameData.Embedded;
        var skill = 12010000;
        var me = new Vector3d(472, 4944, 1525);
        var prey = new Target { Key = 26054, Name = "mob", HpMax = 100, Position = new Vector3d(-44, -2207, 1413), Relationship = 11 };
        Assert.False(QaBotCombat.InCastRange(data, me, prey, skill));
    }
}