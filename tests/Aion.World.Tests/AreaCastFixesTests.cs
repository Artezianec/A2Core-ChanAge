using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Ports from Addiboii/A2Core update-26262663: area centre clamp and untouchable NPC filtering.</summary>
public sealed class AreaCastFixesTests
{
  static readonly long Now = 1_700_000_200_000;

  [Fact]
  public void AreaCentre_trusts_client_location_only_within_skill_reach()
  {
    var def = new SkillDefinition("test", "Attack", null, [], 0, new SkillCost(0, 0, 0), [], UseRangeCm: 800);
    Assert.Equal(new Vector3d(100, 0, 0), GameSession.AreaCentre(def, new Vector3d(0, 0, 0), new Vector3d(100, 0, 0)));
    Assert.Equal(new Vector3d(0, 0, 0), GameSession.AreaCentre(def, new Vector3d(0, 0, 0), new Vector3d(100_000, 0, 0)));
    Assert.Equal(new Vector3d(5, 0, 0), GameSession.AreaCentre(def, null, new Vector3d(5, 0, 0)));
  }

  [Fact]
  public async Task Self_cast_area_skips_untouchable_npcs_at_the_centre()
  {
    var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now, level: 25, rng: new Random(7));
    var map = Assert.IsType<Dictionary<int, Target>>(session.Targets);
    var here = session.Position!.Value;
    map[880_101] = new Target
    {
      Key = 880_101, Name = "untouchable", Hp = 100_000, HpMax = 100_000, Relationship = 6, Position = here, Untouchable = true,
    };
    map[880_102] = new Target
    {
      Key = 880_102, Name = "touchable", Hp = 100_000, HpMax = 100_000, Relationship = 6, Position = here,
    };
    session.SetPositionForTests(here);
    var cast = new GameSession.CastRequest(16010000, 0, 0, false, session.PlayerKey, 0, here);
    var chosen = session.SelfCastTargetsForTest(16010000, cast);
    Assert.Contains(chosen, t => t.Key == 880_102);
    Assert.DoesNotContain(chosen, t => t.Key == 880_101);
    Assert.False(map[880_101].Attackable);
  }

  [Fact]
  public void NpcMoveDelta_omits_zero_dx_or_dy_from_mask()
  {
    var dyOnly = GamePackets.NpcMoveDelta(0x286, 0, 12, 0, 0, false);
  Assert.Equal(0x24, dyOnly[4]);
    Assert.Equal(12, (sbyte)dyOnly[5]);
    Assert.Equal(1, dyOnly[^1]);
    var none = GamePackets.NpcMoveDelta(0x286, 0, 0, 0, 0, false);
    Assert.Equal(0x20, none[4]);
    Assert.Equal(1, none[^1]);
  }
}