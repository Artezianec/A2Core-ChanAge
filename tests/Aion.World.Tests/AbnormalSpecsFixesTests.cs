using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>The 2.0.12/2.0.13 abnormal rows that carry slow and knockdown as stat/displacement effects.</summary>
public sealed class AbnormalSpecsFixesTests
{
    [Fact]
    public void Movement_slow_rows_are_read_from_stat_change_move_speed()
    {
        Assert.Equal(3000, AbnormalSpecs.Npc(10013901).SlowPermille);
        Assert.Equal(5000, AbnormalSpecs.Npc(12000173).SlowPermille);
    }

    [Fact]
    public void Knockdown_instant_rows_displace_the_target()
    {
        var move = Assert.Single(AbnormalSpecs.Npc(12000301).Moves);
        Assert.Equal(AbnormalSpecs.DisplaceKind.Knockback, move.Kind);
        Assert.Equal(200, move.Amount);
        Assert.True(move.Ms >= 100);
    }
}
