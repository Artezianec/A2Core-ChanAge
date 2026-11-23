using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Cast grey rules: SkillCond, weapon, spirit, resource thresholds (Skill.dat / SkillCond.dat).</summary>
public sealed class SkillCastConditionTests
{
    const long Now = 1_760_030_800_000;
    const int Jointstrike = 16_140_000;
    const int RapidScatter = 16_340_000;

    static byte[] Cast(int skill, int target) =>
        new PacketWriter().U16(0x3800).U16(0).I32(skill).U8(2).Varint(target).F32(0).Vec3(Vector3d.Zero).ToArray();

    static bool Accepted(List<byte[]> replies) =>
        replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

    static async Task<GameSession> ElementalistAsync()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { World = new SharedWorld(GameData.Embedded), CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
            new Random(3)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Cond").U8(1).U32(6).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    [Fact]
    public async Task Skill_cond_on_target_blocks_until_stagger_present()
    {
        var session = await ElementalistAsync();
        var dummy = session.Targets![Combat.TrainingDummyKey];
        await session.HandleAsync(Cast(16_100_000, dummy.Key), Now + 1000);
        Assert.False(Accepted(await session.HandleAsync(Cast(RapidScatter, dummy.Key), Now + 2000)));
        session.BreakNpcStaggerForTests(dummy, Now + 2500, Jointstrike);
        Assert.True(Accepted(await session.HandleAsync(Cast(RapidScatter, dummy.Key), Now + 2600)));
    }

    [Fact]
    public async Task Wrong_weapon_refuses_cast()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 45, rng: new Random(4));
        var dummy = session.Targets![Combat.TrainingDummyKey];
        const int bowSkill = 14_020_000;
        Assert.False(Accepted(await session.HandleAsync(Cast(bowSkill, dummy.Key), Now + 500)));
    }
}