using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Ranger FixSlot combo after #447: every chain step must deal damage when accepted early; restarting Snipe after the finisher
/// must not stay blocked on step-1 cooldown.
/// </summary>
public sealed class RangerBasicChainRegressionTests
{
    const long Now = 1_760_020_000_000;
    const int Snipe = 14_020_000, RapidFire = 14_030_000, SpiralArrow = 14_040_000;

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(0).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).ToArray();

    static byte[] BasicAtk(int targetKey) =>
        new PacketWriter().U16(GameSession.BasicAtkRq).U8(0).Varint(targetKey).ToArray();

    static async Task<GameSession> RangerAsync()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { World = new SharedWorld(GameSessionTestHelpers.LiveGameData), CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
            new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("RangerChain").U8(1).U32(5).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    static async Task DrainRapidFireHitsAsync(GameSession session, long castAt)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var rapid = data.Skill(RapidFire)!;
        var lastRow = SkillTiming.HitLandMs(rapid, rapid.Damage.Length - 1);
        var area = SkillAreas.For(RapidFire)!;
        var flight = area.FlightMs(700);
        await session.TickAsync(castAt + lastRow + flight + 100);
    }

    static async Task DrainSnipeHitAsync(GameSession session, long castAt)
    {
        var area = SkillAreas.For(Snipe)!;
        var flight = area.FlightMs(700);
        await session.TickAsync(castAt + SkillTiming.ProjectileReleaseMs + flight + 100);
    }

    static async Task DrainSpiralHitAsync(GameSession session, long castAt)
    {
        var area = SkillAreas.For(SpiralArrow)!;
        var flight = area.FlightMs(700);
        await session.TickAsync(castAt + SkillTiming.ProjectileReleaseMs + flight + 100);
    }

    [Fact]
    public async Task Fast_chain_steps_each_reduce_dummy_hp()
    {
        var session = await RangerAsync();
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var at = dummy.Position;
        var t = Now + 5000;
        var hp0 = dummy.Hp;

        var snipe = await session.HandleAsync(Cast(Snipe, dummy.Key, at), t);
        Assert.Contains(snipe, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        await DrainSnipeHitAsync(session, t);
        Assert.True(dummy.Hp < hp0, "Snipe should land after Rapid Fire cancels its cast end");

        var hp1 = dummy.Hp;
        t += session.HeldCastLockoutRemainingMs(t) + 1;
        var rapid = await session.HandleAsync(Cast(RapidFire, dummy.Key, at), t);
        Assert.Contains(rapid, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        Assert.Contains(rapid, p => GameSessionTestHelpers.Op(p) == 0x3806);
        await DrainRapidFireHitsAsync(session, t);
        Assert.True(dummy.Hp < hp1, "Rapid Fire must deal damage even when Spiral Arrow follows quickly");

        var hp2 = dummy.Hp;
        t += session.HeldCastLockoutRemainingMs(t) + 1;
        var spiral = await session.HandleAsync(Cast(SpiralArrow, dummy.Key, at), t);
        Assert.Contains(spiral, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        await DrainSpiralHitAsync(session, t);
        Assert.True(dummy.Hp < hp2);
    }

    [Fact]
    public async Task Full_basic_combo_via_left_click_then_restarts_without_step1_cooldown()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var session = await RangerAsync();
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var t = Now + 8000;
        var gap = SkillCooldown.LockoutMs(data.Skill(Snipe)!, projectile: true);

        Assert.Equal((ushort)0, BitConverter.ToUInt16(
            (await session.HandleAsync(BasicAtk(dummy.Key), t)).First(p => GameSessionTestHelpers.Op(p) == GameSession.BasicAtkRs), 2));
        await DrainSnipeHitAsync(session, t);

        t += gap;
        Assert.Equal((ushort)0, BitConverter.ToUInt16(
            (await session.HandleAsync(BasicAtk(dummy.Key), t)).First(p => GameSessionTestHelpers.Op(p) == GameSession.BasicAtkRs), 2));
        await DrainRapidFireHitsAsync(session, t);

        t += gap;
        Assert.Equal((ushort)0, BitConverter.ToUInt16(
            (await session.HandleAsync(BasicAtk(dummy.Key), t)).First(p => GameSessionTestHelpers.Op(p) == GameSession.BasicAtkRs), 2));
        await DrainSpiralHitAsync(session, t);

        t += session.HeldCastLockoutRemainingMs(t) + 1;
        var restart = await session.HandleAsync(BasicAtk(dummy.Key), t);
        Assert.Equal((ushort)0, BitConverter.ToUInt16(restart.First(p => GameSessionTestHelpers.Op(p) == GameSession.BasicAtkRs), 2));
        Assert.Contains(restart, p => GameSessionTestHelpers.Op(p) == 0x3802);
        Assert.DoesNotContain(restart, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == GameSession.CastNotReadyResult);
    }
}
