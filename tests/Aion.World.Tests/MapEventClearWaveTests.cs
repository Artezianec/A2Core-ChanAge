using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// MapEvent ClearWave (ported from ckrealm/A2Core c299686, AGPL-3.0): tracked when the host knows the wave's monsters (it reports
/// the clear with MapEventRun.WaveCleared), credited at once when the wave is already done, and timed out after
/// MapEventRun.FallbackMs when the host does not know the wave (as every ClearWave was before).
/// </summary>
public sealed class MapEventClearWaveTests
{
    const long Now = 1_760_300_000_000;

    static MapEventDefinition Waves()
    {
        var fight = new MapEventStep(1, [new MapEventDirective(1, "StartWave", ["Test_Wave_01"])],
            [new MapEventObjective(1, "ClearWave", ["Test_Wave_01"])], []);
        var after = new MapEventStep(2, [], [new MapEventObjective(2, "KillNpc", ["Someone", "1"], Npcs: [1])], []);
        return new MapEventDefinition(600082, [], [], [new MapEventStage(1, "", [fight, after], null, null, null, null)]) { Name = "WaveSlice" };
    }

    [Fact]
    public void A_known_wave_waits_for_its_clear_not_the_timer()
    {
        var host = new WaveHost();
        host.Known.Add("Test_Wave_01");
        var run = new MapEventRun(Waves(), 1, host, Now);
        run.Start(Now);
        Assert.Equal(["Test_Wave_01"], host.Started);
        run.Tick(Now + MapEventRun.FallbackMs + 5_000);
        Assert.Equal(1, run.StepOrder);
        Assert.False(run.WaveCleared("Other_Wave", Now + 40_000));
        Assert.True(run.WaveCleared("test_wave_01", Now + 40_000));
        Assert.Equal(2, run.StepOrder);
    }

    [Fact]
    public void An_unknown_wave_times_out_as_before()
    {
        var host = new WaveHost();
        var run = new MapEventRun(Waves(), 1, host, Now);
        run.Start(Now);
        run.Tick(Now + MapEventRun.FallbackMs - 1_000);
        Assert.Equal(1, run.StepOrder);
        run.Tick(Now + MapEventRun.FallbackMs + 1_000);
        Assert.Equal(2, run.StepOrder);
    }

    [Fact]
    public void A_wave_already_done_when_its_step_begins_is_credited_at_once()
    {
        var host = new WaveHost();
        host.Known.Add("Test_Wave_01");
        host.Done.Add("Test_Wave_01");
        var run = new MapEventRun(Waves(), 1, host, Now);
        run.Start(Now);
        Assert.Equal(2, run.StepOrder);
    }

    sealed class WaveHost : IMapEventHost
    {
        public HashSet<string> Known { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Done { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Started { get; } = [];
        public bool WaveKnown(string wave) => Known.Contains(wave);
        public bool WaveDone(string wave) => Done.Contains(wave);
        public void StartWave(string wave) => Started.Add(wave);
        public void EndWave(string wave) { }
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
