using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QaQuestBrainHeartbeatTests
{
    [Fact]
    public void NoteDeath_clears_stale_action_labels_for_heartbeat()
    {
        var store = new FakeCharacterStore();
        var world = new SharedWorld(GameData.Embedded);
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { World = world }, new Random(1))
        { Store = store, Bot = true, QaBot = true };
        var brain = new QaQuestBrain(GameData.Embedded, session, race: 1, levelTo: 12, new QaBotOptions());
        brain.DriverState.PendingEnvUntil = 9_999;
        brain.DriverState.PendingEnvKey = 123;
        brain.NoteDeath(2_000);
        Assert.Empty(brain.RecentActions);
        Assert.Null(brain.DriverState.PendingEnvKey);
    }
}