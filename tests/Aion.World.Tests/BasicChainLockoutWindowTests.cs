using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>FixSlot combo lockout refusals and chain_window_ms expiry (Skill.dat 3000, official Ranger 2026-10-31 captures).</summary>
public sealed class BasicChainLockoutWindowTests
{
    const long Now = 1_760_030_000_000;
    const int RangerSnipe = 14_020_000, RangerRapid = 14_030_000;
    const int AssassinQuick = 13_010_000, AssassinBreaking = 13_030_000, AssassinSwift = 13_040_000;

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(0).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).ToArray();

    static byte[] BasicAtk(int targetKey) =>
        new PacketWriter().U16(GameSession.BasicAtkRq).U8(0).Varint(targetKey).ToArray();

    static async Task<GameSession> RangerAsync()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { World = new SharedWorld(GameSessionTestHelpers.LiveGameData), CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
            new Random(0)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("ChainLock").U8(1).U32(5).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    static async Task<GameSession> AssassinAsync()
    {
        var creation = Enumerable.Range(1, 64).First(c =>
            GameSessionTestHelpers.LiveGameData.Profile(c, 1) is { Race: 1, Class: "Assassin" });
        var store = new FakeCharacterStore { Progress = (25, 0) };
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { World = new SharedWorld(GameSessionTestHelpers.LiveGameData), CharacterLevel = 25, WorldId = store.WorldId, TrainingDummy = true },
            new Random(41)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("AsChain").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    [Fact]
    public void Ranger_chain_steps_export_three_second_combo_window()
    {
        var data = GameData.Embedded;
        Assert.Equal(3000, data.Skill(RangerRapid)!.ChainWindowMs);
        Assert.Equal(3000, data.Skill(14_040_000)!.ChainWindowMs);
    }

    [Fact]
    public async Task Ranger_early_rapid_is_queued_silently_without_advancing_chain()
    {
        var session = await RangerAsync();
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var at = dummy.Position;
        var t = Now + 5000;
        await session.HandleAsync(Cast(RangerSnipe, dummy.Key, at), t);
        Assert.True(session.ActiveChainSkills.ContainsKey(RangerRapid));

        var early = await session.HandleAsync(Cast(RangerRapid, dummy.Key, at), t + 30);
        // No 0x3801 failure: it cancelled the client's swing animation (live 2026-11-02). The queued wire is answered on drain.
        Assert.DoesNotContain(early, p => GameSessionTestHelpers.Op(p) == 0x3801);
        Assert.DoesNotContain(early, p => GameSessionTestHelpers.Op(p) == 0x3802);
        Assert.DoesNotContain(early, p => GameSessionTestHelpers.Op(p) == 0x3819);
        Assert.True(session.ActiveChainSkills.ContainsKey(RangerRapid));
    }

    [Fact]
    public async Task Ranger_after_combo_window_expires_basic_attack_restarts_snipe()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var session = await RangerAsync();
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var t = Now + 8000;
        await session.HandleAsync(Cast(RangerSnipe, dummy.Key, dummy.Position), t);
        var window = data.Skill(RangerRapid)!.ChainWindowMs;
        Assert.Equal(3000, window);

        var stale = await session.HandleAsync(Cast(RangerRapid, dummy.Key, dummy.Position), t + window + 50);
        Assert.Contains(stale, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        Assert.Contains(stale, p => GameSessionTestHelpers.Op(p) == 0x3802 && p.AsSpan().IndexOf(BitConverter.GetBytes(RangerSnipe)) >= 0);
        Assert.True(session.ActiveChainSkills.ContainsKey(RangerRapid), "fresh Snipe opens the next combo window");
    }

    [Fact]
    public async Task Assassin_swift_still_accepts_during_breaking_swing()
    {
        var session = await AssassinAsync();
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var at = session.Position ?? Vector3d.Zero;
        var t = Now + 10_000;
        Assert.True(await Accept(session, Cast(AssassinQuick, dummy.Key, at), t));
        Assert.True(await Accept(session, Cast(AssassinBreaking, dummy.Key, at), t + 400));
        Assert.True(session.ActiveChainSkills.ContainsKey(AssassinSwift));
        Assert.True(await Accept(session, Cast(AssassinSwift, dummy.Key, at), t + 440));
    }

    [Fact]
    public async Task Assassin_early_breaking_resend_does_not_close_chain_window()
    {
        var session = await AssassinAsync();
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var at = session.Position ?? Vector3d.Zero;
        var t = Now + 12_000;
        await session.HandleAsync(Cast(AssassinQuick, dummy.Key, at), t);
        Assert.True(session.ActiveChainSkills.ContainsKey(AssassinBreaking));
        var resend = await session.HandleAsync(Cast(AssassinQuick, dummy.Key, at), t + 20);
        Assert.Contains(resend, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == GameSession.CastNotReadyResult);
        Assert.True(session.ActiveChainSkills.ContainsKey(AssassinBreaking));
    }

    static async Task<bool> Accept(GameSession session, byte[] wire, long at)
    {
        var replies = await session.HandleAsync(wire, at);
        return replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
    }
}
