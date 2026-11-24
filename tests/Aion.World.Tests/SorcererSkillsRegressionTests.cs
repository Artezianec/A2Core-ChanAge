using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Sorcerer skills from live reports: Hellfire, Frost Burst, Wish of Concentration, Bittercold Wind zone level group.</summary>
public sealed class SorcererSkillsRegressionTests
{
    const long Now = 1_760_100_000_000;
    const int Hellfire = 15_060_000;
    const int FrostBurst = 15_220_000;
    const int Wish = 15_310_000;
    const int FrostBurstMark = 152_200_381;
    const int BittercoldWind = 15_280_000;

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    static async Task<(GameSession Session, Target Mob)> SorcererWithMobAsync()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (55, 0) };
        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Class: "Sorcerer" });
        store.Items.Add(FakeCharacterStore.EquippedItem(data.StarterWeapon(data.Profile(creation, 1)!)!.Value, 900, 1));
        var session = new GameSession(data,
            new GameSessionOptions { World = new SharedWorld(data), CharacterLevel = 55, WorldId = store.WorldId, TrainingDummy = true },
            new Random(19)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Sorc").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var mob = session.Targets![Combat.TrainingDummyKey];
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(mob.Position with { X = mob.Position.X + 600 }), Now + 1_000);
        session.SetResource('M', session.Resource!.MpMax);
        return (session, mob);
    }

    [Fact]
    public void Bittercold_zone_damage_uses_skill_025_level_table_not_028()
    {
        var zone = PcZones.For(BittercoldWind)!;
        var damage = zone.Damage;
        Assert.NotNull(damage);
        Assert.Equal("Sorcerer_Skill025_LvUp", damage!.LevelGroup);
    }

    [Fact]
    public void Specialized_hellfire_child_from_export_has_damage_rows()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var variant = 15_060_120;
        Assert.NotNull(data.Skill(variant));
        Assert.NotEmpty(data.Skill(variant)!.Damage);
    }

    [Fact]
    public async Task Hellfire_cast_deals_damage()
    {
        var (session, mob) = await SorcererWithMobAsync();
        var hp = mob.Hp;
        var at = session.Position!.Value;
        var replies = await session.HandleAsync(Cast(Hellfire, mob.Key, at), Now + 5_000);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        await session.TickAsync(Now + 7_000);
        Assert.True(mob.Hp < hp, "Hellfire should damage the target");
    }

    [Fact]
    public async Task Frost_burst_without_mark_is_refused()
    {
        var (session, mob) = await SorcererWithMobAsync();
        var at = session.Position!.Value;
        var replies = await session.HandleAsync(Cast(FrostBurst, mob.Key, at), Now + 5_000);
        Assert.DoesNotContain(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
    }

    [Fact]
    public async Task Frost_burst_with_mark_deals_damage()
    {
        var (session, mob) = await SorcererWithMobAsync();
        session.ApplyNpcDebuffForTests(mob, FrostBurstMark, 10_000, Now, skillId: FrostBurst);
        var hp = mob.Hp;
        var at = session.Position!.Value;
        var replies = await session.HandleAsync(Cast(FrostBurst, mob.Key, at), Now + 5_000);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        await session.TickAsync(Now + 7_000);
        Assert.True(mob.Hp < hp);
    }

    [Fact]
    public void Bittercold_specialized_variant_uses_family_zone()
    {
        const int variant = 15_280_120;
        Assert.NotNull(PcZones.For(variant));
        Assert.Equal(PcZones.For(BittercoldWind)!.Damage?.EffectId, PcZones.For(variant)!.Damage?.EffectId);
    }

    [Fact]
    public async Task Wish_of_concentration_applies_attack_and_accuracy_buff()
    {
        var (session, mob) = await SorcererWithMobAsync();
        _ = mob;
        var accuracyBefore = session.BuffStatForTests("Accuracy");
        var replies = await session.HandleAsync(Cast(Wish, session.PlayerKey, session.Position!.Value), Now + 5_000);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        Assert.Equal(100, session.BuffStatForTests("Accuracy") - accuracyBefore);
        Assert.Equal(1000, session.BuffStatForTests("DamageRatio"));
        Assert.Equal(1000, session.StatSheetForTests().Stats.FirstOrDefault(s => s.Type == 425).Value);
        Assert.True(session.StatWireValueForTests(28) >= 1000, "DamageRatio should appear on AmplifyAllDamage wire for the attack panel");
        Assert.Contains(153100001, session.ActiveBuffs);
    }
}