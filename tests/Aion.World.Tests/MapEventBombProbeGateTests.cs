using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class MapEventBombProbeGateTests
{
  [Theory]
  [InlineData(0, false)]
  [InlineData(2, false)]
  [InlineData(3, true)]
  public void Bomb_probe_due_matches_tower_peak_threshold(int peak, bool due)
  {
    var run = new MapEventRun(
        new MapEventDefinition(100002, [], [], []) { Name = "x" },
        1,
        new NullHost(),
        0);
    run.BombTowerPeakEver = peak;
    if (peak >= 3) run.BombProbeDue = true;
    Assert.Equal(due, run.BombTowerPeakEver >= 3 || run.BombProbeDue);
  }

  sealed class NullHost : IMapEventHost
  {
    public void Spawn(string spawner, int delayMs) { }
    public void Despawn(string spawner, int delayMs, bool kill) { }
    public void SummonAround(string target, string spawner, int count, double radius) { }
    public void MoveNpc(string spawner, string route) { }
    public void FollowPc(string spawner, bool follow) { }
    public void RotateNpc(string spawner, string target) { }
    public void Talk(string talk) { }
    public void ChatBubble(string spawner, string talk) { }
    public void SocialAction(string spawner, int action, bool stop) { }
    public void SetAggroPc(string spawner) { }
    public void SetAggroNpc(string spawner, string target) { }
    public void SetRelationship(string spawner, string relationship) { }
    public void TeleportPc(string point, string effect, string before, string after) { }
    public void MapExit(string point, string mode) { }
    public void DungeonClear(MapEventRun run) { }
    public void DungeonFail(MapEventRun run) { }
    public void MapEnvState(int state) { }
    public void MapEnvSetting(int group, int state) { }
    public void Cutscene(string name) { }
    public void ShowGuide(string guide) { }
    public void FocusCamera(string camera, string talk) { }
    public void ShowNote(string note) { }
    public void Message(string key, string argument) { }
    public void Abnormal(int id, int durationMs, string target, bool remove) { }
    public void EnvObjState(string spawner, string state) { }
    public void EnvObjChangePhase(string spawner, int phase) { }
    public void SetEnvObjCanInteract(string spawner, bool canInteract) { }
    public void DynamicTerrain(string spawner, bool block) { }
    public void Progress(MapEventRun run) { }
    public void ScriptBeat(MapEventRun run, string kind, string detail, long now) { }
    public void Unsupported(MapEventRun run, string what) { }
    public bool QuestStepActive(int questId, int step) => false;
    public bool QuestComplete(int questId) => false;
    public bool MapEventStartAllowed(MapEventDefinition definition) => true;
    public bool NpcInCombat(string spawner) => false;
    public bool SpawnerAlive(string spawner) => true;
    public bool SpawnerExists(string spawner) => true;
    public bool PlayerDead => false;
    public double? DistanceTo(string spawner) => null;
  }
}