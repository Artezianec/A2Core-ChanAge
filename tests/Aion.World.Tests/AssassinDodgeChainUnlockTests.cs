using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Live playtest: dodge → Quick Slice → dodge → Breaking → Swift (cast_rejected + silent drop) → dodge freeze.
/// Chain steps must bypass cast_gap; every rejection must include a client unlock packet.
/// </summary>
public sealed class AssassinDodgeChainUnlockTests
{
    const long Now = 1_760_010_000_000;
    const int Dodge = 13000100, Quick = 13010000, Breaking = 13030000, Swift = 13040000;

    static int AssassinCreation => Enumerable.Range(1, 64).First(c =>
        GameSessionTestHelpers.LiveGameData.Profile(c, 1) is { Race: 1, Class: "Assassin" });

    static async Task<GameSession> AssassinAsync()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (25, 0) };
        var session = new GameSession(data,
            new GameSessionOptions { World = new SharedWorld(data), CharacterLevel = 25, WorldId = store.WorldId, TrainingDummy = true },
            new Random(41)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("DodgeChain").U8(1).U32((uint)AssassinCreation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    static byte[] SkillCast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    static byte[] SelfDodge(Vector3d at, int playerKey) =>
        new PacketWriter().U16(0x3800).U16(16).I32(Dodge).U8(2).Varint(playerKey).F32(0).Vec3(at).U8(9).ToArray();

    static bool CastAccepted(IReadOnlyList<byte[]> replies) =>
        replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

    static void AssertCastUnlock(IReadOnlyList<byte[]> replies, string step)
    {
        Assert.NotEmpty(replies);
        Assert.True(replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801), step);
    }

    [Fact]
    public async Task Swift_slice_accepts_immediately_after_breaking_in_chain()
    {
        var session = await AssassinAsync();
        var at = session.Position ?? Vector3d.Zero;
        var t = Now + 2000;
        Assert.True(CastAccepted(await session.HandleAsync(SkillCast(Quick, session.PlayerKey, at), t)));
        t += 400;
        Assert.True(CastAccepted(await session.HandleAsync(SkillCast(Breaking, session.PlayerKey, at), t)));
        Assert.True(session.ActiveChainSkills.ContainsKey(Swift));
        var swift = await session.HandleAsync(SkillCast(Swift, session.PlayerKey, at), t + 40);
        AssertCastUnlock(swift, "Swift");
        Assert.True(CastAccepted(swift));
    }

    [Fact]
    public async Task Playtest_sequence_dodge_combo_and_swift_after_dodge_never_reply_empty()
    {
        var session = await AssassinAsync();
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var at = session.Position ?? Vector3d.Zero;
        var t = Now + 3000;

        for (var i = 0; i < 3; i++)
        {
            session.SetResource('S', session.Resource.SpMax);
            var dodge = await session.HandleAsync(SelfDodge(at, session.PlayerKey), t);
            AssertCastUnlock(dodge, $"dodge {i + 1}");
            Assert.True(CastAccepted(dodge), $"dodge {i + 1}");
            t += 600;
        }
        session.SetResource('S', session.Resource.SpMax);

        var quick = await session.HandleAsync(SkillCast(Quick, session.PlayerKey, at), t);
        AssertCastUnlock(quick, "Quick Slice");
        Assert.True(CastAccepted(quick));
        t += 120;

        var dodgeMid = await session.HandleAsync(SelfDodge(at, session.PlayerKey), t);
        AssertCastUnlock(dodgeMid, "dodge after quick");
        Assert.True(CastAccepted(dodgeMid));
        t += 120;

        var breaking = await session.HandleAsync(SkillCast(Breaking, session.PlayerKey, at), t);
        AssertCastUnlock(breaking, "Breaking Slice");
        Assert.True(CastAccepted(breaking));
        Assert.True(session.ActiveChainSkills.ContainsKey(Swift));
        var breakingAt = t;
        t += 40;

        var swift = await session.HandleAsync(SkillCast(Swift, session.PlayerKey, at), t);
        AssertCastUnlock(swift, "Swift Slice right after Breaking (chain bypasses cast_gap)");
        Assert.True(CastAccepted(swift), "Swift Slice should be accepted while chain window is open");

        session.SetResource('S', session.Resource.SpMax);
        var dodgeAfterSwift = await session.HandleAsync(SelfDodge(at, session.PlayerKey), t + 600);
        AssertCastUnlock(dodgeAfterSwift, "dodge after combo");
        Assert.True(CastAccepted(dodgeAfterSwift));
        var dodgeCd = await session.HandleAsync(SelfDodge(at, session.PlayerKey), t + 650);
        // Pressed while the previous dodge still plays: ignored, never the 9-byte refusal that froze the client mid-dash.
        Assert.Empty(dodgeCd);
    }

    [Fact]
    public async Task Dodge_mid_quick_swing_cancels_cast_and_unlocks_on_cooldown_rejection()
    {
        var session = await AssassinAsync();
        var at = session.Position ?? Vector3d.Zero;
        var t = Now + 7000;

        var quick = await session.HandleAsync(SkillCast(Quick, session.PlayerKey, at), t);
        AssertCastUnlock(quick, "Quick Slice");
        Assert.True(CastAccepted(quick));

        var breakingMidSwing = await session.HandleAsync(SkillCast(Breaking, session.PlayerKey, at), t + 20);
        AssertCastUnlock(breakingMidSwing, "Breaking during Quick swing");
        Assert.True(CastAccepted(breakingMidSwing), "chain step should cancel Quick and accept Breaking");
        Assert.Contains(breakingMidSwing, p => GameSessionTestHelpers.Op(p) == 0x3806);

        session.SetResource('S', session.Resource.SpMax);
        var dodge1 = await session.HandleAsync(SelfDodge(at, session.PlayerKey), t + 30);
        AssertCastUnlock(dodge1, "dodge mid Breaking");
        Assert.True(CastAccepted(dodge1));
        Assert.Contains(dodge1, p => GameSessionTestHelpers.Op(p) == 0x3806);

        session.SetResource('S', session.Resource.SpMax);
        var dodge2 = await session.HandleAsync(SelfDodge(at, session.PlayerKey), t + 650);
        AssertCastUnlock(dodge2, "second dodge after cooldown");
        Assert.True(CastAccepted(dodge2));

        session.SetResource('S', session.Resource.SpMax);
        var dodgeCd = await session.HandleAsync(SelfDodge(at, session.PlayerKey), t + 700);
        // Pressed while the previous dodge still plays: ignored, never the 9-byte refusal that froze the client mid-dash.
        Assert.Empty(dodgeCd);
    }

    [Fact]
    public async Task Rejected_chain_step_and_dodge_never_return_empty_replies()
    {
        var session = await AssassinAsync();
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var at = session.Position ?? Vector3d.Zero;
        var t = Now + 5000;

        Assert.True(CastAccepted(await session.HandleAsync(SkillCast(Quick, session.PlayerKey, at), t)));
        t += 4000;
        var swiftNoChain = await session.HandleAsync(SkillCast(Swift, session.PlayerKey, at), t);
        AssertCastUnlock(swiftNoChain, "Swift after combo window expires restarts at Quick Slice");
        Assert.Contains(swiftNoChain, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        Assert.Contains(swiftNoChain, p => GameSessionTestHelpers.Op(p) == 0x3802);

        Assert.True(CastAccepted(await session.HandleAsync(SelfDodge(at, session.PlayerKey), t)));
        var dodgeCd = await session.HandleAsync(SelfDodge(at, session.PlayerKey), t + 50);
        // Pressed while the previous dodge still plays: ignored, never the 9-byte refusal that froze the client mid-dash.
        Assert.Empty(dodgeCd);
    }
}
