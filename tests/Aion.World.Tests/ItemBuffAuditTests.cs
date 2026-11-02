using System.Reflection;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>External review fixes: item buff overlap, cast scroll cooldown, box select, potion gates.</summary>
public sealed class ItemBuffAuditTests
{
    const long Now = 5_500_000;
    const int StrongFood = 510715007;
    const int WeakFood = 510716001;
    const int StrongSpeedScroll = 531060015;
    const int WeakSpeedScroll = 531060003;
    const int InstantHpPotion = 510116006;
    const int ReturnScroll = 530560001;

    static async Task<GameSession> EnterWithItemAsync(FakeCharacterStore store, int itemId, long wireId, long now, int qty = 1, int level = 55)
    {
        store.Items.Add(FakeCharacterStore.BagItem(itemId, qty, wireId));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, now, level: level);
        await GameSessionTestHelpers.LoadInventoryAsync(session, now);
        return session;
    }

    static async Task<List<byte[]>> UseAsync(GameSession session, long wireId, long now) =>
        await session.HandleAsync(new PacketWriter().U16(RidePackets.ItemUseRq).I64(wireId).ToArray(), now);

    [Fact]
    public async Task Weaker_food_in_same_group_is_refused_without_consuming()
    {
        var store = new FakeCharacterStore();
        var session = await EnterWithItemAsync(store, StrongFood, 701, Now);
        store.Items.Add(FakeCharacterStore.BagItem(WeakFood, 1, 702));
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        Assert.Equal(0, (await UseAsync(session, 701, Now))[0][2]);
        var weak = await UseAsync(session, 702, Now + 100);
        Assert.Equal(1, weak[0][2]);
        Assert.Equal(1, store.Items.Single(i => i.TemplateId == WeakFood).Quantity);
    }

    [Fact]
    public async Task Weaker_speed_scroll_is_refused_while_stronger_group_buff_is_active()
    {
        var store = new FakeCharacterStore();
        var session = await EnterWithItemAsync(store, StrongSpeedScroll, 703, Now);
        store.Items.Add(FakeCharacterStore.BagItem(WeakSpeedScroll, 1, 704));
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        Assert.Equal(0, (await UseAsync(session, 703, Now))[0][2]);
        var weak = await UseAsync(session, 704, Now + 50);
        Assert.Equal(1, weak[0][2]);
        Assert.Equal(1, store.Items.Single(i => i.TemplateId == WeakSpeedScroll).Quantity);
    }

    [Fact]
    public async Task Instant_heal_potion_at_full_hp_is_refused()
    {
        var store = new FakeCharacterStore();
        var session = await EnterWithItemAsync(store, InstantHpPotion, 705, Now);
        Assert.Equal(session.Resource.HpMax, session.Resource.Hp);
        var use = await UseAsync(session, 705, Now);
        Assert.Equal(1, use[0][2]);
        Assert.Equal(1, store.Items.Single(i => i.TemplateId == InstantHpPotion).Quantity);
    }

    static async Task<GameSession> EnterAscendedAsync(FakeCharacterStore store, long now)
    {
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, now, level: 55);
        store.States[0] = store.States[0] with { Ascension = 3 };
        return session;
    }

    [Fact]
    public async Task Return_scroll_cooldown_starts_only_after_successful_cast()
    {
        var store = new FakeCharacterStore();
        var session = await EnterAscendedAsync(store, Now);
        store.Items.Add(FakeCharacterStore.BagItem(ReturnScroll, 2, 706));
        await GameSessionTestHelpers.LoadInventoryAsync(session, Now);
        Assert.Equal(0, (await UseAsync(session, 706, Now))[0][2]);
        var at = session.Position!.Value;
        await session.HandleAsync(new PacketWriter().U16(0x3700).U8(2).Vec3(at with { X = at.X + 500 }).ToArray(), Now + 500);
        Assert.Equal(0, (await UseAsync(session, 706, Now + 600))[0][2]);
    }

    [Fact]
    public void Reward_box_roll_rejects_out_of_range_select_index()
    {
        const string group = "Box_Select_Ceilling_Krao_Easy_A_l_01a_b";
        Assert.Null(RewardBoxes.Embedded.Roll(group, selectIndex: 99, count: 1, new Random(1)));
        Assert.NotNull(RewardBoxes.Embedded.Roll(group, selectIndex: 0, count: 1, new Random(1)));
    }

    [Fact]
    public async Task Expired_buff_stats_drop_from_sheet_before_BuffTick()
    {
        var store = new FakeCharacterStore();
        var session = await EnterWithItemAsync(store, StrongFood, 707, Now);
        Assert.Equal(0, (await UseAsync(session, 707, Now))[0][2]);
        var hpWithFood = session.Resource!.HpMax;
        Assert.Contains(21619031, session.ActiveBuffs);
        ForceFirstBuffExpireAt(session, Now - 1);
        typeof(GameSession).GetMethod("RefreshStats", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, null);
        Assert.Equal(hpWithFood - StatBonusFromFood(), session.Resource.HpMax);
        await session.TickAsync(Now + 1);
        Assert.DoesNotContain(21619031, session.ActiveBuffs);
    }

    static int StatBonusFromFood() =>
        RewardBoxes.Embedded.Abnormal(21619031)!.Effects.Where(e => e.Type == "StatChange" && e.Values[0] == "HPMax")
            .Sum(e => int.Parse(e.Values[1]));

    [Fact]
    public async Task Reusing_food_rebuilds_stat_bonus()
    {
        var store = new FakeCharacterStore();
        var session = await EnterWithItemAsync(store, StrongFood, 708, Now, qty: 2);
        Assert.Equal(0, (await UseAsync(session, 708, Now))[0][2]);
        var hpAfterFirst = session.Resource!.HpMax;
        Assert.Equal(0, (await UseAsync(session, 708, Now + 60_000))[0][2]);
        Assert.Equal(hpAfterFirst, session.Resource.HpMax);
        Assert.Contains(21619031, session.ActiveBuffs);
    }

    [Fact]
    public async Task Permanent_buff_refresh_survives_immediate_BuffTick()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now);
        var addBuff = typeof(GameSession).GetMethod("AddBuff", BindingFlags.Instance | BindingFlags.NonPublic)!;
        const int abnormalId = 21619031;
        var replies = new List<byte[]>();
        addBuff.Invoke(session, [abnormalId, -1, 0, Now, replies, false, SelfAbnormalPath.Item]);
        addBuff.Invoke(session, [abnormalId, -1, 0, Now + 100, replies, false, SelfAbnormalPath.Item]);
        await session.TickAsync(Now + 101);
        Assert.Contains(abnormalId, session.ActiveBuffs);
    }

    static void ForceFirstBuffExpireAt(GameSession session, long expireAt)
    {
        var buffsField = typeof(GameSession).GetField("buffs", BindingFlags.Instance | BindingFlags.NonPublic);
        var list = (System.Collections.IList)buffsField!.GetValue(session)!;
        Assert.NotEmpty(list);
        var first = list[0]!;
        first.GetType().GetField("ExpireAt", BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(first, expireAt);
    }
}
