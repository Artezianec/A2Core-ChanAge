using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QaMapEventLayerFixTests
{
    const long T0 = 1_760_900_000_000;

    [Fact]
    public void TryBombProbeFarPoint_requires_distance_beyond_reach()
    {
        var tower = new Vector3d(6612, -32271, 21000);
        var reach = 1200.0;
        var min = reach + 2500;
        Assert.True(QaMapEventRunner.TryBombProbeFarPoint(100002, tower, min, out var far));
        var dx = far.X - tower.X;
        var dy = far.Y - tower.Y;
        Assert.True(Math.Sqrt(dx * dx + dy * dy) >= min - 40);
    }

    [Fact]
    public void ClampLayerMovementGoal_keeps_100002_tower_probe_inside_layer_points()
    {
        var tower = new Vector3d(6612, -32271, 21000);
        var far = QaMapEventRunner.ClampLayerMovementGoal(100002, tower with { X = tower.X + 8000, Y = tower.Y + 8000 });
        var points = MapEventTables.Embedded.Points(100002);
        var xs = points.Values.Where(p => p.Length >= 2).Select(p => p[0]);
        var ys = points.Values.Where(p => p.Length >= 2).Select(p => p[1]);
        Assert.InRange(far.X, xs.Min() - 1200, xs.Max() + 1200);
        Assert.InRange(far.Y, ys.Min() - 1200, ys.Max() + 1200);
    }

    [Fact]
    public async Task Bomb_probe_move_only_does_not_trigger_exploit_stall()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, T0, level: 10, qaBot: true);
        var brain = new QaQuestBrain(GameData.Embedded, session, 1, 45, new QaBotOptions());
        var state = brain.DriverState;
        state.BombProbeGoalsBefore = [0, 1, 0];
        state.BombProbeMapEventRunId = 42;
        state.BombProbeAttemptAt = T0;
        state.BombRangeProbeRunsAttempted.Add(42);
        var stall = brain.Think(T0 + 500).Stuck;
        Assert.Null(stall);
    }

    [Fact]
    public async Task In_range_bomb_credit_during_probe_window_does_not_trigger_exploit_stall()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, T0, level: 10, qaBot: true);
        var brain = new QaQuestBrain(GameData.Embedded, session, 1, 45, new QaBotOptions());
        var state = brain.DriverState;
        state.BombProbeGoalsBefore = [0, 0, 0];
        state.BombProbeMapEventRunId = 42;
        state.BombProbeAttemptAt = T0;
        state.BombProbeAt = new Vector3d(99999, 99999, 20000);
        session.RecordQaBombTowerCredit(new WorldObject(1400824, "tower", 0, [1, 2, 3], 0, 0, 0, 15), new Vector3d(10000, -30000, 21000), T0 + 100);
        Assert.Null(brain.Think(T0 + 200).Stuck);
    }

    [Fact]
    public async Task Cast_refusal_on_far_target_is_specific_not_cast_rules()
    {
        var store = new FakeCharacterStore { Progress = (10, 0) };
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { CharacterLevel = 10, WorldId = store.WorldId, TrainingDummy = true }, new Random(3))
        { Store = store, QaBot = true };
        await session.HandleAsync(GameSessionTestHelpers.Login(), T0);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), T0);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), T0);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), T0);
        var dummy = session.Targets![Combat.TrainingDummyKey];
        session.SetPositionForTests(dummy.Position with { X = dummy.Position.X + 50_000 });
        const int skill = 11_010_000;
        await session.HandleAsync(new PacketWriter().U16(0x3800).U16(0).I32(skill).U8(2).Varint(dummy.Key)
            .F32(0).F32((float)dummy.Position.X).F32((float)dummy.Position.Y).F32((float)dummy.Position.Z).ToArray(), T0 + 100);
        var refusal = session.ConsumeQaCastRefusal();
        Assert.NotNull(refusal);
        Assert.NotEqual("cast_rules", refusal!.Value.Reason);
    }

    [Fact]
    public void Bomb_mission_deprioritizes_hp_remaining_on_carry_maps()
    {
        Assert.True(QaMapEventRunner.MapEventObjectivePriority("UseEnvObj", 100002, deferCombat: true)
            < QaMapEventRunner.MapEventObjectivePriority("HpRemaining", 100002, deferCombat: true));
    }

    [Fact]
    public async Task Height_refusal_triggers_reposition_not_blacklist()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, T0, level: 10, qaBot: true);
        var brain = new QaQuestBrain(GameData.Embedded, session, 1, 45, new QaBotOptions());
        const int preyKey = 56014;
        session.NoteQaCastRefusal(preyKey, "height");
        brain.NoteClientCastOutcome(session);
        Assert.DoesNotContain(preyKey, brain.DriverState.CastRefusalBlacklisted);
        Assert.Equal(preyKey, brain.DriverState.CastHeightRepositionKey);
    }

    [Fact]
    public async Task Out_of_range_refusal_repositions_instead_of_blacklist()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, T0, level: 10, qaBot: true);
        var brain = new QaQuestBrain(GameData.Embedded, session, 1, 45, new QaBotOptions());
        const int preyKey = 56015;
        for (var i = 0; i < QuestDriverState.CastRefusalBlacklistAfter - 1; i++)
        {
            session.NoteQaCastRefusal(preyKey, "out_of_range");
            brain.NoteClientCastOutcome(session);
        }
        Assert.DoesNotContain(preyKey, brain.DriverState.CastRefusalBlacklisted);
        Assert.Equal(preyKey, brain.DriverState.CombatTargetKey);
    }

    [Fact]
    public async Task Repeated_cast_refusals_blacklist_target_for_combat()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, T0, level: 10, qaBot: true);
        var brain = new QaQuestBrain(GameData.Embedded, session, 1, 45, new QaBotOptions());
        const int preyKey = 5555;
        for (var i = 0; i < QuestDriverState.CastRefusalBlacklistAfter; i++)
        {
            session.NoteQaCastRefusal(preyKey, "target_not_hostile");
            brain.NoteClientCastOutcome(session);
        }
        Assert.Contains(preyKey, brain.DriverState.CastRefusalBlacklisted);
    }
}