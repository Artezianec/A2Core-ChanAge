using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

// Field boss timers, schedules and titles from the public guides (tools/data/field-boss-guide.json, docs/FIELD-BOSSES.md).
// The client has no respawn period for WorldMapFieldNamed (PeriodSpawnId names a server table) and no English titles, so this
// is guide data (confirmed / likely / guess per entry), switched by Aion:FieldBosses:GuideTimers and :Schedules.

/// <summary>A boss that appears on a fixed server-clock schedule (the Abyss bosses) instead of a time after its kill.</summary>
public sealed record FieldBossSchedule(string Id, DayOfWeek[] Days, TimeSpan[] Times, bool RandomSpot)
{
    /// <summary>The first slot strictly after <paramref name="nowMs"/> (Unix ms) on a server clock of UTC + <paramref name="offsetHours"/>.</summary>
    public long NextSlotAfter(long nowMs, int offsetHours)
    {
        var offset = TimeSpan.FromHours(offsetHours);
        var local = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).ToOffset(offset);
        for (var day = 0; day <= 8; day++)
        {
            var date = local.Date.AddDays(day);
            if (Array.IndexOf(Days, date.DayOfWeek) < 0) continue;
            foreach (var time in Times)
            {
                var at = new DateTimeOffset(date + time, offset);
                if (at > local) return at.ToUnixTimeMilliseconds();
            }
        }
        throw new InvalidOperationException($"Field boss schedule {Id} has no slot");
    }
}

/// <summary>The guide data: after-kill timers and scheduled slots per NpcData id, and the titles the client strings do not carry.</summary>
public sealed class FieldBossGuide
{
    public sealed record Timer(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("npcs")] int[] Npcs,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("name_ru")] string? NameRu,
        [property: JsonPropertyName("respawn_minutes")] int RespawnMinutes,
        [property: JsonPropertyName("confidence")] string Confidence);

    public sealed record Title(
        [property: JsonPropertyName("npcs")] int[] Npcs,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("name_ru")] string? NameRu,
        [property: JsonPropertyName("confidence")] string Confidence);

    public sealed record ScheduleRow(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("npcs")] int[] Npcs,
        [property: JsonPropertyName("days")] JsonElement Days,
        [property: JsonPropertyName("times")] string[] Times,
        [property: JsonPropertyName("random_spot")] bool RandomSpot,
        [property: JsonPropertyName("confidence")] string Confidence);

    sealed record File(
        [property: JsonPropertyName("server_utc_offset_hours")] int ServerUtcOffsetHours,
        [property: JsonPropertyName("spawn_window_minutes")] int SpawnWindowMinutes,
        [property: JsonPropertyName("timers")] Timer[] Timers,
        [property: JsonPropertyName("titles")] Title[] Titles,
        [property: JsonPropertyName("schedules")] ScheduleRow[] Schedules);

    readonly Dictionary<int, Timer> timerByNpc = [];
    readonly Dictionary<int, (string English, string? Russian)> titleByNpc = [];
    readonly Dictionary<int, FieldBossSchedule> scheduleByNpc = [];
    readonly Dictionary<int, string> scheduleConfidence = [];

    FieldBossGuide(File file)
    {
        titleBySpawn = new(TitlesBySpawn);
        DefaultUtcOffsetHours = file.ServerUtcOffsetHours;
        SpawnWindowMinutes = file.SpawnWindowMinutes;
        Timers = file.Timers;
        Titles = file.Titles;
        foreach (var timer in file.Timers)
            foreach (var npc in timer.Npcs)
            {
                timerByNpc[npc] = timer;
                titleByNpc[npc] = (timer.Name, timer.NameRu);
            }
        foreach (var title in file.Titles)
            foreach (var npc in title.Npcs) titleByNpc[npc] = (title.Name, title.NameRu);
        var schedules = new List<FieldBossSchedule>();
        foreach (var row in file.Schedules)
        {
            var days = row.Days.ValueKind == JsonValueKind.String
                ? Enum.GetValues<DayOfWeek>()
                : row.Days.EnumerateArray().Select(d => Enum.Parse<DayOfWeek>(d.GetString()!)).ToArray();
            var times = row.Times.Select(t => TimeSpan.ParseExact(t, @"hh\:mm", CultureInfo.InvariantCulture)).Order().ToArray();
            var schedule = new FieldBossSchedule(row.Id, days, times, row.RandomSpot);
            schedules.Add(schedule);
            foreach (var npc in row.Npcs)
            {
                scheduleByNpc[npc] = schedule;
                scheduleConfidence[npc] = row.Confidence;
            }
        }
        Schedules = schedules;
    }

    /// <summary>The server clock's offset from UTC (hours) in the guide; Aion:FieldBosses:ServerUtcOffsetHours overrides it. A GUESS.</summary>
    public int DefaultUtcOffsetHours { get; }
    /// <summary>The boss may appear this long (minutes) after its countdown ends.</summary>
    public int SpawnWindowMinutes { get; }
    public IReadOnlyList<Timer> Timers { get; }
    public IReadOnlyList<Title> Titles { get; }
    public IReadOnlyList<FieldBossSchedule> Schedules { get; }

    public int? RespawnMinutesOf(int npcId) => timerByNpc.TryGetValue(npcId, out var t) ? t.RespawnMinutes : null;
    public FieldBossSchedule? ScheduleOf(int npcId) => scheduleByNpc.GetValueOrDefault(npcId);
    public (string English, string? Russian)? TitleOf(int npcId) => titleByNpc.TryGetValue(npcId, out var t) ? t : null;

    /// <summary>The guide title of an official field boss by its spawn name (the NpcData / MapData name the targets carry).</summary>
    public (string English, string? Russian)? TitleOfSpawn(string spawnName) => titleBySpawn.Value.TryGetValue(spawnName, out var t) ? t : null;

    /// <summary>The Russian name of a guide title (an English title as the notices carry it).</summary>
    public string? RussianOf(string english) => titleByNpc.Values.FirstOrDefault(t => t.English == english).Russian;

    Lazy<Dictionary<string, (string English, string? Russian)>> titleBySpawn = null!;

    Dictionary<string, (string English, string? Russian)> TitlesBySpawn()
    {
        var data = GameData.Embedded;
        var map = new Dictionary<string, (string English, string? Russian)>(StringComparer.Ordinal);
        foreach (var (_, mapId, npcId) in data.FieldBosses)
            if (titleByNpc.TryGetValue(npcId, out var title))
                foreach (var spawn in data.Spawns(mapId).Where(s => s.NpcId == npcId)) map[spawn.Name] = title;
        return map;
    }

    public static FieldBossGuide Parse(string json) =>
        new(JsonSerializer.Deserialize<File>(json) ?? throw new InvalidOperationException("Empty field-boss-guide.json"));

    static readonly Lazy<FieldBossGuide> embedded = new(() =>
    {
        using var stream = typeof(FieldBossGuide).Assembly.GetManifestResourceStream("field-boss-guide.json")
            ?? throw new InvalidOperationException("field-boss-guide.json is not embedded");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    public static FieldBossGuide Embedded => embedded.Value;
}
