using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Guide timers, Abyss boss schedules and titles (tools/data/field-boss-guide.json; guide values, not client data).</summary>
[Collection(FieldBossCollection.Name)]
public sealed class FieldBossGuideTests : IDisposable
{
    static readonly TimeSpan Kst = TimeSpan.FromHours(9);
    const long Minute = 60_000;

    public FieldBossGuideTests() { FieldBosses.Configure(FieldBossOptions.Default); FieldBosses.Clear(); }
    public void Dispose() { FieldBosses.Configure(FieldBossOptions.Default); FieldBosses.Clear(); }

    static long At(int year, int month, int day, int hour, int minute) => new DateTimeOffset(year, month, day, hour, minute, 0, Kst).ToUnixTimeMilliseconds();

    static FieldBossSchedule Schedule(string id) => FieldBossGuide.Embedded.Schedules.Single(s => s.Id == id);

    [Fact]
    public void Every_guide_npc_is_an_official_field_boss_row_of_the_client()
    {
        var guide = FieldBossGuide.Embedded;
        var official = GameData.Embedded.FieldBosses.Select(b => b.NpcId).ToHashSet();
        Assert.All(guide.Timers.SelectMany(t => t.Npcs).Concat(guide.Titles.SelectMany(t => t.Npcs)).Concat(guide.Schedules.SelectMany(s => ScheduleNpcs(guide, s))),
            npc => Assert.Contains(npc, official));
        // The 13 timed or tracked bosses: 5 after-kill families (Gartua x2, Lagta, Dartan, Kashapa) and the 6 Abyss schedules.
        Assert.Equal(5, guide.Timers.Count);
        Assert.Equal(6, guide.Schedules.Count);
    }

    static IEnumerable<int> ScheduleNpcs(FieldBossGuide guide, FieldBossSchedule schedule) =>
        GameData.Embedded.FieldBosses.Select(b => b.NpcId).Where(n => guide.ScheduleOf(n) == schedule);

    [Fact]
    public void After_kill_timers_are_the_guides_plus_the_spawn_window()
    {
        var timers = new FieldBossTimers(GameData.Embedded, new Random(1));
        int Code(int npc) => timers.Definitions.First(d => d.NpcId == npc).Code;
        Assert.Equal((660, 660), timers.Range(Code(2101074)));   // Eternal Gartua
        Assert.Equal((660, 660), timers.Range(Code(2400800)));   // Immortal Gartua
        Assert.Equal((660, 660), timers.Range(Code(2101131)));   // Lagta (Elyos)
        Assert.Equal((660, 660), timers.Range(Code(2400853)));   // Lagta (Asmodian)
        Assert.Equal((360, 360), timers.Range(Code(2101120)));   // Dartan
        Assert.Equal((360, 360), timers.Range(Code(2400854)));   // Kashapa
        // The Lv 45 named elites have no published timer: the generic window.
        Assert.Equal((FieldBossTimers.NamedMinMinutes, FieldBossTimers.NamedMaxMinutes), timers.Range(101001));

        const long now = 1_793_000_000_000;
        for (var i = 0; i < 20; i++)
            Assert.InRange(timers.Killed(Code(2101074), now), now + 660 * Minute, now + 670 * Minute);
        FieldBosses.Configure(new FieldBossOptions { GuideTimers = false });
        Assert.Equal((FieldBossTimers.BossMinMinutes, FieldBossTimers.BossMaxMinutes), timers.Range(Code(2101074)));
    }

    [Fact]
    public void Abyss_bosses_come_at_the_next_server_clock_slot()
    {
        var tuesday = At(2026, 11, 24, 0, 30);   // 2026-11-24 is a Tuesday
        Assert.Equal(At(2026, 11, 24, 1, 0), Schedule("watcher_kaira").NextSlotAfter(tuesday, 9));
        Assert.Equal(At(2026, 11, 24, 4, 0), Schedule("watcher_kaira").NextSlotAfter(At(2026, 11, 24, 1, 0), 9));
        Assert.Equal(At(2026, 11, 25, 1, 0), Schedule("watcher_kaira").NextSlotAfter(At(2026, 11, 24, 22, 0), 9));
        Assert.Equal(At(2026, 11, 24, 10, 30), Schedule("argo_spirit_king").NextSlotAfter(tuesday, 9));
        Assert.Equal(At(2026, 11, 24, 22, 30), Schedule("argo_spirit_king").NextSlotAfter(At(2026, 11, 24, 10, 30), 9));
        Assert.Equal(At(2026, 11, 25, 10, 30), Schedule("argo_spirit_king").NextSlotAfter(At(2026, 11, 24, 23, 0), 9));
        Assert.Equal(At(2026, 11, 27, 21, 0), Schedule("guardian_lord_nahma").NextSlotAfter(tuesday, 9));      // Friday
        Assert.Equal(At(2026, 11, 29, 21, 0), Schedule("guardian_lord_nahma").NextSlotAfter(At(2026, 11, 27, 21, 0), 9));   // Sunday
        Assert.Equal(At(2026, 11, 27, 21, 0), Schedule("enraged_guardian_lord_nahma").NextSlotAfter(tuesday, 9));
        Assert.Equal(At(2026, 11, 26, 21, 30), Schedule("executors").NextSlotAfter(tuesday, 9));               // Thursday
        Assert.Equal(At(2026, 11, 26, 21, 30), Schedule("middle_reshanta_trio").NextSlotAfter(tuesday, 9));
        Assert.Equal(At(2026, 11, 30, 21, 30), Schedule("middle_reshanta_trio").NextSlotAfter(At(2026, 11, 28, 21, 30), 9));   // Saturday -> Monday
        // The same instant on a UTC server clock is a different slot (01:00 UTC is 10:00 KST).
        Assert.Equal(At(2026, 11, 24, 10, 0), Schedule("watcher_kaira").NextSlotAfter(At(2026, 11, 24, 9, 30), 0));
    }

