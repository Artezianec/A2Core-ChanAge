using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Mocking Blade (11290000): taunt without holding the mob, knockback after taunt, wired damage hits (client crash regression).</summary>
public sealed class GladiatorMockingBladeTests
{
    const int MockingBlade = 11_290_000;
    const int Knockback = 100_000_321;
    const int Taunt = 112_900_072;
    const long Now = 1_760_260_000_000;

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    static bool Accepted(IReadOnlyList<byte[]> replies) =>
        replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

    static bool ContainsI32(byte[] packet, int value)
    {
        for (var i = 0; i + 4 <= packet.Length; i++)
            if (BitConverter.ToInt32(packet, i) == value) return true;
        return false;
    }

    static ushort Op(byte[] p) => GameSessionTestHelpers.Op(p);

    [Fact]
    public void Taunt_abnormal_is_not_a_hold_for_npc_combat()
    {
        var model = AbnormalSpecs.Npc(Taunt);
        Assert.True(model.Taunt);
        Assert.False(model.Held);
        var (held, _) = AbnormalCombat.NpcFlags(Taunt);
        Assert.False(held);
    }

    [Fact]
    public void Knockback_debuff_requires_taunt_on_target()
    {
        var area = SkillAreas.For(MockingBlade);
        Assert.NotNull(area);
        Assert.Contains(area!.TargetDebuffRows, r => r.Abnormal == Knockback);
        Assert.DoesNotContain(area.TargetDebuffRows, r => r.Abnormal == Taunt && r.Held);
    }

    [Fact]
    public async Task Mocking_blade_cast_deals_damage_and_sends_taunt_without_npc_hold()
    {
        var store = new FakeCharacterStore { Progress = (25, 0) };
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 25, rng: new Random(77));
        session.LearnForTests(MockingBlade);
        session.SetResource('M', session.Resource!.MpMax);
        var mob = session.Targets![Combat.TrainingDummyKey];
        var hp = mob.Hp;
        var castAt = Now + 1_000;
        Assert.True(Accepted(await session.HandleAsync(Cast(MockingBlade, mob.Key, mob.Position), castAt)));
        var land = await GameSessionTestHelpers.AfterSkillHitsAsync(session, castAt);
        land.AddRange(await session.TickAsync(castAt + 3_000));
        Assert.True(mob.Hp < hp, "Mocking Blade should deal damage");
        var wired = SkillSideData.WireEffectId(112_900_0021, MockingBlade);
        Assert.Contains(land, p => Op(p) == 0x3804 && ContainsI32(p, wired));
        Assert.Contains(land, p => Op(p) == 0x382a);
    }

    [Fact]
    public async Task Learnable_cast_audit_accepts_mocking_blade()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = new GameSession(data,
            new GameSessionOptions { World = new SharedWorld(data), CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
            new Random(78)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Mock").U8(1).U32(1).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        session.LearnForTests(MockingBlade);
        session.SetResource('M', session.Resource!.MpMax);
        var mob = session.Targets![Combat.TrainingDummyKey];
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(mob.Position with { X = mob.Position.X + 400 }), Now + 500);
        var row = await LearnableSkillCastAudit.ProbeAsync(session, data, "Gladiator", MockingBlade, Now + 2_000, mob);
        Assert.Equal(LearnableSkillCastAudit.RuntimeStatus.Ok, row.Status);
    }
}