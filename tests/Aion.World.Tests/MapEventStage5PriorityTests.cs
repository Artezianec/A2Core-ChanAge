using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class MapEventStage5PriorityTests
{
    [Fact]
    public void Stage5_light_brigade_prefers_kill_npc_over_hp_remaining()
    {
        var kill = QaMapEventRunner.MapEventObjectivePriority("KillNpc", 100002, deferCombat: false, stageOrder: 5);
        var hp = QaMapEventRunner.MapEventObjectivePriority("HpRemaining", 100002, deferCombat: false, stageOrder: 5);
        Assert.True(kill < hp);
    }
}