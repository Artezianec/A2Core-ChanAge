using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class QaEnterVolumePcTests
{
    const long Now = 1_760_030_000_000;

    [Fact]
    public async Task HQ2102010_enter_volume_sends_move_when_within_coarse_distance()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(store, Now, level: 4);
        await session.QuestCommandAsync("QuestAdd", "HQ2102010", default);
        var trigger = WorldObjects.TriggersOn(1110).First(t => t.Name == "Trigger_HQ2102010_01");
        var near = trigger.At with { X = trigger.At.X - 100, Y = trigger.At.Y - 30 };
        session.Teleport(1110, near, Now + 10);
        await session.HandleAsync(BotRequests.LoadComplete(1), Now + 20);
        var driver = new QuestStepDriver(GameData.Embedded);
        var state = new QuestDriverState();
        var slice = driver.NextSlice(session, "HQ2102010", Now + 100, state);
        Assert.Contains("enter:EnterVolumePC", slice.ActionLabel, StringComparison.Ordinal);
        Assert.Equal(QuestDriverKind.Act, slice.Kind);
        Assert.Single(slice.Requests);
        var op = BitConverter.ToUInt16(slice.Requests[0], 0);
        Assert.True(op is 0x3700 or 0x3701, $"opcode {op:X4}");
    }
}