    [Fact]
    public void A_killed_abyss_boss_returns_at_the_next_slot_and_a_stored_range_wins()
    {
        var timers = new FieldBossTimers(GameData.Embedded, new Random(1));
        var nahma = timers.Definitions.First(d => d.NpcId == 2600084).Code;
        var kaira = timers.Definitions.First(d => d.NpcId == 2600089).Code;
        var tuesday = At(2026, 11, 24, 0, 30);
        Assert.Equal(At(2026, 11, 27, 21, 0), timers.Killed(nahma, tuesday));
        Assert.Equal(At(2026, 11, 24, 1, 0), timers.Killed(kaira, tuesday));
        Assert.Equal(9, timers.UtcOffsetHours);

        // A staff-stored range replaces the schedule for that boss.
        Assert.True(timers.SetRange(nahma, 30, 30));
        Assert.Null(timers.ScheduleOf(nahma));
        Assert.Equal(tuesday + 30 * Minute, timers.Killed(nahma, tuesday));
        Assert.True(timers.SetRange(nahma, null, null));
        Assert.NotNull(timers.ScheduleOf(nahma));

        FieldBosses.Configure(new FieldBossOptions { Schedules = false });
        Assert.Null(timers.ScheduleOf(kaira));
        FieldBosses.Configure(new FieldBossOptions { ServerUtcOffsetHours = 0 });
        Assert.Equal(At(2026, 11, 24, 10, 0), timers.Killed(kaira, At(2026, 11, 24, 9, 30)));   // 01:00 UTC = 10:00 KST
        Assert.Throws<InvalidOperationException>(() => new FieldBossOptions { ServerUtcOffsetHours = 20 }.Validated());
    }

    [Fact]
    public void Watcher_Kaira_is_up_at_one_of_its_six_placements_and_returns_at_another()
    {
        const long now = 1_793_000_000_000;
        var world = new SharedWorld(GameData.Embedded) { Clock = () => now };
        var map = world.Map(20);
        var placements = map.Targets.Values.Where(t => t.NpcId == 2600089).ToList();
        Assert.Equal(6, placements.Count);
        Assert.Single(placements, t => !t.Dead);

        var up = placements.Single(t => !t.Dead);
        Assert.Same(up, map.FieldBossTarget(world.FieldBossTimers.Definitions.First(d => d.NpcId == 2600089).Code));
        var due = map.RecordFieldBossKill(up, now);
        Assert.NotNull(due);
        up.Hp = 0;
        var next = map.SpotToRespawn(up);
        Assert.NotSame(up, next);
        Assert.Equal(2600089, next.NpcId);
        Assert.True(next.Dead);
        // Whatever comes next is the boss now: a kill there is the boss's kill.
        Assert.NotNull(map.RecordFieldBossKill(next, now));
        Assert.Equal(1, placements.Count(t => t.Key == next.Key));

        // Another boss is unaffected.
        var ordinary = map.Targets.Values.First(t => t.NpcId == 2600068);
        Assert.Same(ordinary, map.SpotToRespawn(ordinary));
    }

    [Fact]
    public void The_guide_names_the_bosses_by_spawn_name_and_ducal_matches_its_client_skills()
    {
        var guide = FieldBossGuide.Embedded;
        Assert.Equal("Eternal Gartua", guide.TitleOfSpawn("World_L_A_Single_IslandOfEternity_Boss_1")!.Value.English);
        Assert.Equal("Turncoat Ducal", guide.TitleOf(2600521)!.Value.English);
        var skills = GameData.Embedded.NpcFromTemplate(2600521, "x", [0, 0, 0], 0)!.Skills!;
        var data = GameData.Embedded;
        Assert.NotNull(skills);
        Assert.Contains(1808250, skills);   // Smite Ground: the 1.5 s skill with the 2 s abnormal
        Assert.Contains(1808270, skills);   // Greatsword Plunge: the 2 s cast
        _ = data;
    }
}
