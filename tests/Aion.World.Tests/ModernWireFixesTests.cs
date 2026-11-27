using Aion.Application.Gameplay;
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
    public async Task Achievement_batch_claim_puts_561b_before_568a_on_v213_and_after_it_on_v209()
    {
        const int growth = 301_100_300;
        static async Task<(GameSession Session, List<byte[]> Replies)> ClaimAsync(long now)
        {
            var store = new FakeCharacterStore();
            store.Achievements.Add(new AchievementRecord(growth, 300, null, 1, DateTimeOffset.FromUnixTimeMilliseconds(now)));
            var session = await GameSessionTestHelpers.EnterWorldAsync(store, now, level: 55);
            var replies = await session.HandleAsync(
                new PacketWriter().U16(GameSession.TakeAchievementRewardBatchRq).U8(0).I32(0).U8(0).ToArray(), now + 50);
            return (session, replies);
        }

        var previous = Pin(WireVersion.V213);
        try
        {
            var (_, v213) = await ClaimAsync(Now);
            var inventory = v213.FindIndex(p => Op(p) == 0x561B);
            var batch = v213.FindIndex(p => Op(p) == GameSession.TakeAchievementRewardBatchRs);
            Assert.True(inventory >= 0, "0x561B kAchievement must be present");
            Assert.True(inventory < batch, "0x561B must precede 0x568A on 2.0.13");

            // 0x568A: u16 result, packed bools, results and no Common_RewardItem[] tail (ingame-1 #7549).
            var rs = v213[batch];
            var r = new PacketReader(rs) { Offset = 2 };
            Assert.Equal(0, r.U16());
            _ = r.U8();                 // category
            _ = r.I32();                // group
            _ = r.Bool();               // only
            _ = r.Bool();               // more reward available
            for (var n = r.Varint(); n > 0; n--)
            {
                _ = r.I32();
                for (var c = r.Varint(); c > 0; c--) _ = r.U8();
                for (var c = r.Varint(); c > 0; c--) { _ = r.I32(); _ = r.U8(); }
            }
            Assert.True(r.End);
        }
        finally { WireContext.Current = previous; }

        previous = Pin(WireVersion.V209);
        try
        {
            var (_, v209) = await ClaimAsync(Now);
            var inventory = v209.FindIndex(p => Op(p) == 0x561B);
            var batch = v209.FindIndex(p => Op(p) == GameSession.TakeAchievementRewardBatchRs);
            Assert.True(inventory >= 0);
            Assert.True(batch < inventory, "2.0.9 keeps 0x568A before 0x561B");
        }
        finally { WireContext.Current = previous; }
    }

    [Fact]
    public async Task Group_reward_threshold_counts_completed_steps_on_v213()
    {
        const int group = 300_000_102;   // Group_L_Growth_Complete: 13 achievements, 74 steps, steps at 12/24/...
        const int enhanceStone = 930_100_023;
        var seeded = new List<AchievementTables.Entry>();
        var steps = 0;
        foreach (var entry in AchievementTables.Embedded.InGroup(group).Where(e => e.Steps.Length > 0).OrderByDescending(e => e.Steps.Length))
        {
            seeded.Add(entry);
            steps += entry.Steps.Length;
            if (steps >= 12) break;
        }
        Assert.True(steps >= 12, $"group steps seeded: {steps}");
        Assert.True(seeded.Count < 12, $"seeded distinct achievements {seeded.Count} must stay below the step threshold");

        var store = new FakeCharacterStore();
        foreach (var entry in seeded)
            store.Achievements.Add(new AchievementRecord(entry.Id, 10_000_000, null, 0, DateTimeOffset.FromUnixTimeMilliseconds(Now)));
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Now, level: 55);

        var previous = Pin(WireVersion.V213);
        try
        {
            var replies = await session.HandleAsync(
                new PacketWriter().U16(GameSession.TakeAchievementRewardBatchRq).U8(1).I32(0).U8(0).ToArray(), Now + 50);
            var rsIndex = replies.FindIndex(p => Op(p) == GameSession.TakeAchievementRewardBatchRs);
            var inventoryIndex = replies.FindIndex(p => Op(p) == 0x561B);
            Assert.True(rsIndex >= 0);
            Assert.True(inventoryIndex >= 0 && inventoryIndex < rsIndex, "0x561B must precede 0x568A");

            var reader = new PacketReader(replies[rsIndex]) { Offset = 2 };
            Assert.Equal(0, reader.U16());
            _ = reader.U8();       // category
            _ = reader.I32();      // scoped group
            _ = reader.Bool();     // only
            _ = reader.Bool();     // more reward available
            var claimedStepOne = false;
            for (var n = reader.Varint(); n > 0; n--)
            {
                var gid = reader.I32();
                var groupSteps = new List<byte>();
                for (var c = reader.Varint(); c > 0; c--) groupSteps.Add(reader.U8());
                for (var c = reader.Varint(); c > 0; c--) { _ = reader.I32(); _ = reader.U8(); }
                if (gid == group && groupSteps.Contains(1)) claimedStepOne = true;
            }
            Assert.True(reader.End);
            Assert.True(claimedStepOne, "group reward step 1 must be claimed");
            Assert.Contains(store.Items, i => i.TemplateId == enhanceStone && i.Quantity >= 500);
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
