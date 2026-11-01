using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class AchievementVehicleCollectParityTests
{
    const long Now = 1_762_000_000_000;
    const int AsmodianVehicleCollect = 403_100_300;
    const int ElyosVehicleCollect = 303_100_300;
    const int VehicleRewardBox = 533_700_117;

    static int Op(byte[] packet) => GameSessionTestHelpers.Op(packet);

    [Theory]
    [InlineData(ElyosVehicleCollect, 1, true)]
    [InlineData(AsmodianVehicleCollect, 2, true)]
    [InlineData(ElyosVehicleCollect, 2, false)]
    [InlineData(AsmodianVehicleCollect, 1, false)]
    public void Achievement_id_prefix_matches_race(int achievementId, int race, bool expected) =>
        Assert.Equal(expected, GameSession.AchievementIdMatchesRace(achievementId, race));

    [Fact]
    public async Task World_entry_syncs_vehicle_collect_for_asmodian_with_ten_mounts()
    {
        var store = new FakeCharacterStore();
        for (var i = 0; i < 10; i++)
            store.RideRows.Add(new RideRecord(1100 + i, 1, 0, true));
        var session = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(store, Now, level: 55);
        Assert.True(session.AchievementCount(AsmodianVehicleCollect) >= 10);
    }

    [Fact]
    public async Task Vehicle_collect_step_two_grants_reward_box_for_asmodian()
    {
        var store = new FakeCharacterStore();
        for (var i = 0; i < 10; i++)
            store.RideRows.Add(new RideRecord(1100 + i, 1, 0, true));
        store.Achievements.Add(new AchievementRecord(
            AsmodianVehicleCollect, 10, null, 1, DateTimeOffset.FromUnixTimeMilliseconds(Now)));
        var session = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(store, Now, level: 55);

        var replies = await session.HandleAsync(
            new PacketWriter().U16(GameSession.TakeAchievementRewardRq).I32(AsmodianVehicleCollect).U8(2).ToArray(),
            Now + 50);

        Assert.Contains(replies, p => Op(p) == 0x561b);
        Assert.Contains(store.Items, i => i.TemplateId == VehicleRewardBox && i.Quantity >= 1);
    }

    [Fact]
    public void Open_reward_box_success_reply_leading_u16_is_035d()
    {
        var packet = RidePackets.OpenBoxReply(0, 16, VehicleRewardBox, 0xff, 1, [(534_760_107, 1)]);
        Assert.Equal(RidePackets.OpenRewardBoxSuccessWire, BitConverter.ToUInt16(packet, 2));
    }

    [Fact]
    public async Task Open_vehicle_reward_box_reply_uses_success_wire()
    {
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterSharedDarkWorldAsync(store, Now, level: 55, rng: new Random(20));
        store.Items.Add(FakeCharacterStore.BagItem(VehicleRewardBox, 1, 20));
        await session.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now + 10);

        var opened = await session.HandleAsync(
            new PacketWriter().U16(RidePackets.OpenBoxRq).I64(20).U8(0).I32(1).ToArray(),
            Now + 20);

        Assert.Equal(RidePackets.OpenBoxRs, Op(opened[0]));
        Assert.Equal(RidePackets.OpenRewardBoxSuccessWire, BitConverter.ToUInt16(opened[0], 2));
        Assert.DoesNotContain(opened, p => p.Length >= 3 && Op(p) == RidePackets.OpenBoxRs && BitConverter.ToUInt16(p, 2) == 0);
    }
}
