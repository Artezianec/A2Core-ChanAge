using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

// Boss phases and scripted waves (docs/BOSSES.md). Ported from ckrealm/A2Core (branch friends-server, c299686 / c52fc1a;
// AGPL-3.0, same upstream), fitted to this server's NPC AI (monster volleys, the client's skill-areas.json shapes).
// tools/data/boss-phases.json is written by tools/boss_phases.py from the client's own NPC data. The client data has no boss
// AI tables (its MapEvent scripts only spawn, talk and open doors), so the phases are hand-made per encounter role and mode
// (OURS). The client's own: the bosses, their skills and cast times (the long-cast specials such as KraoCave_Boss_1's 8 s
// skill 1800140 become the telegraphed slams), the slams' area shapes when skill-areas.json has them, the adds (each
// dungeon's own monsters), and the hard modes' 4-minute frenzy (global's Expedition hard-mode rule since 2026-10-21).

/// <summary>
/// A telegraphed slam: a wind-up (the skill's cast, or <see cref="WindUpMs"/>), then a hit on every player inside its area:
/// the skill's client shape (skill-areas.json) when it has one, else a circle of <see cref="Radius"/> (OURS).
/// </summary>
public sealed record BossTelegraph(
    [property: JsonPropertyName("skill")] int Skill,
    [property: JsonPropertyName("every_ms")] int EveryMs,
    [property: JsonPropertyName("wind_up_ms")] int WindUpMs,
    [property: JsonPropertyName("radius")] double Radius,
    [property: JsonPropertyName("damage")] double Damage = 1.0,
    [property: JsonPropertyName("warning")] string? Warning = null,
    [property: JsonPropertyName("warning_ru")] string? WarningRu = null,
    [property: JsonPropertyName("radius_from")] string? RadiusFrom = null,
    [property: JsonPropertyName("wind_up_from")] string? WindUpFrom = null,
    [property: JsonPropertyName("once")] bool Once = false,
    [property: JsonPropertyName("stagger_check")] bool StaggerCheck = false,
    [property: JsonPropertyName("land")] bool Land = true);

/// <summary>Adds summoned around the boss: copies of a monster of the same dungeon, sent at the boss's victim.</summary>
public sealed record BossAdds(
    [property: JsonPropertyName("npc")] int Npc,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("radius")] double Radius = 500);

/// <summary>A phase: from <see cref="At"/> percent HP down, the boss hits harder, attacks faster, calls adds, changes its slam.</summary>
public sealed record BossPhase(
    [property: JsonPropertyName("at")] int At,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("damage")] double Damage = 1.0,
    [property: JsonPropertyName("haste")] double Haste = 1.0,
    [property: JsonPropertyName("adds")] BossAdds? Adds = null,
    [property: JsonPropertyName("telegraph")] BossTelegraph? Telegraph = null,
    [property: JsonPropertyName("notice")] string? Notice = null,
    [property: JsonPropertyName("notice_ru")] string? NoticeRu = null);

/// <summary>The frenzy: this long into the fight the boss's hits and attack speed go up for good (global hard modes: 4 minutes).</summary>
public sealed record BossScriptFrenzy(
    [property: JsonPropertyName("after_ms")] int AfterMs,
    [property: JsonPropertyName("damage")] double Damage,
    [property: JsonPropertyName("haste")] double Haste,
    [property: JsonPropertyName("notice")] string? Notice = null,
    [property: JsonPropertyName("notice_ru")] string? NoticeRu = null);

/// <summary>One boss's script: its slam from the pull, its phases (highest first) and its frenzy.</summary>
public sealed record BossScript(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("dungeon")] string Dungeon,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("telegraph")] BossTelegraph? Telegraph = null,
    [property: JsonPropertyName("telegraphs")] BossTelegraph[]? Telegraphs = null,
    [property: JsonPropertyName("phases")] BossPhase[]? Phases = null,
    [property: JsonPropertyName("frenzy")] BossScriptFrenzy? Frenzy = null,
    [property: JsonPropertyName("display")] string? Display = null,
    [property: JsonPropertyName("display_ru")] string? DisplayRu = null,
    [property: JsonPropertyName("source")] string? Source = null)
{
    public IReadOnlyList<BossPhase> Ordered { get; } = (Phases ?? []).OrderByDescending(p => p.At).ToList();

    /// <summary>The filler slam rotation (official overlays), or the single pull telegraph.</summary>
    public IReadOnlyList<BossTelegraph> Rotation { get; } = Telegraphs is { Length: > 0 } list ? list
        : Telegraph is { } one ? [one] : [];

    /// <summary>Every slam skill of the script (the pull's, the rotation's and the phases'): the boss does not also use them as ordinary specials.</summary>
    public IReadOnlySet<int> SlamSkills { get; } = new[] { Telegraph }.Concat(Telegraphs ?? []).Concat((Phases ?? []).Select(p => p.Telegraph))
        .Where(t => t is not null).Select(t => t!.Skill).ToHashSet();
}

