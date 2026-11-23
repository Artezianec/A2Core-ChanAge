using Xunit;

namespace Aion.World.Tests;

/// <summary>The scheduled Abyss Rift Zone event: schedule (OFFICIAL), the phase machine over the client's phase table, and the world run.</summary>
[Collection(AbyssRiftZoneCollection.Name)]
public sealed class AbyssRiftZoneTests : IDisposable
{
    // Tuesday 2026-11-24 22:00 on the UTC+9 server clock.
    static readonly long TuesdayOpen = new DateTimeOffset(2026, 11, 24, 13, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    const long Minute = 60_000;

    public AbyssRiftZoneTests() => AbyssRiftZone.Configure(AbyssRiftZoneOptions.Default with { ServerUtcOffsetHours = 9 });
    public void Dispose() => AbyssRiftZone.Configure(AbyssRiftZoneOptions.Default);

    [Fact]
    public void Groups_open_at_22_00_22_05_and_22_10_on_Tuesday_and_Thursday_only()
    {
        Assert.Equal(TuesdayOpen, AbyssRiftZone.LatestOpen("A", TuesdayOpen));
        Assert.Null(AbyssRiftZone.LatestOpen("B", TuesdayOpen + 4 * Minute));
        Assert.Equal(TuesdayOpen + 5 * Minute, AbyssRiftZone.LatestOpen("B", TuesdayOpen + 5 * Minute));
        Assert.Equal(TuesdayOpen + 10 * Minute, AbyssRiftZone.LatestOpen("C", TuesdayOpen + 11 * Minute));
        Assert.Equal(TuesdayOpen + 2 * 24 * 60 * Minute + 10 * Minute, AbyssRiftZone.NextOpen("C", TuesdayOpen + 11 * Minute));
        Assert.Equal(TuesdayOpen + 7 * 24 * 60 * Minute, AbyssRiftZone.NextOpen("A", TuesdayOpen + 2 * 24 * 60 * Minute));
        Assert.False(AbyssRiftZone.Eligible(44, 5000));
        Assert.False(AbyssRiftZone.Eligible(60, 2999));
        Assert.True(AbyssRiftZone.Eligible(45, 3000));
    }

    [Fact]
    public void The_offset_moves_the_schedule()
    {
        AbyssRiftZone.Configure(AbyssRiftZoneOptions.Default with { ServerUtcOffsetHours = 0 });
        Assert.Equal(TuesdayOpen + 9 * 60 * Minute, AbyssRiftZone.LatestOpen("A", TuesdayOpen + 9 * 60 * Minute));
    }

    [Fact]
    public void The_run_walks_the_client_phase_table_to_the_end()
    {
        var phases = AbyssRiftPhases.Embedded.PhasesOf(1)!;
        var run = new AbyssRiftZoneRun(phases, TuesdayOpen, 60);
        Assert.Equal(1, Assert.Single(run.Advance(TuesdayOpen)).Phase);
        Assert.Empty(run.Advance(TuesdayOpen + 5 * Minute - 1));
        var step = Assert.Single(run.Advance(TuesdayOpen + 5 * Minute));
        Assert.Equal(2, step.Phase);
        Assert.Equal(12, step.Spawn.Length);

        var now = TuesdayOpen + 6 * Minute;
        for (var phase = 2; phase <= 6; phase++)
        {
            var need = int.Parse(phases[phase - 1].Ends.Single().Value);
            Assert.Equal(phase, run.Phase);
            for (var i = 0; i < need - 1; i++) run.NoteKill(phases[phase - 1].Npcs[i]);
            Assert.Empty(run.Advance(now));
            run.NoteKill(phases[phase - 1].Npcs[need - 1]);
            Assert.Equal(phase + 1, Assert.Single(run.Advance(now += Minute)).Phase);
        }

        Assert.Equal(7, run.Phase);
        Assert.Equal(20, run.Current!.Npcs.Length);
        Assert.Empty(run.Advance(now + 59 * Minute));
        Assert.Equal(8, Assert.Single(run.Advance(now + 60 * Minute)).Phase);
        now += 61 * Minute;
        var last = phases[7].Npcs;
        foreach (var npc in last.Take(3)) run.NoteKill(npc);
        Assert.Empty(run.Advance(now));
        run.NoteKill(last[3]);
        var end = Assert.Single(run.Advance(now));
        Assert.True(end.Finished);
        Assert.True(run.Finished);
        Assert.Empty(run.Advance(now + Minute));
    }

    [Fact]
    public void A_run_on_the_world_places_each_phase_and_advances_on_kills()
    {
        var world = new SharedWorld(GameData.Embedded);
        var events = world.RiftZone;
        Assert.True(events.NeedsTick(TuesdayOpen));
        var opens = events.Tick(world, TuesdayOpen);
        Assert.Contains(opens, n => n.English.Contains("group A") && n.English.Contains("opens"));
        Assert.Equal(1, events.Current(1)!.Value.Phase);
        var map = world.Map(20);
        var before = map.Targets.Count;

        events.Tick(world, TuesdayOpen + 5 * Minute);
        var phase2 = map.Targets.Values.Where(t => t.Key >= MapInstance.FirstStaffKey).ToList();
        Assert.Equal(12, phase2.Count);
        Assert.Equal(AbyssRiftPhases.Embedded.PhasesOf(1)![1].Npcs.Order(), phase2.Select(t => t.NpcId).Order());
        Assert.Equal(2, events.Current(1)!.Value.Phase);

        foreach (var t in phase2) { t.Hp = 0; map.RespawnAt[t.Key] = TuesdayOpen; }
        var notices = events.Tick(world, TuesdayOpen + 7 * Minute);
        Assert.Contains(notices, n => n.English.Contains("phase 3/8"));
        Assert.Equal(3, events.Current(1)!.Value.Phase);
        Assert.All(phase2, t => Assert.False(map.RespawnAt.ContainsKey(t.Key)));
        Assert.Equal(before + 16, map.Targets.Count);
    }

    [Fact]
    public void A_group_is_not_started_late_nor_twice_and_the_flag_turns_the_event_off()
    {
        var world = new SharedWorld(GameData.Embedded);
        world.RiftZone.Tick(world, TuesdayOpen + 30 * Minute);
        Assert.Null(world.RiftZone.Current(1));
        Assert.Empty(world.RiftZone.ActiveGroups);

        AbyssRiftZone.Configure(AbyssRiftZoneOptions.Default with { Enabled = false, ServerUtcOffsetHours = 9 });
        Assert.False(world.RiftZone.NeedsTick(TuesdayOpen));
        Assert.Null(world.RiftZone.Current(1));
    }
}

public static class AbyssRiftZoneCollection
{
    public const string Name = "AbyssRiftZone";
}

[CollectionDefinition(AbyssRiftZoneCollection.Name, DisableParallelization = true)]
public sealed class AbyssRiftZoneCollectionDefinition;
