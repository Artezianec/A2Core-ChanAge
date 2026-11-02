using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Live 2026-11-02 Gladiator: dodge, Keen/Rupture/Wrathful, dodge, dodge again (cast_rejected) left the client stuck in the dash.</summary>
public sealed class GladiatorDodgeChainStuckTests
{
    const long Now = 1_760_011_000_000;
    const int Dodge = 11_000_100;

    static byte[] SkillCast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    static bool Accepted(IEnumerable<byte[]> r) => r.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

    static Task<GameSession> GladiatorAsync() => EnterAsync(1u, "GlaDodge");

    static async Task<GameSession> EnterAsync(uint classId, string name)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (25, 0) };
        var session = new GameSession(data,
            new GameSessionOptions { World = new SharedWorld(data), CharacterLevel = 25, WorldId = store.WorldId, TrainingDummy = true },
            new Random(41)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String(name).U8(1).U32(classId).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    [Theory]
    [InlineData(17)]
    [InlineData(100)]
    [InlineData(300)]
    [InlineData(600)]
    public async Task Dodge_chain_dodge_dodge_always_unlocks_the_client(int secondDodgeAfterMs)
    {
        var session = await GladiatorAsync();
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var at = session.Position ?? Vector3d.Zero;
        var t = Now + 5000;
        session.SetResource('S', session.Resource.SpMax);
        Assert.True(Accepted(await session.HandleAsync(SkillCast(Dodge, session.PlayerKey, at), t)));
        foreach (var step in new[] { 11_020_000, 11_030_000, 11_040_000 })
        {
            var fired = false;
            for (var i = 0; i < 120 && !fired; i++)
            {
                t += 17;
                var r = await session.HandleAsync(SkillCast(step, dummy.Key, at), t);
                r.AddRange(await session.TickAsync(t));
                fired = Accepted(r);
            }
            Assert.True(fired, $"step {step}");
            t += 500; // the swing plays out
        }
        t += 50;
        session.SetResource('S', session.Resource.SpMax);
        var d1 = await session.HandleAsync(SkillCast(Dodge, session.PlayerKey, at), t);
        Assert.True(Accepted(d1), "first dodge after chain");
        var d2 = await session.HandleAsync(SkillCast(Dodge, session.PlayerKey, at), t + secondDodgeAfterMs);
        // A dodge pressed while the first still plays is ignored (no reply); later it is accepted. Never the 9-byte
        // DodgeResult refusal (result 1), which froze the client mid-dash.
        Assert.True(d2.Count == 0 || Accepted(d2), $"second dodge after {secondDodgeAfterMs} ms: {string.Join(' ', d2.Select(Convert.ToHexString))}");
        Assert.DoesNotContain(d2, IsDodgeRefusal);
        Assert.DoesNotContain(d2, p => GameSessionTestHelpers.Op(p) == 0x3806);
        // And the next dodge after the refusal is accepted normally.
        session.SetResource('S', session.Resource.SpMax);
        Assert.True(Accepted(await session.HandleAsync(SkillCast(Dodge, session.PlayerKey, at), t + secondDodgeAfterMs + 700)));
    }

    static bool IsDodgeRefusal(byte[] p) => GameSessionTestHelpers.Op(p) == 0x3801 && p.Length == 9 && p[2] != 0;

    [Theory]
    [InlineData(17)]
    [InlineData(100)]
    [InlineData(250)]
    public async Task Assassin_slice_chain_dodge_dodge_never_sends_dodge_refusal(int secondDodgeAfterMs)
    {
        const int AssassinDodge = 13_000_100;
        var session = await EnterAsync((uint)Enumerable.Range(1, 64).First(c => GameSessionTestHelpers.LiveGameData.Profile(c, 1) is { Race: 1, Class: "Assassin" }), "AsnDdg" + secondDodgeAfterMs);
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var at = session.Position ?? Vector3d.Zero;
        var t = Now + 5000;
        session.SetResource('S', session.Resource.SpMax);
        foreach (var step in new[] { 13_010_000, 13_030_000, 13_040_000 })
        {
            var fired = false;
            for (var i = 0; i < 120 && !fired; i++)
            {
                t += 17;
                var r = await session.HandleAsync(SkillCast(step, dummy.Key, at), t);
                r.AddRange(await session.TickAsync(t));
                fired = Accepted(r);
            }
            Assert.True(fired, $"step {step}");
        }
        t += 17;
        Assert.True(Accepted(await session.HandleAsync(SkillCast(AssassinDodge, session.PlayerKey, at), t)), "first dodge");
        var all = new List<byte[]>();
        for (var dt = secondDodgeAfterMs; dt < secondDodgeAfterMs + 120; dt += 17)
        {
            var r = await session.HandleAsync(SkillCast(AssassinDodge, session.PlayerKey, at), t + dt);
            all.AddRange(r);
        }
        Assert.DoesNotContain(all, IsDodgeRefusal);
        session.SetResource('S', session.Resource.SpMax);
        t += 1500;
        await session.TickAsync(t);
        Assert.True(Accepted(await session.HandleAsync(SkillCast(AssassinDodge, session.PlayerKey, at), t)), "dodge after the first ended");
    }
}
