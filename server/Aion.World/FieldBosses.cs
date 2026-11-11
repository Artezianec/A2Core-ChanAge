using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Aion.World;

// Open-world field bosses (docs/FIELD-BOSSES.md). Ported from ckrealm/A2Core 77ac9ed (FunFeatures.cs, AGPL-3.0) with retail-like
// timers: Aion 2's field bosses come back hours after their kill (the Korean and global guides list 30 min to 11 h; Gartua and
// Lagta 11 h, Dartan and Kashapa 6 h), where ckrealm used 60-90 min.

/// <summary>Aion:FieldBosses: open-world HeroMonster respawn windows, server-wide notices and loot for everyone who fought.</summary>
public sealed record FieldBossOptions
{
    /// <summary>Off: every monster respawns after GameSession.RespawnMs, no notices, killer/party loot only.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>Field boss respawn window (minutes after the kill); each kill picks a random time inside it. Retail-like 6 h.</summary>
    public int BossRespawnMinMinutes { get; init; } = 360;
    public int BossRespawnMaxMinutes { get; init; } = 360;
    /// <summary>
    /// Fixed respawns (minutes) by a part of the spawn name, ahead of the window. Gartua and Lagta 11 h are the retail guides'
    /// figures; Ragta (Lagta's Light-side twin) and the Island of Eternity boss (Gartua's Light-side twin) are INFERRED to match.
    /// </summary>
    public Dictionary<string, int> RespawnMinutesByName { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Gartua"] = 660, ["Lagta"] = 660, ["Ragta"] = 660, ["IslandOfEternity"] = 660,
    };
    /// <summary>Named elites (the other open-world HeroMonsters): their respawn window in minutes (OURS: no retail figure).</summary>
    public int NamedRespawnMinMinutes { get; init; } = 30;
    public int NamedRespawnMaxMinutes { get; init; } = 60;
    /// <summary>Server-wide notices (centre of the screen in each client's language, EN and RU, plus chat) on a boss's kill and its return.</summary>
    public bool Announcements { get; init; } = true;
    /// <summary>Everyone on the boss's hate table rolls its loot on their own (not only the killer and the killer's party).</summary>
    public bool SharedLoot { get; init; } = true;

    public static FieldBossOptions Default { get; } = new();

    public FieldBossOptions Validated()
    {
        static void Window(string name, int min, int max)
        {
            if (min < 1 || max < min || max > 48 * 60)
                throw new InvalidOperationException($"Aion:FieldBosses:{name}RespawnMinMinutes/MaxMinutes must be 1..2880 with min <= max.");
        }
        Window("Boss", BossRespawnMinMinutes, BossRespawnMaxMinutes);
        Window("Named", NamedRespawnMinMinutes, NamedRespawnMaxMinutes);
        if (RespawnMinutesByName.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is < 1 or > 48 * 60))
            throw new InvalidOperationException("Aion:FieldBosses:RespawnMinutesByName values must be 1..2880 minutes.");
        return this;
    }

    public override string ToString() => Enabled
        ? $"on (bosses {BossRespawnMinMinutes}-{BossRespawnMaxMinutes} min, "
          + string.Join(", ", RespawnMinutesByName.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key} {p.Value} min"))
          + $"; named {NamedRespawnMinMinutes}-{NamedRespawnMaxMinutes} min; notices {(Announcements ? "on" : "off")}; shared loot {(SharedLoot ? "on" : "off")})"
        : "off";
}

/// <summary>An open-world monster's place in the field boss rules.</summary>
public enum FieldRank { None, Named, Boss }

/// <summary>A server-wide notice: English and Russian text (the client shows the one of its language).</summary>
public sealed record FieldBossNotice(string English, string Russian);

/// <summary>The field boss rules (ranks, timers, names) and the notices the sessions queue for the host to show to everyone.</summary>
public static class FieldBosses
{
    static FieldBossOptions options = FieldBossOptions.Default;
    static readonly ConcurrentQueue<FieldBossNotice> now = new();
    static readonly List<(long DueUtcMs, FieldBossNotice Notice)> later = [];
    static readonly object laterLock = new();

    public static FieldBossOptions Options => options;

    /// <summary>Aion:FieldBosses for every session of this host (Program.cs; tests set it back with <see cref="FieldBossOptions.Default"/>).</summary>
    public static void Configure(FieldBossOptions value) => options = value.Validated();

