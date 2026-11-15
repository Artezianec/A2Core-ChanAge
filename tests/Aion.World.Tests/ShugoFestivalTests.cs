using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>The Shugo festival schedule and games (ported from zaraza87/A2Core dell-merge-2026-11): data, wire forms, schedule, entrance.</summary>
public sealed class ShugoFestivalTests
{
    static ushort Op(byte[] b) => (ushort)(b[0] | b[1] << 8);

    [Fact]
    public void Festival_data_holds_the_11_games_for_both_races()
    {
        var games = FestivalGames.Embedded;
        Assert.Equal(22, games.All.Count);
        Assert.Equal(11, games.All.Count(g => g.Event / 100_000 == 1));
        Assert.Equal(11, games.All.Select(g => g.Game).Distinct().Count());
        var trap = games.OfMap(500036)!;
        Assert.Equal("TrapBoard", trap.Contents);
        Assert.Equal(8, trap.TrapBoard!.Rounds.Length);
        Assert.Equal(175_000, trap.TrapBoard.Rounds.Sum(r => r.LengthMs));
        Assert.True(games.OfMap(500039)!.Racing is { Laps: 2, StartCount: 5, EndCount: 10 });
        Assert.Equal(6, games.OfMap(500034)!.Dilemma!.Rounds.Length);
    }

    [Fact]
    public void Every_object_spawner_of_the_event_maps_stands_in_our_map_data()
    {
        var bad = new List<string>();
        foreach (var g in FestivalGames.Embedded.All)
        {
            var map = Systems2Tables.Embedded.FieldEventOf(g.Event)!.Map;
            foreach (var (name, s) in g.Spawners.Where(x => x.Value.Envs.Length > 0))
                if (WorldObjects.ObjectsOn(map).Count(o => o.Name == name) != s.Count * s.Envs.Length) bad.Add($"{map} {name}");
        }
        Assert.True(bad.Count == 0, string.Join("; ", bad.Take(8)));
    }

    [Fact]
    public void Wire_forms_of_the_games()
    {
        Assert.Equal("1260B917010807060504030201", Convert.ToHexString(FestivalGames.TrapBoardState(3001, FestivalGames.TrapReady, 0x0102030405060708)));
        Assert.Equal("0B60E907050000000000000002", Convert.ToHexString(FestivalGames.RacingCharLap(1001, 5, 2)));
        Assert.Equal("1060BD46020100000000000000", Convert.ToHexString(FestivalGames.PlatformerState(9021, FestivalGames.PlatformerStart, 1)));
        var rng = new Random(1);
        for (var i = 0; i < 50; i++)
        {
            var path = FestivalGames.TrapPath(6, 6, rng);
            Assert.Equal(6, path.Select(c => c / 6).Distinct().Count());
        }
    }

    [Fact]
    public void Each_festival_event_prepares_at_the_full_hour_then_runs()
    {
        var e = GameSession.FestaEventsOf(1).First();
        var hour = new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var prepare = GameSession.FieldEventState(e, hour + 60_000);
        Assert.Equal(GameSession.FieldEventPrepare, prepare.State);
        Assert.Equal(hour, prepare.Open);
        Assert.Equal(hour + e.ReadySeconds * 1000L, prepare.Start);
        Assert.Equal(GameSession.FieldEventProgress, GameSession.FieldEventState(e, prepare.Start + 1000).State);
        Assert.Equal(GameSession.FieldEventEnd, GameSession.FieldEventState(e, prepare.End + 1000).State);
        Assert.Equal(9, GameSession.FestaEventsOf(1).Count);
        Assert.Equal(9, GameSession.FestaEventsOf(2).Count);
        // FieldEventInfo_NT lists the running ones, "00" when none.
        var info = GameSession.FieldEventInfo(1, hour + 60_000);
        Assert.Equal(GameSession.FieldEventInfoNt, Op(info));
        Assert.Equal(9, info[2]);
        Assert.Equal(new byte[] { 0x00, 0x60, 0x00 }, GameSession.FieldEventInfo(1, hour + 59 * 60_000));
    }

    [Fact]
    public async Task The_entrance_is_refused_outside_the_entry_window_and_for_the_other_race()
    {
        var hour = new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, hour + 50 * 60_000, level: 45);
        var light = GameSession.FestaEventsOf(1).First();
        var closed = await session.HandleAsync(new PacketWriter().U16(GameSession.FieldEventEntranceRq).Varint(light.Id).ToArray(), hour + 50 * 60_000);
        var rs = Assert.Single(closed, p => Op(p) == GameSession.FieldEventEntranceRs);
        Assert.Equal(4962, BitConverter.ToUInt16(rs, 2));
        var dark = GameSession.FestaEventsOf(2).First();
        var other = await session.HandleAsync(new PacketWriter().U16(GameSession.FieldEventEntranceRq).Varint(dark.Id).ToArray(), hour + 50 * 60_000);
        Assert.Equal(4963, BitConverter.ToUInt16(Assert.Single(other, p => Op(p) == GameSession.FieldEventEntranceRs), 2));
    }

    [Fact]
    public async Task Entering_in_the_entry_window_moves_into_the_event_map_with_its_game()
    {
        var hour = new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var now = hour + 60_000;
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, now, level: 45);
        var trap = GameSession.FestaEventsOf(1).First(e => FestivalGames.Embedded.OfMap(e.Map)?.Contents == "TrapBoard");
        var entered = await session.HandleAsync(new PacketWriter().U16(GameSession.FieldEventEntranceRq).Varint(trap.Id).ToArray(), now);
        Assert.Equal(0, BitConverter.ToUInt16(Assert.Single(entered, p => Op(p) == GameSession.FieldEventEntranceRs), 2));
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 100);
        Assert.Equal(trap.Map, session.MapId);
        Assert.NotNull(session.Copy?.Festival);
    }
}
