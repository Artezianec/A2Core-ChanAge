using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Abnormal rows ported from Addiboii/A2Core build 26262663 that this server's exports did not apply:
/// monster skills on the player, their DoT and slows, KnockdownInstant, and player Dot_NormalCalc on NPCs.
/// </summary>
public sealed class PortedAbnormalTests
{
    static readonly long Now = 1_700_000_100_000;

    static bool Has(byte[] packet, int value) => packet.AsSpan().IndexOf(BitConverter.GetBytes(value)) >= 0;

    static bool HasBytes(byte[] packet, byte[] needle) => packet.AsSpan().IndexOf(needle) >= 0;

    [Fact]
    public async Task Monster_knockdown_sends_382a_then_3816_on_the_first_hit_only()
    {
        var session = await GameSessionTestHelpers.EnterWorldAsync(new FakeCharacterStore(), Now);
        var at = new Vector3d(100, 200, 300);
        var hit = session.NpcHitsPlayer(50, Now + 10_000, killerKey: 2001, killerSkill: 1604680, hitSeq: 1, casterAt: at);
        var add = Assert.Single(hit, p => GameSessionTestHelpers.Op(p) == 0x382a);
        Assert.Contains((byte)0x13, add);
        Assert.True(Has(add, 12000311));
        Assert.True(Has(add, 1604680));
        var knock = hit.First(p => GameSessionTestHelpers.Op(p) == 0x3816);
        Assert.True(hit.ToList().IndexOf(knock) > hit.ToList().IndexOf(add));
        Assert.True(Has(knock, 120003111));
        Assert.True(knock.AsSpan(knock.Length - 3).SequenceEqual(new byte[] { 0x04, 0x01, 0x01 }));
        Assert.Contains(12000311, session.ActiveBuffs);

        var again = session.NpcHitsPlayer(50, Now + 10_100, killerKey: 2001, killerSkill: 1604680, hitSeq: 2, casterAt: at);
        Assert.DoesNotContain(again, p => GameSessionTestHelpers.Op(p) == 0x382a);
        Assert.DoesNotContain(again, p => GameSessionTestHelpers.Op(p) == 0x3816);

        var expired = await session.TickAsync(Now + 10_000 + 3_000);
        Assert.Contains(expired, p => GameSessionTestHelpers.Op(p) == 0x382c);
        Assert.DoesNotContain(12000311, session.ActiveBuffs);
        Assert.Equal(100, AbnormalStackRules.For(12000311).Group);
    }

    [Fact]
    public async Task Monster_dot_ticks_a_share_of_the_hit_on_the_damage_packet()
    {
        var session = await GameSessionTestHelpers.EnterWorldAsync(new FakeCharacterStore(), Now);
        var before = session.Resource.Hp;
        var hit = session.NpcHitsPlayer(20, Now + 10_000, killerKey: 2001, killerSkill: 1223050);
        Assert.DoesNotContain(hit, p => GameSessionTestHelpers.Op(p) == 0x3805);
        Assert.Equal(before - 20, session.Resource.Hp);

        var ticks = await session.TickAsync(Now + 12_000);
        var dot = Assert.Single(ticks, p => GameSessionTestHelpers.Op(p) == 0x3805);
        Assert.Contains((byte)0x0a, dot);
        Assert.True(Has(dot, 120001111));
        Assert.True(Has(dot, 1223050));
        Assert.Equal(before - 20 - 8, session.Resource.Hp);
    }

    [Fact]
    public async Task Monster_slow_is_a_signed_364a_and_clears_to_zero()
    {
        var session = await GameSessionTestHelpers.EnterWorldAsync(new FakeCharacterStore(), Now);
        var hit = session.NpcHitsPlayer(10, Now + 10_000, killerKey: 2001, killerSkill: 1217770);
        Assert.Contains(hit, p => GameSessionTestHelpers.Op(p) == 0x364a && HasBytes(p, [0xCD, 0x00, 0x48, 0xF4, 0xFF, 0xFF]));
        Assert.DoesNotContain(hit, p => GameSessionTestHelpers.Op(p) == 0x364b);
        Assert.Contains(12172701, session.ActiveBuffs);
        Assert.True(session.Resource.Hp > 0);

        var expired = await session.TickAsync(Now + 20_000);
        Assert.Contains(expired, p => GameSessionTestHelpers.Op(p) == 0x364a && HasBytes(p, [0xCD, 0x00, 0x00, 0x00, 0x00, 0x00]));
        Assert.DoesNotContain(12172701, session.ActiveBuffs);
        Assert.False(session.Dead);
    }

    [Fact]
    public async Task Dash_sends_its_effect_then_the_self_buff()
    {
        var session = await GameSessionTestHelpers.EnterWorldAsync(new FakeCharacterStore(), Now, level: 55);
        var replies = session.ClassSelfBuffsForTests(1101, 3, Now + 6_000);
        var effect = replies.ToList().FindIndex(p => GameSessionTestHelpers.Op(p) == 0x3804 && Has(p, 110111));
        var add = replies.ToList().FindIndex(p => GameSessionTestHelpers.Op(p) == 0x382a && Has(p, 11011) && Has(p, 1101));
        Assert.True(effect >= 0 && add > effect);
        Assert.Contains(11011, session.ActiveBuffs);
        await session.TickAsync(Now + 8_000);
        Assert.DoesNotContain(11011, session.ActiveBuffs);
    }

    [Fact]
    public async Task Drill_dart_dot_ticks_from_the_attack_on_the_damage_packet()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now);
        var dummy = session.Targets![Combat.TrainingDummyKey];
        var start = dummy.Hp;
        var added = session.ApplyNpcDebuffForTests(dummy, 140500001, 6_000, Now + 10_000, skillId: 14050000);
        Assert.DoesNotContain(added, p => GameSessionTestHelpers.Op(p) == 0x3805);
        var ticks = await session.TickAsync(Now + 11_000);
        var dot = Assert.Single(ticks, p => GameSessionTestHelpers.Op(p) == 0x3805);
        Assert.Contains((byte)0x0a, dot);
        Assert.True(Has(dot, 1405000012));
        Assert.True(Has(dot, 14050000));
        Assert.True(dummy.Hp < start);
        Assert.Contains(ticks, p => GameSessionTestHelpers.Op(p) == 0x8d00);
    }

    [Fact]
    public async Task Client_jointstrike_row_does_not_use_the_stagger_override_hold()
    {
        var session = await GameSessionTestHelpers.EnterSharedWorldAsync(new FakeCharacterStore(), Now);
        var dummy = session.Targets![Combat.TrainingDummyKey];
        session.ApplyNpcDebuffForTests(dummy, 161400001, 5_000, Now + 10_000, skillId: 16140000, clientEffects: true);
        Assert.False(dummy.Held);
        Assert.False(PortedSkillAbnormals.Combat(161400001).Held);
        var ticks = await session.TickAsync(Now + 11_000);
        var dot = Assert.Single(ticks, p => GameSessionTestHelpers.Op(p) == 0x3805);
        Assert.Contains((byte)0x0a, dot);
        Assert.True(Has(dot, 1614000011));
    }
}
