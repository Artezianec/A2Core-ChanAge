using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QaQuestBrainProgressTests
{
    const long Now = 1_700_100_000_000;
    static GameData Data => GameSessionTestHelpers.LiveGameData;

    [Fact]
    public async Task Driver_move_toward_advances_position_on_session()
    {
        var store = new FakeCharacterStore();
        var world = new SharedWorld(Data);
        var session = new GameSession(Data,
            new GameSessionOptions { World = world, CharacterLevel = 1, WorldId = store.WorldId, TrainingDummy = true },
            new Random(7)) { Store = store, Bot = true, QaBot = true };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(BotRequests.Create("QA_Move", 1), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(BotRequests.Select((uint)wireId << 16), Now);
        await session.HandleAsync(BotRequests.LoadComplete(1), Now);
        Assert.True(session.InWorld);
        var start = session.WorldPosition!.Value;
        var goal = start with { X = start.X + 500, Y = start.Y + 500 };
        var driver = new QuestStepDriver(Data, new QaBotOptions());
        var state = new QuestDriverState();
        var moved = false;
        for (var tick = 0; tick < 30; tick++)
        {
            var now = Now + tick * 250L;
            var me = session.WorldPosition ?? start;
            var step = InvokeMoveToward(driver, session, me, goal, 50, now, state, "test");
            if (step.Requests.Count == 0) continue;
            await session.HandleAsync(step.Requests[0], now);
            if (session.WorldPosition is { } p && Flat(p, start) > 100)
            {
                moved = true;
                break;
            }
        }
        Assert.True(moved, "QA movement driver should advance the session position toward the goal");
    }

    static QuestDriverSlice InvokeMoveToward(QuestStepDriver driver, GameSession session, Vector3d me, Vector3d goal, double within, long now, QuestDriverState state, string label)
    {
        var method = typeof(QuestStepDriver).GetMethod("MoveToward", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (QuestDriverSlice)method.Invoke(driver, [session, me, goal, within, now, state, label])!;
    }

    static double Flat(Vector3d a, Vector3d b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    [Fact]
    public void Poeta_kibelisk_for_HQ1101010_step3_is_on_map_1000()
    {
        const int artifactId = 1_051_035;
        Assert.True(Kibelisks.TryPlacement(artifactId, out var mapId, out _), "Expected teleport-artifacts entry");
        Assert.Equal(1000, mapId);
        var rel = Kibelisks.OnMap(1000).FirstOrDefault(k => k.ArtifactId == artifactId);
        Assert.NotNull(rel);
        Assert.Equal("TeleportArtifact_1051035", rel.Artifact);
    }
}