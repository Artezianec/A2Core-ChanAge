using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// HQ1101030 / HQ002 step 13: after Salros and Nania kill the Drakan wave, scripted NpcMoveToWayPoint must not wait
/// <see cref="MapEventRun.FightWaitMs"/> while companions were left Engaged.
/// </summary>
public sealed class Hq1101030CompanionEngagedTests
{
  [Fact]
  public void Companion_clears_Engaged_when_its_scripted_NPC_fight_ends()
  {
        var prey = new Target
        {
            Key = 9102, Name = "drakan", Hp = 1, HpMax = 1000, Position = new Vector3d(300, 0, 0),
            WalkSpeed = 126, RunSpeed = 500, Radius = 40, Relationship = 11,
        };
        var salros = new Target
        {
            Key = 9101, Name = "salros", Hp = 999999, HpMax = 999999, Position = Vector3d.Zero,
            WalkSpeed = 126, RunSpeed = 500, Radius = 40, Function = "None", Relationship = 13,
        };
        var map = new Dictionary<int, Target> { [salros.Key] = salros, [prey.Key] = prey };
        var ai = new NpcAi(new Random(3));
        Assert.NotEmpty(ai.ForceAggroNpc(salros, prey, 0));
        Assert.True(ai.Brains[salros.Key].Engaged);

        long now = 100;
        prey.Hp = 0;
        ai.Tick(map, new Vector3d(0, -500, 0), now);
        Assert.False(ai.Brains[salros.Key].Engaged);
        Assert.Equal(NpcAi.Mode.Idle, ai.Brains[salros.Key].Mode);
    }

    [Fact]
    public void CompanionPrey_skips_hostiles_not_listed_in_CompanionEngageNpcIds()
    {
        var tree = new Target
        {
            Key = 9201, Name = "tree_mob", NpcId = 2290001, Hp = 500, HpMax = 500,
            Position = new Vector3d(80, 0, 0),             WalkSpeed = 126, RunSpeed = 500, Radius = 40, Relationship = 11,
        };
        var nania = new Target
        {
            Key = 9200, Name = "nania", NpcId = 2931088, Hp = 999999, HpMax = 999999, Position = Vector3d.Zero,
            WalkSpeed = 126, RunSpeed = 500, Radius = 40, Function = "Companion", Relationship = 13,
        };
        var map = new Dictionary<int, Target> { [nania.Key] = nania, [tree.Key] = tree };
        var ai = new NpcAi(new Random(5)) { CompanionEngageNpcIds = new HashSet<int> { 2290002 } };
        ai.Tick(map, Vector3d.Zero, 0);
        Assert.Equal(NpcAi.Mode.Idle, ai.Brains[nania.Key].Mode);
        Assert.False(ai.Brains[nania.Key].Engaged);
    }
}