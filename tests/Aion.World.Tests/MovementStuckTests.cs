using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class MovementStuckTests
{
    const long Now = 1_760_500_000_000;
    const int StunDebuff = 18_017_800;

    static byte[] MoveUpdate(Vector3d at) => new PacketWriter().U16(0x3701).U8(2).Vec3(at).Zeros(16).U8(0).I64(0).ToArray();

    [Fact]
    public async Task Stale_expired_cc_does_not_block_movement()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterMap1010Async(store, Now);
        var start = session.Position!.Value;
        session.TryApplyPlayerDebuff(StunDebuff, 5000, Now);
        Assert.True(session.HeldByAbnormal);
        session.ForceExpireBuffForTests(StunDebuff);
        Assert.False(session.HeldByAbnormal);
        var step = start with { X = start.X + 200 };
        await session.HandleAsync(MoveUpdate(step), Now + 200);
        Assert.Equal(step, session.Position);
    }

    [Fact]
    public async Task Map_load_clears_knockback_displacement_lock()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterMap1010Async(store, Now);
        var at = session.Position!.Value;
        session.DisplacedBy(at with { X = at.X - 500 }, Combat.TrainingDummyKey, effectId: 1, amount: 800, pull: false, now: Now);
        Assert.True(session.PendingMoveStopCount > 0);
        session.Teleport(session.MapId!.Value, at, Now + 50);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now + 50);
        Assert.Equal(0, session.PendingMoveStopCount);
        var step = at with { X = at.X + 300 };
        await session.HandleAsync(MoveUpdate(step), Now + 200);
        Assert.Equal(step, session.Position);
    }
}