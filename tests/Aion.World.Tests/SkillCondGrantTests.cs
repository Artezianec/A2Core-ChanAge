using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Ambush skill_cond_grant unlocks Whirlwind Slice (SkillCond self group 130600471).</summary>
public sealed class SkillCondGrantTests
{
    const long Now = 1_761_000_000_000;
    const int Ambush = 13_060_000;
    const int Whirlwind = 13_210_000;
    const int Group = 130_600_471;

    static byte[] Cast(int skill, int target) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skill).U8(2).Varint(target).F32(0).Vec3(Vector3d.Zero).U8(9).ToArray();

    static bool Accepted(IReadOnlyList<byte[]> replies) =>
        replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

    [Fact]
    public async Task Ambush_grant_enables_whirlwind()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var data = GameSessionTestHelpers.LiveGameData;
        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Class: "Assassin" });
        store.Items.Add(FakeCharacterStore.EquippedItem(data.StarterWeapon(data.Profile(creation, 1)!)!.Value, 900, 1));
        var session = new GameSession(data,
            new GameSessionOptions { World = new SharedWorld(data), CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
            new Random(41)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Cond").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        session.LearnForTests(Ambush);
        session.LearnForTests(Whirlwind);
        session.SetResource('M', session.Resource!.MpMax);
        var mob = session.Targets![Combat.TrainingDummyKey];
        Assert.False(Accepted(await session.HandleAsync(Cast(Whirlwind, mob.Key), Now + 500)));
        Assert.True(Accepted(await session.HandleAsync(Cast(Ambush, mob.Key), Now + 1000)));
        Assert.Contains(Group, session.ActiveBuffs);
        Assert.True(Accepted(await session.HandleAsync(Cast(Whirlwind, mob.Key), Now + 1500)));
    }
}