    /// <summary>The wall clock the delayed notices use (sessions' loop clocks differ); tests replace it.</summary>
    public static Func<long> UtcMs { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Queues a notice now, or <paramref name="inMs"/> from now (a boss's return).</summary>
    public static void Announce(FieldBossNotice notice, long inMs = 0)
    {
        if (!options.Announcements) return;
        if (inMs <= 0) { now.Enqueue(notice); return; }
        lock (laterLock) later.Add((UtcMs() + inMs, notice));
    }

    /// <summary>The notices due, oldest first; each is returned once (the host's FieldBossNoticeService).</summary>
    public static List<FieldBossNotice> TakeDue()
    {
        var due = new List<FieldBossNotice>();
        while (now.TryDequeue(out var n)) due.Add(n);
        var at = UtcMs();
        lock (laterLock)
        {
            foreach (var n in later.Where(n => n.DueUtcMs <= at).OrderBy(n => n.DueUtcMs).ToList())
            {
                later.Remove(n);
                due.Add(n.Notice);
            }
        }
        return due;
    }

    /// <summary>Notices waiting for their time (boss returns), for tests and the log.</summary>
    public static IReadOnlyList<(long DueUtcMs, FieldBossNotice Notice)> Pending { get { lock (laterLock) return [.. later]; } }

    /// <summary>Drops every queued notice (tests).</summary>
    public static void Clear()
    {
        while (now.TryDequeue(out _)) { }
        lock (laterLock) later.Clear();
    }

    /// <summary>
    /// A spawn's field rank: hostile open-world HeroMonsters only (never in a dungeon, a scripted layer or a quest's support spawn).
    /// Bosses: the spawn names a boss (M_*_FieldBoss_*, M_AR*_AbBoss_*, World_*_IslandOfEternity_Boss_*, *Gartua*) or grade 6+;
    /// the rest (M_*_Named_*, the elite HeroMonsters of grades 2-5) are named elites.
    /// </summary>
    public static FieldRank Rank(Target target, bool inInstance)
    {
        if (inInstance || target.SubType != "HeroMonster" || !target.Hostile || target.OwnerKey != 0 || BossSummons.IsSummon(target.Key)) return FieldRank.None;
        if (target.Name.Contains("QuestSup", StringComparison.Ordinal) || target.Name.Contains("Artifact", StringComparison.Ordinal)) return FieldRank.None;
        return target.Name.Contains("Boss", StringComparison.Ordinal) || target.Name.Contains("Gartua", StringComparison.Ordinal) || target.Grade >= 6
            ? FieldRank.Boss : FieldRank.Named;
    }

    /// <summary>The respawn delay (ms) of a field-ranked monster: a fixed time by name, else a random time in its window.</summary>
    public static long RespawnMs(Target target, FieldRank rank, Random rng)
    {
        if (rank == FieldRank.Boss)
            foreach (var (part, minutes) in options.RespawnMinutesByName.OrderByDescending(p => p.Key.Length))
                if (target.Name.Contains(part, StringComparison.OrdinalIgnoreCase)) return minutes * 60_000L;
        var (min, max) = rank == FieldRank.Boss
            ? (options.BossRespawnMinMinutes, options.BossRespawnMaxMinutes)
            : (options.NamedRespawnMinMinutes, options.NamedRespawnMaxMinutes);
        return (long)((min + rng.NextDouble() * (max - min)) * 60_000);
    }

    static readonly Regex Noise = new(@"^(M|N|World|[LD]\d|AR\d|\d+|V\d+|CV\d+|[A-Z]{1,3}|IoI|FieldBoss|Named|Single|EOR|Ab)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// A readable name from a spawn name (the server data has no display names): M_D1_FieldBoss_Kashapa_02_1 → Kashapa,
    /// M_D1_IoI_OdiumGartua_01 → Odium Gartua, World_L_A_Single_IslandOfEternity_Boss_1 → Island Of Eternity Boss.
    /// </summary>
    public static string DisplayName(string spawnName)
    {
        var words = spawnName.Split('_', StringSplitOptions.RemoveEmptyEntries).Where(w => !Noise.IsMatch(w)).ToList();
        if (words.Count == 0) return spawnName;
        var text = new StringBuilder();
        foreach (var word in words)
        {
            // BeritraD → Beritra (a one-letter variant suffix), then split CamelCase.
            var w = word.Length > 2 && char.IsUpper(word[^1]) && char.IsLower(word[^2]) ? word[..^1] : word;
            for (var i = 0; i < w.Length; i++)
            {
                if (i > 0 && char.IsUpper(w[i]) && char.IsLower(w[i - 1])) text.Append(' ');
                text.Append(w[i]);
            }
            text.Append(' ');
        }
        return text.ToString().Trim();
    }

    /// <summary>Russian names of the field bosses (OURS transliterations; the server has no client string table for them).</summary>
    static readonly Dictionary<string, string> RussianNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Kashapa"] = "Кашапа", ["Lagta"] = "Лагта", ["Ragta"] = "Рагта", ["Beritra"] = "Беритра", ["Dragon"] = "Дракон",
        ["Odium Gartua"] = "Одиум Гартуа", ["Island Of Eternity Boss"] = "Босс Острова Вечности", ["Boss"] = "Босс Бездны",
        ["Pretor Boss"] = "Претор",
    };

    public static string RussianName(string displayName) => RussianNames.GetValueOrDefault(displayName, displayName);

    /// <summary>The side of a base map: 10xx the Light lands, 11xx the Dark lands, the rest the Abyss.</summary>
    public static (string English, string Russian) Region(int mapId) => (mapId / 100) switch
    {
        10 => ("the Light lands", "земли Света"),
        11 => ("the Dark lands", "земли Тьмы"),
        _ => ("the Abyss", "Бездна"),
    };

    /// <summary>"{killer} defeated the field boss …" (EN, RU); <paramref name="others"/> who fought along.</summary>
    public static FieldBossNotice Defeated(string killer, int others, string spawnName, int level, int mapId)
    {
        var name = DisplayName(spawnName);
        var (en, ru) = Region(mapId);
        var lvl = level.ToString(CultureInfo.InvariantCulture);
        var who = others > 0 ? $"{killer} and {others} other{(others == 1 ? "" : "s")}" : killer;
        var whoRu = others > 0 ? $"{killer} и ещё {others}" : killer;
        return new($"{who} defeated the field boss {name} (level {lvl}) in {en}!",
            $"{whoRu}: полевой босс {RussianName(name)} ({lvl} ур.) повержен — {ru}!");
    }

    /// <summary>"The field boss … has appeared" (EN, RU).</summary>
    public static FieldBossNotice Appeared(string spawnName, int level, int mapId)
    {
        var name = DisplayName(spawnName);
        var (en, ru) = Region(mapId);
        var lvl = level.ToString(CultureInfo.InvariantCulture);
        return new($"The field boss {name} (level {lvl}) has appeared in {en}.",
            $"Полевой босс {RussianName(name)} ({lvl} ур.) появился — {ru}.");
    }
}
