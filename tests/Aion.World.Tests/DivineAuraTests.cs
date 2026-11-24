using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Live 2026-11-23: the Cleric's Divine Aura 17150000 spawned no aura — Spawn places a marker summon with no attacks, which PcSummons skips.</summary>
public sealed class DivineAuraTests
{
    const int DivineAura = 17150000;
    static readonly long Now = 1_700_000_100_000;

    [Fact]
    public void Skill_has_no_damage_row_and_its_zone_carries_marker_pulse_buffs_and_damage()
    {
        var skill = GameData.Embedded.Skill(DivineAura)!;
        Assert.Equal("Divine Aura", skill.Name);
        Assert.Empty(skill.Damage);
        Assert.Null(PcSummons.For(DivineAura));

        var zone = PcZones.For(DivineAura)!;
        Assert.Equal(2920637, zone.Npc);
        Assert.Equal(9_500, zone.LifetimeMs);
        Assert.Equal(800, zone.TickMs);
        Assert.Equal(400, zone.Radius);
        Assert.NotNull(zone.Damage);
        Assert.Equal(1715000211, zone.Damage!.EffectId);
        Assert.NotNull(zone.AllyAbnormals);
        Assert.Contains(171500031, zone.AllyAbnormals!);
        Assert.Contains(171500041, zone.AllyAbnormals!);
        Assert.Equal(1_000, zone.AbnormalDurationMs);
    }

    [Fact]
    public async Task Cast_on_self_places_the_marker_and_buffs_the_caster_each_tick()
    {
        var (session, mob) = await SkillEffectTests.Setup("Cleric");
        session.LearnForTests(DivineAura);
        var at = session.Position!.Value;
        var placed = session.PlaceZoneForTest(DivineAura, session.PlayerKey, at, Now);
        Assert.Equal(1, session.PlacedZoneCount);
        Assert.Contains(placed, p => GameSessionTestHelpers.Op(p) == 0x3641);

        await session.TickAsync(Now + 700);
        var wires = session.BuffWires();
        Assert.Contains(wires, w => w.AbnormalId == 171500031);
        Assert.Contains(wires, w => w.AbnormalId == 171500041);
        Assert.Equal(mob.HpMax, mob.Hp);

        await session.TickAsync(Now + 12_000);
        Assert.Equal(0, session.PlacedZoneCount);
    }

    [Fact]
    public async Task Cast_on_a_monster_damages_it_each_tick_inside_the_aura()
    {
        var (session, mob) = await SkillEffectTests.Setup("Cleric");
        session.LearnForTests(DivineAura);
        var at = mob.Position;
        session.PlaceZoneForTest(DivineAura, mob.Key, at, Now);

        await session.TickAsync(Now + 700);
        Assert.True(mob.Hp < mob.HpMax, "marker pulse damage hits enemies in the aura");
    }

    static byte[] Cast(int skillId, int targetKey, Vector3d at) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skillId).U8(2).Varint(targetKey).F32(0).Vec3(at).U8(9).ToArray();

    [Fact]
    public async Task Wired_cast_is_accepted_and_places_the_zone_marker()
    {
        var (session, mob) = await SkillEffectTests.Setup("Cleric");
        session.LearnForTests(DivineAura);
        session.SetResource('M', session.Resource!.MpMax);
        var castAt = Now + 3_000;
        var replies = await session.HandleAsync(Cast(DivineAura, session.PlayerKey, mob.Position), castAt);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3801 && GameSession.CastResultCode(p) == 0);
        Assert.Contains(replies, p => GameSessionTestHelpers.Op(p) == 0x3641);
        Assert.Equal(1, session.PlacedZoneCount);
    }
}