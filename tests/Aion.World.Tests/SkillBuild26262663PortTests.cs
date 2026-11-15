using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Ports from Addiboii/A2Core update-26262663: value4 hit divisor, SkillLv cooldowns, SkillEffectLv flats,
/// hit-count NPCs, and 0x3806 at the act time (cut by the next cast).
/// </summary>
public sealed class SkillBuild26262663PortTests
{
    static readonly long Now = 1_700_000_200_000;

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    static (int Skill, byte Reason) SkillEndOf(byte[] packet)
    {
        var reader = new PacketReader(packet) { Offset = 2 };
        reader.Varint();
        var skill = reader.I32();
        reader.U8();
        return (skill, reader.U8());
    }

    [Fact]
    public void Value4_divides_a_multi_hit_row()
    {
        Assert.Equal(2, GameData.Embedded.Skill(11010000)!.Damage[0].HitDivisor);
        Assert.Equal(1, GameData.Embedded.Skill(11020000)!.Damage[0].HitDivisor);
        Assert.Equal((70 + 10 * 7425 / 10000.0) / 2, Combat.RowDamage(70, 10, 7425, 2));
    }

    [Fact]
    public void Skill_level_replaces_flat_and_shortens_cooldown()
    {
        var data = GameData.Embedded;
        var keen = data.Skill(11020000)!.Damage[0];
        Assert.Equal(51, data.LevelFlat(keen, 1, false)[0]);
        Assert.Equal(70, data.LevelFlat(keen, 2, false)[0]);
        Assert.Equal(989, data.LevelFlat(keen, 40, false)[0]);
        Assert.Equal(66, data.LevelFlat(keen, 2, true)[0]);
        Assert.Equal(897, data.LevelFlat(keen, 99, true)[0]);

        var defiance = data.Skill(11260000)!;
        Assert.Equal(60000, defiance.CooldownAt(1));
        Assert.Equal(59000, defiance.CooldownAt(2));
        Assert.Equal(21000, defiance.CooldownAt(40));
        Assert.Equal(21000, defiance.CooldownAt(99));

        var child = data.Skill(11260010)!;
        Assert.Equal(21000, child.CooldownAt(40));
        Assert.Equal(data.Skill(11010000)!.Damage[0].LevelGroup, data.Skill(11010010)!.Damage[0].LevelGroup);
    }

    [Fact]
    public void Skill_end_rounds_to_the_server_tick()
    {
        Assert.Equal(450, Combat.SkillEndMs(474));
        Assert.Equal(350, Combat.SkillEndMs(474, 13426));
        Assert.Equal(0, Combat.SkillEndMs(0));
    }

    [Fact]
    public void Hit_count_totem_has_seven_hp_and_each_hit_is_one()
    {
        var spawn = GameData.Embedded.NpcFromTemplate(2290017, "DraTotem", [0, 0, 0], 0);
        Assert.NotNull(spawn);
        Assert.True(spawn!.HitCount);
        Assert.Equal(7, Combat.HpFor(spawn));
        var totem = new Target { Key = 1, Name = "totem", Hp = 7, HpMax = 7, HitCount = true };
        var landed = totem.Landed([new Hit(80, true, 1), new Hit(0, false, 2)]);
        Assert.Equal((1, false), (landed[0].Amount, landed[0].Critical));
        Assert.Equal(0, landed[1].Amount);
        Assert.Equal(1, totem.Landed(40));
        Assert.Equal(40, new Target { Key = 2, Name = "other", Hp = 40, HpMax = 40 }.Landed(40));
    }

    [Fact]
    public async Task Keen_strike_ends_at_its_act_time()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now, level: 25, rng: new Random(5));
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var skill = GameData.Embedded.Skill(11020000)!;
        var castAt = Now + 50;
        var opened = await session.HandleAsync(Cast(11020000, dummy.Key, dummy.Position), castAt);
        Assert.DoesNotContain(opened, p => GameSessionTestHelpers.Op(p) == 0x3806);
        var end = Combat.SkillEndMs(skill.EndMs!.Value, session.AttackSpeedForTests);
        var early = await session.TickAsync(castAt + end - 1);
        Assert.DoesNotContain(early, p => GameSessionTestHelpers.Op(p) == 0x3806);
        var due = await session.TickAsync(castAt + end);
        var ended = due.Where(p => GameSessionTestHelpers.Op(p) == 0x3806).Select(SkillEndOf).ToList();
        Assert.Contains((11020000, GamePackets.SkillEndNatural), ended);
    }

    [Fact]
    public async Task Next_cast_cuts_the_running_skill_before_its_start()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now, level: 25, rng: new Random(5));
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var castAt = Now + 50;
        await session.HandleAsync(Cast(11020000, dummy.Key, dummy.Position), castAt);
        // Rending Blow is a different level-1 skill, after the cast gap and before Keen Strike's act end.
        var next = await session.HandleAsync(Cast(11010000, dummy.Key, dummy.Position), castAt + 250);
        var indexOf = (ushort op, Func<byte[], bool> match) => next.FindIndex(p => GameSessionTestHelpers.Op(p) == op && match(p));
        var endAt = indexOf(0x3806, p => SkillEndOf(p) == (11020000, GamePackets.SkillEndByNextCast));
        var startAt = indexOf(0x3802, p => true);
        Assert.True(endAt >= 0 && startAt > endAt);
    }
}
