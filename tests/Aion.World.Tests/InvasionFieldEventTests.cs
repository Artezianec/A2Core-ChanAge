using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Dimensional Invasion (Beritra Air Raid and its variants) hosted by the field event machinery: the list, the slot rotation, the entrance.</summary>
public sealed class InvasionFieldEventTests : IDisposable
{
    static ushort Op(byte[] b) => (ushort)(b[0] | b[1] << 8);

    // 12:30 KST (03:30 UTC): hour 12, 12 % 3 == 0 = the first variant, Beritra Air Raid (OBSERVED 220101 at 12:30 for an Asmodian).
    static readonly long Slot12 = new DateTimeOffset(2026, 11, 24, 3, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    public InvasionFieldEventTests() => GameSession.FieldEventSettings = FieldEventOptions.Default;
    public void Dispose() => GameSession.FieldEventSettings = FieldEventOptions.Default;

    [Fact]
    public void The_client_tables_hold_a_Direct_invasion_per_variant_and_race_that_runs_hourly_at_half_past()
    {
        var light = GameSession.InvasionEventsOf(1);
        var dark = GameSession.InvasionEventsOf(2);
        Assert.Equal([120101, 120201, 120301], light.Select(e => e.Id));
        Assert.Equal([220101, 220201, 220301], dark.Select(e => e.Id));
        Assert.Equal([500032, 500031, 500033], light.Select(e => e.Map));
        Assert.Equal([510027, 510026, 510028], dark.Select(e => e.Map));
        Assert.All(light.Concat(dark), e => { Assert.Equal(180, e.ReadySeconds); Assert.Equal(600, e.ProgressSeconds); });
        Assert.Equal("IN_BeritraAttack_L_S2", MapEventTables.Embedded.DungeonOfMap(500032)!.Name);
    }

    [Fact]
    public void One_variant_runs_per_slot_and_Beritra_comes_every_third_hour()
    {
        var beritra = GameSession.InvasionEventsOf(2)[0];
        var other = GameSession.InvasionEventsOf(2)[1];
        var prepare = GameSession.FieldEventState(beritra, Slot12 + 60_000);
        Assert.Equal(GameSession.FieldEventPrepare, prepare.State);
        Assert.Equal(Slot12, prepare.Open);
        Assert.Equal(Slot12 + 180_000, prepare.Start);
        Assert.Equal(Slot12 + 780_000, prepare.End);
        Assert.Equal(GameSession.FieldEventNone, GameSession.FieldEventState(other, Slot12 + 60_000).State);
        Assert.Equal(GameSession.FieldEventProgress, GameSession.FieldEventState(beritra, Slot12 + 200_000).State);
        Assert.Equal(GameSession.FieldEventEnd, GameSession.FieldEventState(beritra, Slot12 + 800_000).State);
        Assert.Equal(GameSession.FieldEventPrepare, GameSession.FieldEventState(beritra, Slot12 + 3 * 3_600_000 + 60_000).State);
        Assert.Equal(GameSession.FieldEventNone, GameSession.FieldEventState(beritra, Slot12 + 3_600_000 + 60_000).State);
        Assert.Equal(GameSession.FieldEventPrepare, GameSession.FieldEventState(other, Slot12 + 3_600_000 + 60_000).State);
    }

    [Fact]
    public void The_event_list_carries_the_running_invasion_for_the_players_race()
    {
        var info = GameSession.FieldEventInfo(2, Slot12 + 60_000);
        Assert.Equal(GameSession.FieldEventInfoNt, Op(info));
        Assert.Equal(1, info[2]);
        Assert.Equal(new byte[] { 0x03 }, info[3..4]);
        Assert.Equal(new byte[] { 0x00, 0x60, 0x00 }, GameSession.FieldEventInfo(2, Slot12 + 20 * 60_000));
        GameSession.FieldEventSettings = new FieldEventOptions { Invasions = false };
        Assert.Equal(new byte[] { 0x00, 0x60, 0x00 }, GameSession.FieldEventInfo(2, Slot12 + 60_000));
        Assert.Equal(GameSession.FieldEventNone, GameSession.FieldEventState(GameSession.InvasionEventsOf(2)[0], Slot12 + 60_000).State);
    }

    [Fact]
    public async Task The_entrance_moves_a_level_45_player_into_the_Beritra_map_during_the_entry_window_only()
    {
        var now = Slot12 + 60_000;
        var store = new FakeCharacterStore { Progress = (45, 0) };
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, now, level: 45);
        var raceEvents = GameSession.InvasionEventsOf(session.ProfileRace);
        var beritra = raceEvents[0];
        var late = await session.HandleAsync(new PacketWriter().U16(GameSession.FieldEventEntranceRq).Varint(beritra.Id).ToArray(), Slot12 + 300_000);
        Assert.Equal(4962, BitConverter.ToUInt16(Assert.Single(late, p => Op(p) == GameSession.FieldEventEntranceRs), 2));
        var entered = await session.HandleAsync(new PacketWriter().U16(GameSession.FieldEventEntranceRq).Varint(beritra.Id).ToArray(), now);
        Assert.Equal(0, BitConverter.ToUInt16(Assert.Single(entered, p => Op(p) == GameSession.FieldEventEntranceRs), 2));
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 100);
        Assert.Equal(beritra.Map, session.MapId);
        for (var t = 0; t <= 40; t++) await session.TickAsync(Slot12 + 178_000 + t * 1000);
        Assert.Equal("Process", session.Copy?.Phase);
        // The map's own script (IN_Beritra_01_L_MapEvent: the Dreadgion and the four Beritra waves) runs on the existing map event engine.
        Assert.Contains(session.MapEventRuns, r => r.Name == "IN_Beritra_01_L_MapEvent");
        Assert.Contains(session.Targets.Values, t => t.Hostile && !t.Dead);
    }
}
