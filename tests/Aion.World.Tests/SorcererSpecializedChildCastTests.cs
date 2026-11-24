using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Sorcerer casts with specialization parts use child ids; side tables must resolve via SkillSideData.</summary>
public sealed class SorcererSpecializedChildCastTests
{
    const long Now = 1_760_100_000_000;
    const int WishParent = 15_310_000;
    const int WishChild = 15_310_010;
    const int BittercoldParent = 15_280_000;
    const int BittercoldChild = 15_280_010;
    const int HellfireParent = 15_060_000;
    const int HellfireChild = 15_060_010;
    const int FrostParent = 15_220_000;
    const int FrostChild = 15_220_010;
    const int FrostMark = 152_200_381;

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    static bool ContainsI32(byte[] packet, int value)
    {
        for (var i = 0; i + 4 <= packet.Length; i++)
            if (BitConverter.ToInt32(packet, i) == value) return true;
        return false;
    }

    static async Task<GameSession> SorcererWithPartAsync(int parent, int child)
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var store = new FakeCharacterStore { Progress = (55, 0) };
        var creation = Enumerable.Range(1, 64).First(c => data.Profile(c, 1) is { Class: "Sorcerer" });
        store.Items.Add(FakeCharacterStore.EquippedItem(data.StarterWeapon(data.Profile(creation, 1)!)!.Value, 900, 1));
        store.Skills.Add(new SkillState(parent, 12, null, 12));
        var session = new GameSession(data,
            new GameSessionOptions { World = new SharedWorld(data), CharacterLevel = 55, WorldId = store.WorldId, TrainingDummy = true },
            new Random(21)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("Spec").U8(1).U32((uint)creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        session.SetSkillPartForTests(parent, 1, child);
        session.LearnForTests(child);
        session.SetResource('M', session.Resource!.MpMax);
        _ = session.RefreshPassiveStatBuffs(Now);
        var mob = session.Targets![Combat.TrainingDummyKey];
        await session.HandleAsync(MapEventScenarioHelpers.MoveUpdate(mob.Position with { X = mob.Position.X + 600 }), Now + 1_000);
        return session;
    }

    [Theory]
    [InlineData(WishParent, WishChild)]
    [InlineData(BittercoldParent, BittercoldChild)]
    [InlineData(HellfireParent, HellfireChild)]
    [InlineData(FrostParent, FrostChild)]
    public void specialized_child_resolves_same_side_tables_as_parent(int parent, int child)
    {
        Assert.Equal(PortedSkillAbnormals.SelfRows(parent).Count, PortedSkillAbnormals.SelfRows(child).Count);
        var parentZone = PcZones.For(parent);
        var childZone = PcZones.For(child);
        if (parentZone is not null)
            Assert.Equal(parentZone.Npc, childZone!.Npc);
        var parentArea = SkillAreas.For(parent);
        var childArea = SkillAreas.For(child);
        if (parentArea is { Max: > 0 })
        {
            Assert.NotNull(childArea);
            Assert.Equal(parentArea.Max, childArea!.Max);
        }
    }

    [Fact]
    public async Task Wish_child_cast_applies_self_buff_wire_effect()
    {
        var session = await SorcererWithPartAsync(WishParent, WishChild);
        var at = session.Position!.Value;
        var replies = await session.HandleAsync(Cast(WishChild, session.PlayerKey, at), Now + 5_000);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3804 && ContainsI32(p, SkillSideData.WireEffectId(1531000011, WishChild)));
        Assert.Contains(153100001, session.ActiveBuffs);
    }

    [Fact]
    public async Task Bittercold_child_cast_places_zone_and_client_cast_fx()
    {
        var session = await SorcererWithPartAsync(BittercoldParent, BittercoldChild);
        var mob = session.Targets![Combat.TrainingDummyKey];
        var castAt = Now + 5_000;
        var replies = await session.HandleAsync(Cast(BittercoldChild, mob.Key, mob.Position), castAt);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3802 && ContainsI32(p, BittercoldChild));
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3641);
        Assert.Equal(1, session.PlacedZoneCount);
        var fx = SkillSideData.WireEffectId(1528000011, BittercoldChild);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3804 && ContainsI32(p, fx));
        await session.TickAsync(castAt + 700);
        Assert.True(mob.Hp < mob.HpMax);
    }

    [Fact]
    public void Hellfire_child_damage_row_uses_specialized_projectile_id()
    {
        var skill = GameSessionTestHelpers.LiveGameData.Skill(HellfireChild);
        Assert.NotNull(skill);
        Assert.Equal(150_600_101, skill!.Damage[0].Projectile);
    }

    [Fact]
    public async Task Hellfire_child_fires_row_projectile_and_damages()
    {
        var session = await SorcererWithPartAsync(HellfireParent, HellfireChild);
        var mob = session.Targets![Combat.TrainingDummyKey];
        var hp = mob.Hp;
        var at = session.Position!.Value;
        var castAt = Now + 5_000;
        var replies = await session.HandleAsync(Cast(HellfireChild, mob.Key, at), castAt);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        Assert.True(session.PendingCombatHitCountForTests > 0, "combat hits should be scheduled");
        var queued = session.NextProjectileForTests();
        Assert.NotNull(queued);
        Assert.Equal(150_600_101, queued!.Value.ProjectileId);
        var released = await session.TickAsync(queued.Value.DueMs);
        Assert.Equal(0, session.PendingProjectileCountForTests);
        var fire = Assert.Single(released, p => GameSessionTestHelpers.Op(p) == 0x3803);
        var reader = new PacketReader(fire) { Offset = 2 };
        reader.Varint(); reader.U8(); reader.Varint(); reader.I32();
        reader.U8();
        Assert.Equal(150_600_101u, reader.Varint());
        await session.TickAsync(castAt + 5_000);
        Assert.True(mob.Hp < hp);
    }

    [Fact]
    public async Task Frost_burst_child_with_mark_hits_area_up_to_four()
    {
        var session = await SorcererWithPartAsync(FrostParent, FrostChild);
        var at = session.Position!.Value;
        var map = Assert.IsType<Dictionary<int, Target>>(session.Targets);
        var main = map[Combat.TrainingDummyKey];
        main.Position = at;
        session.ApplyNpcDebuffForTests(main, FrostMark, 10_000, Now, skillId: FrostChild);
        for (var i = 0; i < 5; i++)
        {
            var key = 880_600 + i;
            map[key] = new Target
            {
                Key = key, Name = $"m{i}", NpcId = 2400032, Hp = 1_000_000, HpMax = 1_000_000, Relationship = 6,
                Position = at with { Y = at.Y + 100 + i * 80 },
            };
            session.ApplyNpcDebuffForTests(map[key], FrostMark, 10_000, Now, skillId: FrostChild);
        }
        var replies = await session.HandleAsync(Cast(FrostChild, main.Key, at), Now + 5_000);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        await session.TickAsync(Now + 7_000);
        var hurt = map.Values.Count(t => t.Hostile && t.Hp < t.HpMax);
        Assert.InRange(hurt, 1, 4);
    }
}