using Aion.Protocol.Native;
using Xunit;

namespace Aion.World.Tests;

public sealed class RiftCubeTests
{
    /// <summary>02:00 KST on 2026-11-15 (17:00 UTC the day before).</summary>
    static readonly long Opening = new DateTimeOffset(2026, 11, 15, 2, 0, 0, TimeSpan.FromHours(9)).ToUnixTimeMilliseconds();

    [Fact]
    public void Field_maps_get_the_pools_and_all_rift_spawners_without_doubling_the_placed_rifts()
    {
        foreach (var map in new[] { 1010, 1011, 1110, 1111 })
        {
            var objects = WorldObjects.ObjectsOn(map);
            var rifts = WorldPools.Rifts.Values.Where(r => r.Map == map).ToList();
            Assert.NotEmpty(rifts);
            foreach (var rift in rifts) Assert.Equal(5, WorldPools.RiftPoints(map, rift.Spawner).Count);
            Assert.DoesNotContain(objects, o => o.SpacetimeRift && o.Rift is null);
            Assert.NotEmpty(WorldPools.PoolsOnMap(map));
        }
        Assert.Equal(206, new[] { 1010, 1011, 1110, 1111 }.Sum(m => WorldPools.PoolsOnMap(m).Count));
    }

    [Fact]
    public void Rifts_open_every_three_hours_from_two_KST_for_ten_minutes()
    {
        var rift = WorldPools.Rifts.Values.First();
        Assert.Equal(Opening, WorldPools.OpenedAt(rift, Opening + 5 * 60_000));
        Assert.Null(WorldPools.OpenedAt(rift, Opening + 11 * 60_000));
        Assert.Equal(Opening + 3 * 3_600_000L, WorldPools.NextOpening(rift, Opening + 11 * 60_000));
        Assert.Equal(Opening - 3 * 3_600_000L, WorldPools.LastOpening(rift, Opening - 1));
        var map = rift.Map;
        var points = WorldPools.RiftPoints(map, rift.Spawner);
        var objects = WorldObjects.ObjectsOn(map);
        Assert.Single(points, i => WorldPools.RiftShown(map, i, objects[i], Opening + 60_000, out _));
        Assert.DoesNotContain(points, i => WorldPools.RiftShown(map, i, objects[i], Opening + 20 * 60_000, out _));
    }

    [Fact]
    public void A_pool_keeps_its_slots_up_and_an_opened_cube_comes_back_elsewhere_later()
    {
        var state = new PoolState(1010);
        var rng = new Random(5);
        var pool = WorldPools.PoolsOnMap(1010).Values.First(p => p.Positions.Count > p.Slots.Count);
        var standing = state.Positions(pool.Key, rng, 0);
        Assert.All(standing, p => Assert.True(p >= 0));
        var index = pool.Positions[standing[0]][pool.Slots[0]];
        Assert.NotNull(state.Standing(index, rng, 0));
        Assert.True(state.Take(index, rng, 1000, 15 * 60_000));
        Assert.False(state.Take(index, rng, 1001, 15 * 60_000));
        Assert.Equal(-1, state.Positions(pool.Key, rng, 2000)[0]);
        Assert.True(state.Positions(pool.Key, rng, 1000 + 15 * 60_000)[0] >= 0);
    }

    [Fact]
    public void Each_rift_opening_counts_its_uses_against_the_limit()
    {
        var state = new PoolState(1110);
        Assert.True(state.UseRift("x", Opening, 2));
        Assert.True(state.UseRift("x", Opening, 2));
        Assert.False(state.UseRift("x", Opening, 2));
        Assert.True(state.UseRift("x", Opening + 3 * 3_600_000L, 2));
    }

    /// <summary>The client's map-loaded report after a move (0x3622).</summary>
    static async Task Arrive(GameSession session, List<byte[]> _, long now) =>
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 100);

    [Fact]
    public async Task Using_an_open_rift_moves_the_player_to_the_other_factions_map_and_starts_the_visit()
    {
        var now = Opening + 60_000;
        var session = await GameSessionTestHelpers.EnterWorldAsync(new FakeCharacterStore { Progress = (50, 0) }, now, level: 50);
        var home = session.ProfileRace == 2 ? 1110 : 1010;
        var rift = WorldPools.Rifts.Values.First(r => r.Map == home);
        var objects = WorldObjects.ObjectsOn(home);
        await Arrive(session, session.Teleport(home, objects[WorldPools.RiftPoints(home, rift.Spawner)[0]].At, now), now);
        var index = WorldPools.RiftPoints(home, rift.Spawner).Single(i => WorldPools.RiftShown(home, i, objects[i], now, out _));
        await Arrive(session, await session.UseObjectForChecks(index, now), now);
        Assert.Equal(rift.ArrivalMap, session.MapId);
        Assert.Equal(home, session.RiftVisit?.Map);
    }

    [Fact]
    public async Task A_closed_rift_refuses_the_use()
    {
        var now = Opening + 30 * 60_000;
        var session = await GameSessionTestHelpers.EnterWorldAsync(new FakeCharacterStore { Progress = (50, 0) }, now, level: 50);
        var home = session.ProfileRace == 2 ? 1110 : 1010;
        var rift = WorldPools.Rifts.Values.First(r => r.Map == home);
        var index = WorldPools.RiftPoints(home, rift.Spawner)[0];
        await Arrive(session, session.Teleport(home, WorldObjects.ObjectsOn(home)[index].At, now), now);
        var replies = await session.UseObjectForChecks(index, now);
        Assert.Equal(home, session.MapId);
        Assert.Contains(replies, p => BitConverter.ToUInt16(p, 0) == 0x3637 && BitConverter.ToUInt16(p, 2) == 1);
    }
}
