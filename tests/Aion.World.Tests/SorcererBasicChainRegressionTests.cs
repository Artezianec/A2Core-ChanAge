using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Regression (live 2026-11-02, Sorcerer): held left click hit with all three combo steps but only step 1 animated, because
/// every early step press inside the swing lockout got a 0x3801 failure that cancelled the client's swing.
/// </summary>
public sealed class SorcererBasicChainRegressionTests
{
    const long Now = 1_760_010_000_000;

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(0).I32(skillId).U8(2).Varint(targetKey).F32(0)
            .F32((float)at.X).F32((float)at.Y).F32((float)at.Z).ToArray();

    static bool Accepted(IEnumerable<byte[]> replies) =>
        replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

    static bool Refused(IEnumerable<byte[]> replies) =>
        replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) != 0);

    static async Task<(GameSession, Target, Vector3d)> EnterAsync(uint classId, string name)
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var s = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true }, new Random(1)) { Store = store };
        await s.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await s.HandleAsync(new PacketWriter().U16(0x361b).String(name).U8(1).U32(classId).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await s.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await s.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var dummy = s.Targets![Combat.TrainingDummyKey];
        var at = new Vector3d(dummy.Position.X + 1200, dummy.Position.Y, dummy.Position.Z);
        s.SetPositionForTests(at);
        return (s, dummy, at);
    }

    [Theory]
    [InlineData(7u, 15_210_000, 15_030_000, 15_250_000)]   // Sorcerer Flame Arrow -> Burst -> Pyroclasm
    [InlineData(5u, 14_020_000, 14_030_000, 14_040_000)]   // Ranger Snipe -> Rapid Fire -> Spiral Arrow
    public async Task Held_left_click_chain_advances_one_two_three_one_without_failure_replies(uint classId, int s1, int s2, int s3)
    {
        var (session, dummy, at) = await EnterAsync(classId, "Chn" + classId);
        var t = Now + 5000;
        Assert.True(Accepted(await session.HandleAsync(Cast(s1, dummy.Key, at), t)));
        foreach (var step in new[] { s2, s3, s1 })
        {
            // The client switches the held slot to the next step and resends it every ~17 ms until it fires.
            var all = new List<byte[]>();
            var fired = false;
            for (var i = 0; i < 120 && !fired; i++)
            {
                t += 17;
                var r = await session.HandleAsync(Cast(step, dummy.Key, at), t);
                r.AddRange(await session.TickAsync(t));
                all.AddRange(r);
                fired = Accepted(r);
            }
            Assert.True(fired, $"step {step} never fired");
            Assert.False(Refused(all), $"step {step}: failure 0x3801 sent while held");
            Assert.Contains(all, p => GameSessionTestHelpers.Op(p) == 0x3802 && Convert.ToHexString(p).Contains(Convert.ToHexString(BitConverter.GetBytes(step))));
        }
    }
}
