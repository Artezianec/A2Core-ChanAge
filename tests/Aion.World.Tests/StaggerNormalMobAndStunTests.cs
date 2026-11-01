using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class StaggerNormalMobAndStunTests
{
    const long Now = 1_760_005_000_000;
    const int RapidScattershot = 16_340_000;
    const int StunAbnormal = 100_000_311;
    const int Dummy = Combat.TrainingDummyKey;

    static byte[] Cast(int skill, int target = Dummy) =>
        new PacketWriter().U16(0x3800).U16(0).I32(skill).U8(2).Varint(target).F32(0).F32(100).F32(200).F32(300).ToArray();

    static bool Accepted(List<byte[]> replies) =>
        replies.Any(p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);

    static async Task<GameSession> EnterElementalistAsync()
    {
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = new GameSession(GameSessionTestHelpers.LiveGameData,
            new GameSessionOptions { World = new SharedWorld(GameData.Embedded), CharacterLevel = 45, WorldId = store.WorldId, TrainingDummy = true },
            new Random(11)) { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String("StaggerNorm").U8(1).U32(6).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States[0].WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    static Target FieldMob() => new()
    {
        Key = 9001, Name = "Field Mob", Hp = 532, HpMax = 532, Position = Vector3d.Zero,
        NpcId = 2290017, Level = 6, SubType = "NormalMonster", Grade = 3,
    };

    [Fact]
    public void Groggy_break_abnormal_ids_exclude_stun_cc()
    {
        Assert.True(StaggerGaugeCombat.IsGroggyBreakStaggerAbnormal(161_400_001));
        Assert.True(StaggerGaugeCombat.IsGroggyBreakStaggerAbnormal(StaggerGaugeCombat.StaggerAbnormalId));
        Assert.False(StaggerGaugeCombat.IsGroggyBreakStaggerAbnormal(StunAbnormal));
        Assert.False(StaggerGaugeCombat.IsGroggyBreakStaggerAbnormal(100_000_301));
    }

    [Fact]
    public void Normal_monster_gauge_capped_at_one_per_hit_and_slower_than_boss()
    {
        var data = GameSessionTestHelpers.LiveGameData;
        var mob = FieldMob();
        StaggerGaugeCombat.InitRegularMobGaugeForTests(mob);
        var boss = new Target
        {
            Key = 9002, Name = "Hero", Hp = 3000, HpMax = 3000, Position = Vector3d.Zero,
            NpcId = 2701034, Level = 5, SubType = "HeroMonster", Grade = 5,
        };
        StaggerGaugeCombat.InitBossGauge(boss);
        const int hit = 80;
        var onMob = StaggerGaugeCombat.GaugeDamage(data, 16_040_000, mob, hit);
        var onBoss = StaggerGaugeCombat.GaugeDamage(data, 16_040_000, boss, hit);
        Assert.Equal(StaggerGaugeCombat.RegularMobMaxGroggyDrainPerHit, onMob);
        Assert.True(onBoss > onMob);
        var replies = StaggerGaugeCombat.AddGaugeDamage(mob, onMob, 1, Now, session: null, firstRow: true);
        Assert.Single(replies);
        Assert.Equal(StaggerGaugeCombat.DefaultGaugeMax - 1, StaggerGaugeCombat.WireGroggyEnergy(mob));
    }

    [Fact]
    public void Template_groggy_max_enables_wire_bar()
    {
        var mob = FieldMob();
        mob = new Target
        {
            Key = mob.Key, Name = mob.Name, Hp = mob.Hp, HpMax = mob.HpMax, Position = mob.Position,
            NpcId = mob.NpcId, Level = mob.Level, SubType = mob.SubType, Grade = mob.Grade, TemplateGroggyMax = 50,
        };
        StaggerGaugeCombat.InitBossGauge(mob);
        Assert.Equal(50, mob.GroggyMax);
        Assert.Equal(50, mob.GroggyEnergy);
    }

    [Fact]
    public async Task Stun_on_target_does_not_unlock_stagger_conditional_skill()
    {
        var session = await EnterElementalistAsync();
        var mob = session.Targets![Dummy];
        var t = Now + 60_000;
        session.ApplyNpcDebuffForTests(mob, StunAbnormal, 5000, t, held: true);
        Assert.True(mob.Held);
        Assert.False(Accepted(await session.HandleAsync(Cast(RapidScattershot, mob.Key), t + 500)));
    }

    [Fact]
    public async Task Groggy_break_debuff_unlocks_stagger_conditional_skill()
    {
        var session = await EnterElementalistAsync();
        var mob = session.Targets![Dummy];
        var t = Now + 60_000;
        session.BreakNpcStaggerForTests(mob, t, 16_140_000);
        Assert.True(Accepted(await session.HandleAsync(Cast(RapidScattershot, mob.Key), t + 200)));
    }
}
