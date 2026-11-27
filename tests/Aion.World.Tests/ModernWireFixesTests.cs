using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>2.0.13 wire fixes: Subscribe types, Battle Pass groups, achievement replies, gear score/CP entry.</summary>
public sealed class ModernWireFixesTests
{
    const long Now = 1_792_000_000_000L;   // 2026-10-14, the global passes 10044/10045 are active

    static ushort Op(byte[] packet) => GameSessionTestHelpers.Op(packet);

    static WireVersion Pin(WireVersion version)
    {
        var previous = WireContext.Current;
        WireContext.Current = version;
        return previous;
    }

    static List<(byte Type, int DataId, long Start, long End)> Subscribes(byte[] packet)
    {
        var r = new PacketReader(packet) { Offset = 2 };
        var list = new List<(byte, int, long, long)>();
        for (var n = r.Varint(); n > 0; n--) list.Add((r.U8(), r.I32(), r.I64(), r.I64()));
        Assert.Equal(packet.Length, r.Offset);
        return list;
    }

    [Fact]
    public async Task Subscribe_list_uses_the_2_0_13_types_on_v213()
    {
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 45, WorldId = 1204 }, new Random(5))
        {
            Store = new FakeCharacterStore(Guid.NewGuid()),
        };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);

        var previous = Pin(WireVersion.V213);
        try
        {
            var update = session.SetMembership(true, Now);
            var entries = Subscribes(update.Single(p => Op(p) == GameSession.SubscribeUpdateNt));
            Assert.Equal([34, 35, 37, 38, 39, 40, 41, 42, 44], entries.Select(e => (int)e.Type).ToList());
            Assert.All(entries.Take(8), e => Assert.Equal(4, e.DataId));
            Assert.Equal(8, entries[^1].DataId);
            Assert.All(entries, e => Assert.Equal(GameSession.MembershipDurationMs, e.End - e.Start));
        }
        finally { WireContext.Current = previous; }

        previous = Pin(WireVersion.V209);
        try
        {
            var legacy = session.SetMembership(true, Now);
            var entries = Subscribes(legacy.Single(p => Op(p) == GameSession.SubscribeUpdateNt));
            Assert.Equal(GameSession.QuaiMembershipTypes.Select(t => (int)t).ToList(), entries.Select(e => (int)e.Type).ToList());
        }
        finally { WireContext.Current = previous; }
    }

    [Fact]
    public async Task Battle_pass_entry_lists_active_2_0_13_groups_and_the_subscribed_count()
    {
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 45, WorldId = 1204 }, new Random(5))
        {
            Store = new FakeCharacterStore(Guid.NewGuid()),
        };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);

        var previous = Pin(WireVersion.V213);
        try
        {
            var replies = session.GrantBattlePassExp(0, Now);
            var packet = replies.Last(p => Op(p) == GameSession.BattlePassInfoListNt);
            var r = new PacketReader(packet) { Offset = 2 };
            var groups = new List<(int Id, int Total)>();
            uint subscribed = 0;
            for (var n = r.Varint(); n > 0; n--)
            {
                var id = r.I32();
                var total = r.I32();
                r.I32(); r.I64(); r.I64();
                for (var c = r.Varint(); c > 0; c--) r.I32();
                for (var c = r.Varint(); c > 0; c--) { r.I32(); r.U8(); }
                subscribed = r.Varint();
                r.Bool(); r.I32(); r.Bool();
                groups.Add((id, total));
            }
            Assert.Equal(packet.Length, r.Offset);
            Assert.Contains(groups, g => g.Id is 10044 or 10045 or 10047);
            Assert.All(groups, g => Assert.True(g.Total >= 0));
            Assert.Equal(1u, subscribed);
        }
        finally { WireContext.Current = previous; }
    }

    [Fact]
    public void Achievement_take_replies_use_the_2_0_13_layout()
    {
        var previous = Pin(WireVersion.V213);
        try
        {
            var one = GameSession.TakeAchievementRewardReply(0, 403_100_300, 2, [(930100001, 50)]);
            Assert.Equal(9, one.Length);   // u16 result, i32 id, u8 step, no reward items
            var r = new PacketReader(one) { Offset = 2 };
            Assert.Equal(0, r.U16());
            Assert.Equal(403_100_300, r.I32());
            Assert.Equal(2, r.U8());
            Assert.True(r.End);

            var group = GameSession.TakeAchievementGroupRewardReply(1, 7, 3, [(930100001, 50)]);
            Assert.Equal(9, group.Length);
            r = new PacketReader(group) { Offset = 2 };
            Assert.Equal(1, r.U16());
            Assert.Equal(7, r.I32());
            Assert.Equal(3, r.U8());
            Assert.True(r.End);

            var batch = GameSession.RewardBatchReply(1, 10, only: true,
                [(10, new byte[] { 1 }, new Dictionary<int, int> { [20] = 1 })], moreRewardAvailable: false, grantedItems: [(930100001, 50)]);
            r = new PacketReader(batch) { Offset = 2 };
            Assert.Equal(0, r.U16());
            Assert.Equal(1, r.U8());
            Assert.Equal(10, r.I32());
            Assert.True(r.Bool());
            Assert.False(r.Bool());
            Assert.Equal(1u, r.Varint());
            Assert.Equal(10, r.I32());
            Assert.Equal(1u, r.Varint());
            Assert.Equal(1, r.U8());
            Assert.Equal(1u, r.Varint());
            Assert.Equal(20, r.I32());
            Assert.Equal(1, r.U8());
            Assert.True(r.End);   // no trailing Common_RewardItem[]
        }
        finally { WireContext.Current = previous; }
    }

    [Fact]
    public async Task World_entry_sends_nonzero_gear_score_and_combat_power_on_v213()
    {
        var store = new FakeCharacterStore(Guid.NewGuid());
        var profile = GameData.Embedded.Profile(1, 1)!;
        var weapon = GameData.Embedded.StarterWeapon(profile)!.Value;
        store.Items.Add(FakeCharacterStore.EquippedItem(weapon, 900, 1));
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { CharacterLevel = 45, WorldId = store.WorldId }, new Random(5))
        {
            Store = store,
        };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(GameSessionTestHelpers.LightCreate(), Now);
        var wireId = store.States[0].WireId;

        var previous = Pin(WireVersion.V213);
        try
        {
            var select = new PacketWriter().U16(0xC4F6).I32(0).I32(0).I32(0).Varint((uint)wireId << 16).I32(0).ToArray();
            var replies = await session.HandleAsync(select, Now + 1);

            var equipLevels = replies.Where(p => Op(p) == 0x561D).ToList();
            Assert.NotEmpty(equipLevels);
            Assert.All(equipLevels, p => Assert.True(BitConverter.ToInt32(p, 2) > 0, $"0x561D gear score must be non-zero, got {BitConverter.ToInt32(p, 2)}"));
            var combatPower = Assert.Single(replies, p => Op(p) == 0x3656);
            Assert.True(BitConverter.ToInt64(combatPower, 2) > 0, "0x3656 combat power must be non-zero");
        }
        finally { WireContext.Current = previous; }
    }
}
