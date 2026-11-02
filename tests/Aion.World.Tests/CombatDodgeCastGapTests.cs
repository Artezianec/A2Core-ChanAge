using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Dodge must ignore the generic MinCastGapMs between skills; only dodge cooldown applies.</summary>
public sealed class CombatDodgeCastGapTests
{
    static readonly long Now = 1_700_000_200_000;

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    static byte[] SelfDodge(int skillId, int playerKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(playerKey).F32(0).Vec3(at).U8(9).ToArray();

    static bool CastAccepted(IReadOnlyList<byte[]> replies) =>
        replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

    [Fact]
    public async Task Dodge_right_after_another_skill_is_not_blocked_by_cast_gap()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(31));
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var at = session.Position ?? Vector3d.Zero;
        const int dodgeId = GameSession.CombatDodge;
        var attackAt = Now + 500;
        var attack = await session.HandleAsync(Cast(11020000, dummy.Key, dummy.Position), attackAt);
        Assert.True(CastAccepted(attack));
        var dodgeAt = attackAt + 50;
        var dodge = await session.HandleAsync(SelfDodge(dodgeId, session.PlayerKey, at), dodgeAt);
        Assert.NotEmpty(dodge);
        Assert.True(CastAccepted(dodge), "dodge within MinCastGapMs after a skill must be accepted when off cooldown");
    }

    [Fact]
    public async Task Dodge_pressed_while_previous_dodge_plays_is_ignored()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(32));
        const int dodgeId = GameSession.CombatDodge;
        var at = session.Position ?? Vector3d.Zero;
        var castAt = Now + 1000;
        var wire = SelfDodge(dodgeId, session.PlayerKey, at);
        Assert.True(CastAccepted(await session.HandleAsync(wire, castAt)));
        var early = await session.HandleAsync(wire, castAt + 50);
        Assert.Empty(early); // the 9-byte refusal mid-dash froze the client (live 2026-11-02)
    }
}
