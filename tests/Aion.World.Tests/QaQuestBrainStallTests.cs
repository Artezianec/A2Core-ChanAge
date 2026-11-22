using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QaQuestBrainStallTests
{
    const long Now = 1_760_100_000_000;

    [Fact]
    public async Task Movement_alone_does_not_reset_no_progress_timer()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now, level: 4, qaBot: true);
        await session.QuestCommandAsync("QuestAdd", "HQ1101018", default);
        for (var i = 0; i < 3; i++)
            await session.QuestCommandAsync("QuestCompleteStep", "HQ1101018", default);
        Assert.Equal(4, session.Quests!.Get("HQ1101018")!.Step);

        var brain = new QaQuestBrain(GameData.Embedded, session, 1, 45,
            new QaBotOptions { StepTimeoutMs = 8000, PathTimeoutMs = 60_000, ActionLoopTimeoutMs = 600_000 });
        brain.Think(Now + 100);
        session.Teleport(1000, new Vector3d(-25000, -51000, 1000), Now + 200);
        brain.Think(Now + 300);
        session.Teleport(1000, new Vector3d(-20800, -44400, 1000), Now + 400);
        brain.Think(Now + 500);
        var stall = brain.Think(Now + 9_000).Stuck;
        Assert.NotNull(stall);
        Assert.Equal(QaStuckReason.NoProgress, stall!.ReasonCode);
    }
}