/// <summary>
/// A scripted wave the spawn data does not name (OURS): a MapEvent StartWave whose spawn entries are not called
/// "{wave}_{n}" (Ferocious Horn Den's totem wave). Copies of <see cref="Npc"/> come around <see cref="Around"/> every
/// <see cref="EveryMs"/> while it lives, at most <see cref="Pulses"/> times; the wave is clear when it and they are dead.
/// </summary>
public sealed record SyntheticWave(
    [property: JsonPropertyName("map")] int Map,
    [property: JsonPropertyName("around")] string Around,
    [property: JsonPropertyName("npc")] int Npc,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("every_ms")] int EveryMs,
    [property: JsonPropertyName("pulses")] int Pulses,
    [property: JsonPropertyName("radius")] double Radius = 600);

/// <summary>Aion:Bosses (OURS): switches for the boss phases and dungeon waves; all on by default.</summary>
public sealed record BossOptions
{
    /// <summary>Phases, adds and slams (off: every boss fights as before).</summary>
    public bool Phases { get; init; } = true;
    /// <summary>The hard modes' frenzy (global: 4 minutes into the boss fight).</summary>
    public bool Frenzy { get; init; } = true;
    /// <summary>Override of every script's frenzy time in seconds (0: the script's, 240 on hard modes).</summary>
    public int FrenzySeconds { get; init; }
    /// <summary>Scales every slam's and phase's damage (1.0: as scripted). Lower it for a casual group.</summary>
    public double DamageScale { get; init; } = 1.0;
    /// <summary>ClearWave follows the wave's own monsters (off: the old 30 s fallback for every wave).</summary>
    public bool Waves { get; init; } = true;
    /// <summary>The next part of a wave comes this long after the last one even if it still lives (OURS).</summary>
    public int WavePartSeconds { get; init; } = 25;
    public static BossOptions Default { get; } = new();

    public BossOptions Validated()
    {
        if (FrenzySeconds is < 0 or > 3600) throw new InvalidOperationException($"Aion:Bosses:FrenzySeconds must be between 0 and 3600 (got {FrenzySeconds}).");
        if (DamageScale is < 0.1 or > 10 || double.IsNaN(DamageScale)) throw new InvalidOperationException($"Aion:Bosses:DamageScale must be between 0.1 and 10 (got {DamageScale}).");
        if (WavePartSeconds is < 1 or > 600) throw new InvalidOperationException($"Aion:Bosses:WavePartSeconds must be between 1 and 600 (got {WavePartSeconds}).");
        return this;
    }

    public override string ToString() => (!Phases ? "boss phases off"
        : $"boss phases on, frenzy {(Frenzy ? FrenzySeconds > 0 ? $"after {FrenzySeconds} s" : "as scripted" : "off")}, damage x{DamageScale.ToString("0.##", CultureInfo.InvariantCulture)}")
        + (Waves ? $", waves tracked (next part after {WavePartSeconds} s)" : ", waves on the timer");
}

/// <summary>The embedded boss-phases.json: scripts by NpcData id and the synthetic waves by wave name.</summary>
public sealed class BossPhases
{
    sealed record File(
        [property: JsonPropertyName("bosses")] Dictionary<string, BossScript> Bosses,
        [property: JsonPropertyName("waves")] Dictionary<string, SyntheticWave>? Waves = null);

    readonly Dictionary<int, BossScript> bosses;
    readonly Dictionary<(int Map, string Wave), SyntheticWave> waves;

    public BossPhases(IReadOnlyDictionary<int, BossScript> bosses, IReadOnlyDictionary<string, SyntheticWave>? waves = null)
    {
        this.bosses = new(bosses);
        this.waves = (waves ?? new Dictionary<string, SyntheticWave>())
            .ToDictionary(p => (p.Value.Map, p.Key.Split('@')[0]), p => p.Value, new WaveKeyComparer());
    }

    sealed class WaveKeyComparer : IEqualityComparer<(int Map, string Wave)>
    {
        public bool Equals((int Map, string Wave) a, (int Map, string Wave) b) => a.Map == b.Map && string.Equals(a.Wave, b.Wave, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((int Map, string Wave) k) => HashCode.Combine(k.Map, StringComparer.OrdinalIgnoreCase.GetHashCode(k.Wave));
    }

    public static BossPhases Parse(string json)
    {
        var file = JsonSerializer.Deserialize<File>(json) ?? throw new InvalidOperationException("Empty boss-phases.json");
        return new(file.Bosses.ToDictionary(p => int.Parse(p.Key, CultureInfo.InvariantCulture), p => p.Value), file.Waves);
    }

