using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Cleric Bolt (17060000): live report — no damage/FX when casting a specialization child id; 0x3804 must carry
/// <see cref="SkillSideData.WireEffectId"/> like self-buffs and zones (Sorcerer #628).
/// </summary>
public sealed class ClericBoltTests
{
    const int Bolt = 17_060_000;
    const int BoltChild = 17_060_010;
    const long Now = 1_760_200_000_000;

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    static bool ContainsI32(byte[] packet, int value)
    {
        for (var i = 0; i + 4 <= packet.Length; i++)
            if (BitConverter.ToInt32(packet, i) == value) return true;
        return false;
    }

    [Fact]
    public void Bolt_parent_and_child_share_damage_rows_via_specialized_skills()
    {
        var parent = GameData.Embedded.Skill(Bolt)!;
        var child = GameData.Embedded.Skill(BoltChild)!;
        Assert.Equal("Bolt", parent.Name);
        Assert.Equal(2, parent.Damage.Length);
        Assert.NotNull(child);
        Assert.Equal(170_600_0011, parent.Damage[0].EffectId);
        Assert.Equal(parent.Damage[0].EffectId, child!.Damage[0].EffectId);
    }

    [Fact]
    public async Task Parent_cast_deals_damage_with_parent_effect_id_on_hit()
    {
        var (session, _) = await SkillEffectTests.Setup("Cleric");
        session.LearnForTests(Bolt);
        session.SetResource('M', session.Resource!.MpMax);
        var mob = session.Targets![Combat.TrainingDummyKey];
        var hp = mob.Hp;
        var castAt = Now + 5_000;
        Assert.Contains(
            await session.HandleAsync(Cast(Bolt, mob.Key, mob.Position), castAt),
            p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        var land = await session.TickAsync(castAt + 2_000);
        Assert.True(mob.Hp < hp);
        var wired = SkillSideData.WireEffectId(170_600_0011, Bolt);
        Assert.Equal(170_600_0011, wired);
        Assert.Contains(land, p => GameSessionTestHelpers.Op(p) == 0x3804 && ContainsI32(p, wired));
    }

    [Fact]
    public async Task Child_cast_wires_hit_effect_id_and_deals_damage()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (55, 0) };
        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Class: "Cleric" });
        store.Items.Add(FakeCharacterStore.EquippedItem(data.StarterWeapon(data.Profile(creation, 1)!)!.Value, 900, 1));
        store.Skills.Add(new SkillState(Bolt, 12, null, 12));
        var session = new GameSession(data,
            new GameSessionOptions { World = new SharedWorld(data), CharacterLevel = 55, WorldId = store.WorldId, TrainingDummy = true },
            new Random(33)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Bolt").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var dummy = session.Targets![Combat.TrainingDummyKey].Position;
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(dummy with { X = dummy.X + 700 }), Now + 1_000);
        session.SetSkillPartForTests(Bolt, 1, BoltChild);
        session.LearnForTests(BoltChild);
        session.SetResource('M', session.Resource!.MpMax);
        var mob = session.Targets![Combat.TrainingDummyKey];
        var hp = mob.Hp;
        var castAt = Now + 8_000;
        var wired = SkillSideData.WireEffectId(170_600_0011, BoltChild);
        Assert.Equal(170_600_1011, wired);
        Assert.Contains(
            await session.HandleAsync(Cast(BoltChild, mob.Key, mob.Position), castAt),
            p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        var land = await session.TickAsync(castAt + 2_000);
        Assert.True(mob.Hp < hp);
        Assert.Contains(land, p => GameSessionTestHelpers.Op(p) == 0x3804 && ContainsI32(p, wired));
    }
}