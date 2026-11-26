using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Fall damage (GameSession.Fall.cs): HP loss without entering combat or drawing the weapon.</summary>
public sealed class FallDamageTests
{
    const long Now = 1_760_400_000_000;

    static byte[] Move(ushort op, Vector3d at) =>
        new PacketWriter().U16(op).U8(0).Vec3(at).ToArray();

    [Fact]
    public async Task Landing_from_a_long_fall_hurts_without_entering_combat()
    {
        // Live 2026-11-25: after a long fall the character drew its weapon on landing; official does not.
        var store = new FakeCharacterStore { Progress = (10, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        var here = session.Position!.Value;
        var hpBefore = session.Resource.Hp;
        await session.HandleAsync(Move(0x3702, here with { Z = here.Z + 3000 }), Now + 10);   // fall start, high up
        var landing = await session.HandleAsync(Move(0x3701, here), Now + 1200);            // land at the start point
        Assert.True(session.Resource.Hp < hpBefore);
        Assert.DoesNotContain(landing, p => GameSessionTestHelpers.Op(p) == 0x8d21);
    }

    [Fact]
    public async Task An_npc_hit_still_enters_combat()
    {
        var store = new FakeCharacterStore { Progress = (10, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 10);
        var hit = session.NpcHitsPlayer(100, Now + 10, killerKey: 2000);
        Assert.Contains(hit, p => GameSessionTestHelpers.Op(p) == 0x8d21);
        var fall = session.NpcHitsPlayer(100, Now + 20, killerKey: 0, enterCombat: false);
        Assert.DoesNotContain(fall, p => GameSessionTestHelpers.Op(p) == 0x8d21);
    }
}