    static readonly Lazy<BossPhases> embedded = new(() =>
    {
        using var stream = typeof(BossPhases).Assembly.GetManifestResourceStream("boss-phases.json")
            ?? throw new InvalidOperationException("Missing embedded game data boss-phases.json");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    public static BossPhases Embedded => embedded.Value;
    public static BossPhases None { get; } = new(new Dictionary<int, BossScript>());

    public int Count => bosses.Count;
    public IEnumerable<(int NpcId, BossScript Script)> All => bosses.Select(p => (p.Key, p.Value));
    public BossScript? Of(int npcId) => bosses.GetValueOrDefault(npcId);
    /// <summary>The hand-made wave of this map's StartWave name (keys "{wave}" with the map in the entry), or null.</summary>
    public SyntheticWave? Wave(int map, string wave) => waves.GetValueOrDefault((map, wave));
    public int WaveCount => waves.Count;
}

/// <summary>A boss's running fight: phases reached, current multipliers, the next slam, the adds it called.</summary>
public sealed class BossFight(BossScript script, long startedMs)
{
    public BossScript Script { get; } = script;
    public long StartedMs { get; } = startedMs;
    /// <summary>Phases applied so far (of <see cref="BossScript.Ordered"/>, highest first).</summary>
    public int Reached { get; set; }
    public double Damage { get; set; } = 1.0;
    public double Haste { get; set; } = 1.0;
    public bool Frenzied { get; set; }
    public BossTelegraph? Telegraph { get; set; } = script.Rotation.FirstOrDefault() ?? script.Telegraph;
    /// <summary>Index in <see cref="BossScript.Rotation"/> of the next filler slam.</summary>
    public int RotationIndex { get; set; }
    /// <summary>The filler telegraph to restore after a once-only phase slam.</summary>
    public BossTelegraph? Filler { get; set; } = script.Rotation.FirstOrDefault() ?? script.Telegraph;
    public long NextSlamMs { get; set; }
    /// <summary>The slam being wound up: its data, when it lands, its action sequence, the area's centre and facing at the wind-up.</summary>
    public (BossTelegraph Slam, long DueMs, int Seq, Vector3d Centre, double Yaw)? Winding { get; set; }
    public List<int> Adds { get; } = [];
    public int Slams { get; set; }
}

/// <summary>Monsters summoned during a fight (boss adds, scripted waves): new targets on the map, never respawned.</summary>
public static class BossSummons
{
    /// <summary>Keys of summoned monsters (clear of map NPCs 2000+, players 100000+, staff spawns 600000+, Kibelisks 900000+).</summary>
    public const int FirstKey = 700_000, LastKey = 799_999;
    public static bool IsSummon(int key) => key is >= FirstKey and <= LastKey;
    /// <summary>Appended to a summon's name so map-event spawners (a wave's "{wave}_{n}", KillSpawner) never count or respawn it.</summary>
    public const string NameSuffix = "_Summon";

    /// <summary>
    /// Copies of the NPC (its NpcData template) in a ring around a point; their targets, already in the map's table. The
    /// caller sends their appears and sets them on a victim.
    /// </summary>
    public static List<Target> Spawn(GameData data, Dictionary<int, Target> targets, int npcId, Vector3d centre, int count, double radius)
    {
        if (count <= 0) return [];
        var made = new List<Target>();
        var next = Math.Max(FirstKey, targets.Keys.Where(IsSummon).DefaultIfEmpty(FirstKey - 1).Max() + 1);
        for (var i = 0; i < count && next <= LastKey; i++, next++)
        {
            var angle = i * 2 * Math.PI / Math.Max(1, count);
            var at = centre with { X = centre.X + Math.Cos(angle) * radius, Y = centre.Y + Math.Sin(angle) * radius };
            if (data.NpcFromTemplate(npcId, "", [at.X, at.Y, at.Z], (angle * 180 / Math.PI + 180) % 360) is not { } spawn) return made;
            var name = (data.NpcPlaced(npcId)?.Name ?? $"Npc_{npcId}") + NameSuffix;
            var hp = Combat.HpFor(spawn);
            var t = new Target
            {
                Key = next, Name = name, Hp = hp, HpMax = hp, Position = at,
                NpcId = spawn.NpcId, Level = spawn.Level, Direction = spawn.Yaw, Relationship = spawn.Relationship is 6 or 11 ? spawn.Relationship : 6,
                Aggressive = true, WalkSpeed = spawn.WalkSpeed, RunSpeed = spawn.RunSpeed, Radius = spawn.Radius, Function = spawn.Function,
                Aggro = spawn.Aggro, Skills = spawn.Skills ?? [], NoRotation = spawn.NoRotation, Air = spawn.Air,
                AirMonster = spawn.Air && spawn.SubType.EndsWith("Monster", StringComparison.Ordinal),
                DamageScale = Combat.DamageScale(spawn.SubType, spawn.Grade), SubType = spawn.SubType, Grade = spawn.Grade,
                Defense = Combat.NpcDefense(spawn.Level, spawn.SubType, spawn.Grade, data.BaseStats("Gladiator", spawn.Level).GetValueOrDefault("Defense")),
            };
            targets[t.Key] = t;
            made.Add(t);
        }
        return made;
    }
}
