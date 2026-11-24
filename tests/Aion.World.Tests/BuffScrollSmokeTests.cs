using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Smoke tests for buff scroll item use → abnormal → stat application (main parity check).</summary>
public sealed class BuffScrollSmokeTests
{
    const long Now = 6_000_000;
    const int AttackSpeedScroll = 531060004; // Skill 2210105 → CombatSpeed +1000
    const int MoveSpeedScroll = 531060015;   // Skill 2210104 → MoveSpeed +1500
    const int MaxHpMpScroll = 531060027;    // Skill 2210402 → MaxHPRatio +1000, MPMax +100

    static async Task<GameSession> EnterWithItemAsync(FakeCharacterStore store, int itemId, long wireId, long now)
    {
        store.Items.Add(FakeCharacterStore.BagItem(itemId, 1, wireId));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, now, level: 55);
        await GameSessionTestHelpers.LoadInventoryAsync(session, now);
        return session;
    }

    [Fact]
    public async Task Attack_speed_scroll_applies_combat_speed_buff()
    {
        var store = new FakeCharacterStore();
        var session = await EnterWithItemAsync(store, AttackSpeedScroll, 801, Now);
        var before = session.AttackSpeedForTests;
        var use = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(801).ToArray(), Now);
        Assert.Equal(0, use[0][2]);
        Assert.Contains(22101051, session.ActiveBuffs);
        Assert.Equal(before + 1000, session.AttackSpeedForTests);
        Assert.DoesNotContain(store.Items, i => i.TemplateId == AttackSpeedScroll && i.Quantity > 0);
    }

    [Fact]
    public async Task Move_speed_scroll_applies_move_speed_buff_and_wire()
    {
        var store = new FakeCharacterStore();
        var session = await EnterWithItemAsync(store, MoveSpeedScroll, 802, Now);
        var use = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(802).ToArray(), Now);
        Assert.Equal(0, use[0][2]);
        Assert.Contains(22101041, session.ActiveBuffs);
        Assert.Equal(1500, session.WireMoveSpeedPermille());
        Assert.Contains(use, p => p.SequenceEqual(PlayerPackets.MoveSpeedUpdate(session.PlayerKey, [(GameSession.MoveSpeedStat, 1500)])));
    }

    [Fact]
    public async Task Max_hp_ratio_scroll_raises_hp_max_pool()
    {
        var store = new FakeCharacterStore();
        var session = await EnterWithItemAsync(store, MaxHpMpScroll, 803, Now);
        var hpBefore = session.Resource!.HpMax;
        var mpBefore = session.Resource.MpMax;
        var use = await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(803).ToArray(), Now);
        Assert.Equal(0, use[0][2]);
        Assert.Contains(22104021, session.ActiveBuffs);
        Assert.True(session.Resource.HpMax > hpBefore);
        Assert.InRange(session.Resource.HpMax - hpBefore, hpBefore / 12, hpBefore / 8); // MaxHPRatio +1000 ≈ +10%
        Assert.Equal(mpBefore + 100, session.Resource.MpMax);
    }
}