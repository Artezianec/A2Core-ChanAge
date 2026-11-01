using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Water spirit owner buff and Dimensional Control enabler (capture 000220).</summary>
public sealed class DimensionalControlSummonTests
{
    const long Now = 1_760_003_000_000;
    const int WaterSummon = 16_110_000;
    const int FireSummon = 16_100_000;
    const int DimensionalControl = GameSession.DimensionalControlSkillId;

    static byte[] Cast(int skill, int target) =>
        new PacketWriter().U16(0x3800).U16(0).I32(skill).U8(2).Varint(target).F32(0).F32(100).F32(200).F32(300).ToArray();

    static async Task<GameSession> ElementalistAsync()
    {
        var data = GameData.Embedded;
        var store = new FakeCharacterStore { Progress = (55, 0) };
        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Race: 1 } p && p.Class == "Elementalist");
        store.Items.Add(FakeCharacterStore.EquippedItem(data.StarterWeapon(data.Profile(creation, 1)!)!.Value, 900, 1));
        var session = new GameSession(data,
            new GameSessionOptions { World = new SharedWorld(data), CharacterLevel = 55, WorldId = store.WorldId, TrainingDummy = true },
            new Random(7)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("El").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    static byte[] CastAt(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    [Fact]
    public async Task Water_spirit_applies_link_abnormal_then_owner_buff_and_dimensional_enabler()
    {
        var session = await ElementalistAsync();
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var t = Now + 10_000;
        session.SummonFromSkillForTest(WaterSummon, dummy.Key, t);
        session.ElementalActivateTick(t + 1);
        Assert.Contains(session.ActiveBuffs, id => id == ElementalSummons.PlayerSpiritLinkAbnormal(2));

        var buff = session.SpiritOwnerBuffTick(t + GameSession.SpiritOwnerBuffDelayMs);
        Assert.Contains(buff, p => GameSessionTestHelpers.Op(p) == 0x3802);
        Assert.Contains(buff, p => GameSessionTestHelpers.Op(p) == 0x3804);
        Assert.Contains(session.ActiveBuffs, id => id == GameSession.DimensionalControlEnablerAbnormal);
    }

    [Fact]
    public async Task Fire_spirit_casts_owner_buff_without_dimensional_enabler()
    {
        var session = await ElementalistAsync();
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var t = Now + 10_000;
        session.SummonFromSkillForTest(FireSummon, dummy.Key, t);
        session.ElementalActivateTick(t + 1);
        session.SpiritOwnerBuffTick(t + GameSession.SpiritOwnerBuffDelayMs);
        Assert.DoesNotContain(session.ActiveBuffs, id => id == GameSession.DimensionalControlEnablerAbnormal);
    }

    [Fact]
    public async Task Dimensional_control_consumes_enabler_on_cast_accept()
    {
        var session = await ElementalistAsync();
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var t = Now + 10_000;
        session.SummonFromSkillForTest(WaterSummon, dummy.Key, t);
        session.ElementalActivateTick(t + 1);
        session.SpiritOwnerBuffTick(t + GameSession.SpiritOwnerBuffDelayMs);
        Assert.Contains(session.ActiveBuffs, id => id == GameSession.DimensionalControlEnablerAbnormal);

        var cast = await session.HandleAsync(CastAt(DimensionalControl, dummy.Key, dummy.Position), t + 500);
        Assert.Contains(cast, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        Assert.Contains(cast, p => GameSessionTestHelpers.Op(p) == 0x382c);
        Assert.DoesNotContain(session.ActiveBuffs, id => id == GameSession.DimensionalControlEnablerAbnormal);
    }
}
