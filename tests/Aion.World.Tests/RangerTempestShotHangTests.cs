using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Regression (live 2026-11-02 21:00, Ranger): Tempest Shot spam (cast_gap / cast_rejected), kills, then Rapid Fire; the
/// character could only run afterwards. Every cast wire must get a reply within a bounded time and casting must resume.
/// </summary>
public sealed class RangerTempestShotHangTests
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

    const int Tempest = 14_340_000;

    [Theory]
    [InlineData(17)]
    [InlineData(60)]
    public async Task Tempest_spam_then_held_chain_never_leaves_an_unanswered_cast(int resendMs)
    {
        var (session, dummy, at) = await EnterAsync(5u, "Tmp" + resendMs);
        var t = Now + 5000;
        var lastReply = t;
        var tempestAccepts = 0;
        async Task<List<byte[]>> Send(int skill)
        {
            t += resendMs;
            var r = await session.HandleAsync(Cast(skill, dummy.Key, at), t);
            r.AddRange(await session.TickAsync(t));
            if (r.Any(p => GameSessionTestHelpers.Op(p) == 0x3801)) lastReply = t;
            Assert.True(t - lastReply <= 1500, $"no 0x3801 reply for {t - lastReply} ms (skill {skill})");
            return r;
        }
        for (var i = 0; i < 60; i++)
            if (Accepted(await Send(Tempest))) tempestAccepts++;
        Assert.True(tempestAccepts >= 2, "Tempest Shot never re-accepted");
        foreach (var step in new[] { 14_020_000, 14_030_000, 14_040_000, 14_020_000 })
        {
            var fired = false;
            for (var i = 0; i < 120 && !fired; i++) fired = Accepted(await Send(step));
            Assert.True(fired, $"chain step {step} never fired");
        }
        // Key released: tick until everything settles; nothing may stay queued and casting must resume.
        for (var i = 0; i < 100; i++) { t += 17; await session.TickAsync(t); }
        t += 3000;
        await session.TickAsync(t);
        session.SetResource('M', session.Resource.MpMax);
        var again = await session.HandleAsync(Cast(Tempest, dummy.Key, at), t);
        Assert.True(Accepted(again), "Tempest Shot refused after the spam: client would stay hung");
        t += 1000;
        Assert.True(Accepted(await session.HandleAsync(Cast(14_020_000, dummy.Key, at), t)), "Snipe refused after the spam");
    }

    [Fact]
    public async Task Tempest_kills_mobs_then_chain_every_start_is_ended_and_casting_resumes()
    {
        var (session, dummy, _) = await EnterAsync(5u, "TmpKill");
        var mobs = session.Targets!.Values.Where(x => x.Key != dummy.Key && x.Hp > 0)
            .OrderBy(x => x.Position.X).Take(40).ToList();
        var t = Now + 5000;
        var starts = 0; var ends = 0; var lastReply = t; var kills = 0;
        async Task<List<byte[]>> Send(int skill, Target m, Vector3d at)
        {
            t += 17;
            var r = await session.HandleAsync(Cast(skill, m.Key, at), t);
            r.AddRange(await session.TickAsync(t));
            Count(r);
            if (r.Any(p => GameSessionTestHelpers.Op(p) == 0x3801)) lastReply = t;
            Assert.True(t - lastReply <= 1500, $"no 0x3801 reply for {t - lastReply} ms (skill {skill}, mob {m.Key} hp {m.Hp})");
            return r;
        }
        void Count(List<byte[]> r)
        {
            starts += r.Count(p => GameSessionTestHelpers.Op(p) == 0x3802);
            ends += r.Count(p => GameSessionTestHelpers.Op(p) == 0x3806);
        }
        foreach (var mob in mobs.Take(3))
        {
            mob.Hp = Math.Min(mob.Hp, 900);
            var at = new Vector3d(mob.Position.X + 1200, mob.Position.Y, mob.Position.Z);
            session.SetPositionForTests(at);
            for (var i = 0; i < 80 && mob.Hp > 0; i++) await Send(Tempest, mob, at);
            // Client keeps resending at the corpse a few frames.
            for (var i = 0; i < 6; i++) await Send(Tempest, mob, at);
            if (mob.Hp <= 0) kills++;
            session.SetResource('M', session.Resource.MpMax);
            var next = mobs[3 + kills];
            var at2 = new Vector3d(next.Position.X + 1200, next.Position.Y, next.Position.Z);
            session.SetPositionForTests(at2);
            foreach (var step in new[] { 14_020_000, 14_030_000 })
            {
                var fired = false;
                for (var i = 0; i < 120 && !fired; i++) fired = Accepted(await Send(step, next, at2));
                Assert.True(fired, $"chain step {step} never fired after kill");
            }
        }
        Assert.True(kills > 0, "no mob died");
        for (var i = 0; i < 200; i++) { t += 17; Count(await session.TickAsync(t)); }
        Assert.Equal(starts, ends);
    }

    [Fact]
    public async Task Held_combo_press_that_cannot_fire_is_answered_within_the_silent_queue_cap()
    {
        var (session, dummy, at) = await EnterAsync(5u, "TmpCap");
        var t = Now + 5000;
        Assert.True(Accepted(await session.HandleAsync(Cast(14_020_000, dummy.Key, at), t)));
        // Out of MP: Rapid Fire keeps failing its rules at every drain; the held press must still get a reply.
        session.SetResource('M', 0);
        var replied = false;
        for (var i = 0; i < 150 && !replied; i++)
        {
            t += 17;
            var r = await session.HandleAsync(Cast(14_030_000, dummy.Key, at), t);
            r.AddRange(await session.TickAsync(t));
            replied = r.Any(p => GameSessionTestHelpers.Op(p) == 0x3801);
        }
        Assert.True(replied, "held combo press never answered");
    }
}
