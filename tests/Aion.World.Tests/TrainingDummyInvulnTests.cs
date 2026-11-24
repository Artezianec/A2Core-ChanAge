using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class TrainingDummyInvulnTests
{
    static readonly long Now = 1_700_500_000_000;

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

  [Theory]
    [InlineData(Combat.TrainingDummyKey, Combat.SandbagNpcId, "M_D1_FV_TraDummy_01_01_1", true)]
    [InlineData(2001, 999, "SomeMob", false)]
    [InlineData(2002, 2400035, "M_D1_DR_TraDummy_01_01_1", true)]
    public void IsTrainingDummy_matches_retail_targets(int key, int npcId, string name, bool expect)
    {
        var t = new Target
        {
            Key = key, Name = name, Hp = 100, HpMax = 100, Position = Vector3d.Zero, NpcId = npcId, Level = 10,
        };
        Assert.Equal(expect, Combat.IsTrainingDummy(t));
    }

    [Fact]
    public void HpAfterDamage_clamps_training_dummy_at_one_hp()
    {
        var t = new Target
        {
            Key = 2000, Name = "M_D1_FV_TraDummy_01_01_1", Hp = 2, HpMax = 5000, Position = Vector3d.Zero,
            NpcId = Combat.SandbagNpcId, Level = 10,
        };
        Assert.Equal(1, Combat.HpAfterDamage(t, 2, 500, killAllowed: false));
        Assert.Equal(0, Combat.HpAfterDamage(t, 2, 500, killAllowed: true));
    }

    [Fact]
    public async Task Dev_training_dummy_takes_damage_but_never_dies_or_grants_kill_loot()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(store, Now, level: 45);
        var dummy = session.Targets![Combat.TrainingDummyKey];
        dummy.Hp = 1;
        var skillId = 11020000;
        var castAt = Now + 500;
        await session.HandleAsync(Cast(skillId, dummy.Key, dummy.Position), castAt);
        var landed = await GameSessionTestHelpers.AfterSkillHitsAsync(session, castAt);
        Assert.False(dummy.Dead);
        Assert.True(dummy.Hp >= 1);
        Assert.DoesNotContain(landed, p => GameSessionTestHelpers.Op(p) == 0x8d04);
        Assert.DoesNotContain(landed, p => GameSessionTestHelpers.Op(p) == 0x8d07);
    }

    [Fact]
    public async Task World_spawned_TraDummy_on_field_map_survives_lethal_damage()
    {
        const int mapId = 1111;
        var store = new FakeCharacterStore();
        var data = GameData.Embedded;
        var shared = new SharedWorld(data);
        var session = new GameSession(data,
            new GameSessionOptions { World = shared, CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = false },
            new Random(31)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        session.Teleport(mapId, new Vector3d(-88534.859, -102015.672, 48697.688), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        var dummy = session.Targets!.Values.First(t => t.Name.Contains("TraDummy", StringComparison.OrdinalIgnoreCase));
        Assert.True(Combat.IsTrainingDummy(dummy));
        var before = dummy.Hp;
        dummy.Hp = 1;
        const int skillId = 11020000;
        var castAt = Now + 1000;
        await session.HandleAsync(Cast(skillId, dummy.Key, dummy.Position), castAt);
        await GameSessionTestHelpers.AfterSkillHitsAsync(session, castAt);
        Assert.False(dummy.Dead);
        Assert.True(dummy.Hp >= 1);
        Assert.True(before > 0);
    }
}