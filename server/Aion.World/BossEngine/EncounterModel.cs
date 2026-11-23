using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

// The boss mechanics engine's data model (docs/BOSS-MECHANICS-ENGINE.md). One encounter file per boss family under
// tools/data/boss-mechanics/*.json, schema tools/data/boss-mechanics.schema.json, validated by tools/boss_mechanics_validate.py.
// Every action is one flat object with a "type" discriminator; only the fields that type documents are read.

/// <summary>A number in an encounter file, or a "$name" reference to the encounter's (difficulty-merged) vars.</summary>
[JsonConverter(typeof(NumConverter))]
public readonly record struct Num(double Value, string? Var = null)
{
    public static implicit operator Num(double value) => new(value);

    public double Resolve(IReadOnlyDictionary<string, double> vars) =>
        Var is null ? Value : vars.TryGetValue(Var, out var v) ? v : Value;

    public override string ToString() => Var is null ? Value.ToString(CultureInfo.InvariantCulture) : "$" + Var;
}

public sealed class NumConverter : JsonConverter<Num>
{
    public override Num Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Number => new Num(reader.GetDouble()),
        JsonTokenType.String when reader.GetString() is { Length: > 1 } s && s[0] == '$' => new Num(0, s[1..]),
        _ => throw new JsonException("expected a number or a \"$var\" reference"),
    };

    public override void Write(Utf8JsonWriter writer, Num value, JsonSerializerOptions options)
    {
        if (value.Var is null) writer.WriteNumberValue(value.Value);
        else writer.WriteStringValue("$" + value.Var);
    }
}

/// <summary>A string or an array of strings ("phase": "p2" or "phase": ["p2", "p3"]).</summary>
[JsonConverter(typeof(StringListConverter))]
public sealed class StringList : List<string>
{
    public StringList() { }
    public StringList(IEnumerable<string> items) : base(items) { }
}

public sealed class StringListConverter : JsonConverter<StringList>
{
    public override StringList Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return [reader.GetString()!];
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("expected a string or an array of strings");
        var list = new StringList();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            list.Add(reader.GetString() ?? throw new JsonException("null in string list"));
        return list;
    }

    public override void Write(Utf8JsonWriter writer, StringList value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.ToArray(), options);
}

/// <summary>
/// Where an action happens: a keyword ("boss", "victim", "each_player", "mark:tempest", "group:clones", ...) or a world point
/// {"x":..,"y":..,"z":..}. Keywords are listed in <see cref="EncounterAnchors"/>.
/// </summary>
[JsonConverter(typeof(AnchorConverter))]
public sealed record AnchorDef(string Kind, double X = 0, double Y = 0, double Z = 0)
{
    public static AnchorDef Boss { get; } = new("boss");
    public string Base => Kind.Split(':', 2)[0];
    public string? Arg => Kind.Split(':', 2) is { Length: 2 } parts ? parts[1] : null;
    public override string ToString() => Kind == "point" ? $"({X},{Y},{Z})" : Kind;
}

public sealed class AnchorConverter : JsonConverter<AnchorDef>
{
    public override AnchorDef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return new AnchorDef(reader.GetString()!);
        using var doc = JsonDocument.ParseValue(ref reader);
        var o = doc.RootElement;
        double Get(string name) => o.TryGetProperty(name, out var v) ? v.GetDouble() : 0;
        return new AnchorDef("point", Get("x"), Get("y"), Get("z"));
    }

    public override void Write(Utf8JsonWriter writer, AnchorDef value, JsonSerializerOptions options)
    {
        if (value.Kind != "point") { writer.WriteStringValue(value.Kind); return; }
        writer.WriteStartObject();
        writer.WriteNumber("x", value.X);
        writer.WriteNumber("y", value.Y);
        writer.WriteNumber("z", value.Z);
        writer.WriteEndObject();
    }
}

/// <summary>Names the engine understands; the validator and the schema use the same lists (EncounterVocabularyTests).</summary>
public static class EncounterAnchors
{
    public static readonly string[] Keywords =
    [
        "boss", "victim", "each_player", "random_player", "farthest_player", "nearest_player", "arena_centre", "home",
        "cast_centre", "event", "mark", "group", "point",
    ];
}

/// <summary>How much an action hurts: a client skill's own damage row, a factor of the NPC's fixed damage, a flat range, or lethal.</summary>
public sealed class DamageDef
{
    /// <summary>Skill.dat damage (world-npcs.json npc_skills) of this skill, scaled by the boss's level, grade and fight multipliers.</summary>
    public int? Skill { get; init; }
    /// <summary>[min, max] in 1/10000 of <see cref="Combat.NpcFixingDamage"/> (the Skill.dat unit), used when <see cref="Skill"/> is unset.</summary>
    public Num[]? Factor { get; init; }
    /// <summary>[min, max] flat damage, not scaled by the fight's multipliers (DoTs, puddles).</summary>
    public Num[]? Fixed { get; init; }
    /// <summary>Share of the victim's maximum HP in 1/10000 (the client's Damage row calc type MaxHPRatio: 10000 = all of it). Used when the skill-effect engine's NPC ratio damage is on; otherwise the other fields apply.</summary>
    public Num? HpRatio { get; init; }
    public Num? Scale { get; init; }
    /// <summary>Kills outright (wipe checks, enrage).</summary>
    public bool Lethal { get; init; }
    /// <summary>Skip the victim's defense reduction.</summary>
    public bool IgnoreDefense { get; init; }
}

/// <summary>A ground shape. "skill_area" uses the client shape of the action's skill (skill-areas.json) instead.</summary>
public sealed class ShapeDef
{
    public string? Shape { get; init; }
    public Num? Radius { get; init; }
    public Num? Inner { get; init; }
    public Num? Angle { get; init; }
    public Num? Length { get; init; }
    public Num? Width { get; init; }
    /// <summary>Hit everyone outside the shape (safe zones, safe bands).</summary>
    public bool? Invert { get; init; }
}

/// <summary>The Kromede-style moving wall grid (tools/data/kromede-fire-walls.json values).</summary>
public sealed class GridDef
{
    public Num ArenaHalfExtent { get; init; } = 5000;
    public Num CellSize { get; init; } = 1400;
    public Num WallHalfThickness { get; init; } = 550;
    public int Rows { get; init; } = 7;
    public int Cols { get; init; } = 7;
    public Num StepIntervalMs { get; init; } = 2500;
    public int[][] GapPatterns { get; init; } = [[3]];
    public int PatternOffset { get; init; }
}

/// <summary>A breakable part (client MonsterPartsData, or authored).</summary>
public sealed class PartDef
{
    public required string Id { get; init; }
    /// <summary>HP of the part, taken from the boss's HP loss while the part is exposed. Unset with from_client: MonsterPartsData max_hp.</summary>
    public Num? Hp { get; init; }
    public int? ClientPartId { get; init; }
    public List<ActionDef>? OnBreak { get; init; }
}

public sealed class ChoiceDef
{
    public Num Weight { get; init; } = 1;
    public List<ActionDef> Do { get; init; } = [];
}

/// <summary>What makes a rule fire. <see cref="Type"/> is one of <see cref="EncounterVocabulary.Triggers"/>.</summary>
public sealed class TriggerDef
{
    public required string Type { get; init; }
    public Num? Pct { get; init; }
    public Num? Step { get; init; }
    public Num? AfterMs { get; init; }
    public Num? EveryMs { get; init; }
    /// <summary>"fight" (default) or "phase": what timers count from.</summary>
    public string? From { get; init; }
    public int? Count { get; init; }
    public string? Phase { get; init; }
    public string? Group { get; init; }
    public string? Shield { get; init; }
    public string? Debuff { get; init; }
    public int? Stacks { get; init; }
    public int? Npc { get; init; }
    public string? Counter { get; init; }
    public Num? Value { get; init; }
    public string? Part { get; init; }
    public string? Event { get; init; }
    public AnchorDef? At { get; init; }
    public ShapeDef? Area { get; init; }
    public int? Min { get; init; }
    public int? Max { get; init; }
    public Num? HoldMs { get; init; }
    public string? Note { get; init; }
}

/// <summary>A rule: when its trigger fires (and its filters pass), run its actions.</summary>
public sealed class RuleDef
{
    public required string Id { get; init; }
    public required TriggerDef When { get; init; }
    /// <summary>Only while the encounter is in one of these phases.</summary>
    public StringList? Phase { get; init; }
    /// <summary>Only on these difficulties (Easy/Exploration, Normal/Conquest, Hard).</summary>
    public StringList? Difficulty { get; init; }
    /// <summary>Fire at most once per fight (default: true for hp_below, false otherwise).</summary>
    public bool? Once { get; init; }
    public int? MaxFires { get; init; }
    public Num? Chance { get; init; }
    public List<ActionDef> Do { get; init; } = [];
    public string? Note { get; init; }
}

/// <summary>
/// One primitive. <see cref="Type"/> is one of <see cref="EncounterVocabulary.Actions"/>; the fields each type reads are listed
/// there and in docs/BOSS-MECHANICS-ENGINE.md.
/// </summary>
public sealed class ActionDef
{
    public required string Type { get; init; }
    /// <summary>Names the action in logs and its outcome events ("&lt;id&gt;.success" / "&lt;id&gt;.fail"); shields, covers, zones and orbs are referenced by it.</summary>
    public string? Id { get; init; }
    public string? Note { get; init; }

    // cast / telegraph / zone / damage sources
    public int? Skill { get; init; }
    public Num? WindUpMs { get; init; }
    public Num? EveryMs { get; init; }
    public Num? Radius { get; init; }
    public DamageDef? Damage { get; init; }
    public string? Warning { get; init; }
    public string? WarningRu { get; init; }
    public bool? Land { get; init; }
    public bool? StaggerCheck { get; init; }
    public bool? Once { get; init; }
    /// <summary>cast: players behind an active cover object (the "cover" primitive) are not hit.</summary>
    public bool? Cover { get; init; }
    public List<ActionDef>? During { get; init; }
    public List<ActionDef>? OnLand { get; init; }
    public List<ActionDef>? OnBreak { get; init; }

    // shapes and placement
    public string? Shape { get; init; }
    public Num? Inner { get; init; }
    public Num? Angle { get; init; }
    public Num? Length { get; init; }
    public Num? Width { get; init; }
    public bool? Invert { get; init; }
    public List<ShapeDef>? Steps { get; init; }
    public GridDef? Grid { get; init; }
    public AnchorDef? At { get; init; }
    /// <summary>Move the anchor this far toward <see cref="Toward"/> (a pillar between the boss and its victim).</summary>
    public AnchorDef? Toward { get; init; }
    public Num? Distance { get; init; }
    public List<Num[]>? Offsets { get; init; }
    /// <summary>"world" (default) or "facing": offsets as [forward, right] from the boss's facing.</summary>
    public string? OffsetSpace { get; init; }
    public Num? DelayMs { get; init; }
    public bool? Decal { get; init; }

    // zones and repeats
    public Num? DurationMs { get; init; }
    /// <summary>"cast_land": the zone or repeat ends when the cast it belongs to lands (plus <see cref="LingerMs"/>).</summary>
    public string? Ends { get; init; }
    public Num? LingerMs { get; init; }
    public Num? TickMs { get; init; }
    public Num? FirstTickMs { get; init; }
    public bool? BlocksMovement { get; init; }
    public int? Count { get; init; }
    public Num? IntervalMs { get; init; }

    // soak / stack / spread
    public int? MinPlayers { get; init; }
    public bool? Split { get; init; }
    public Num? OverlapScale { get; init; }
    public List<ActionDef>? OnHit { get; init; }
    public List<ActionDef>? OnSuccess { get; init; }
    public List<ActionDef>? OnFail { get; init; }

    // displacement
    public string? Mode { get; init; }

    // summons and waves
    public int? Npc { get; init; }
    public string? Group { get; init; }
    public string? Wave { get; init; }

    // shields, stagger, parts
    public bool? Invuln { get; init; }
    public Num? Hp { get; init; }
    public Num? HpPct { get; init; }
    public string? Until { get; init; }
    public List<ActionDef>? OnExpire { get; init; }
    public Num? WindowMs { get; init; }
    public List<PartDef>? Parts { get; init; }
    public bool? FromClient { get; init; }

    // debuffs and tank swap
    public string? Debuff { get; init; }
    public int? Stacks { get; init; }
    public int? Max { get; init; }
    public int? Abnormal { get; init; }
    public List<ActionDef>? OnMax { get; init; }

    // orbs and env objects
    public int? EnvObj { get; init; }
    public int? Required { get; init; }
    public string? CountMode { get; init; }
    public List<ActionDef>? OnComplete { get; init; }
    public List<ActionDef>? OnTouch { get; init; }
    public string? Op { get; init; }
    public int? Slot { get; init; }

    // movement
    public AnchorDef? To { get; init; }

    // enrage / modify
    public Num? DamageMul { get; init; }
    public Num? HasteMul { get; init; }
    public bool? Hard { get; init; }

    // notices and captions
    public string? Text { get; init; }
    public string? TextRu { get; init; }
    public int? Size { get; init; }
    public string? Color { get; init; }
    public string? Talk { get; init; }

    // flow
    public string? Phase { get; init; }
    public string? Name { get; init; }
    public AnchorDef? Target { get; init; }
    public string? Counter { get; init; }
    public Num? Add { get; init; }
    public Num? Set { get; init; }
    public Num? Ms { get; init; }
    public List<ActionDef>? Do { get; init; }
    public List<ChoiceDef>? Choices { get; init; }
    public string? Event { get; init; }
    public List<ActionDef>? Rotation { get; init; }

    /// <summary>A compiled boss-phases.json slam, reused as it is so compiled scripts behave exactly as before.</summary>
    [JsonIgnore] internal BossTelegraph? Compiled { get; init; }
}

/// <summary>Per-difficulty overrides: numbers (vars, scales, skill remaps), extra or removed rules, another rotation.</summary>
public sealed class DifficultyDef
{
    public Dictionary<string, double>? Vars { get; init; }
    public double? DamageScale { get; init; }
    /// <summary>Multiplies every delay, duration, interval and timer (0.8: everything 20% faster).</summary>
    public double? TimeScale { get; init; }
    public Dictionary<string, int>? SkillRemap { get; init; }
    public List<ActionDef>? Rotation { get; init; }
    public List<RuleDef>? RulesAdd { get; init; }
    public StringList? RulesRemove { get; init; }
    public string? Note { get; init; }
}

public sealed class ArenaDef
{
    /// <summary>"spawn" (the boss's home, default), "boss" (where the fight starts) or a point.</summary>
    public AnchorDef? Centre { get; init; }
    public Num? Radius { get; init; }
}

/// <summary>One encounter file.</summary>
public sealed class EncounterDef
{
    [JsonPropertyName("$schema")] public string? Schema { get; init; }
    public required string Id { get; init; }
    public string? Display { get; init; }
    public string? DisplayRu { get; init; }
    public string? Dungeon { get; init; }
    public string? Source { get; init; }
    public string? Note { get; init; }
    /// <summary>NpcData ids per difficulty: {"Easy": [...], "Normal": [...], "Hard": [...]}.</summary>
    public Dictionary<string, int[]> Npcs { get; init; } = [];
    public ArenaDef? Arena { get; init; }
    public Dictionary<string, double>? Vars { get; init; }
    public string? StartPhase { get; init; }
    /// <summary>First rotation cast this long after the pull (default: half the first rotation cast's every_ms).</summary>
    public Num? OpeningMs { get; init; }
    public List<ActionDef> Rotation { get; init; } = [];
    public List<RuleDef> Rules { get; init; } = [];
    public Dictionary<string, DifficultyDef>? Difficulty { get; init; }
}

/// <summary>The vocabulary: trigger and action types, difficulty names. The schema and validator mirror these.</summary>
public static class EncounterVocabulary
{
    public static readonly string[] Triggers =
    [
        "fight_start", "hp_below", "hp_every", "timer", "enrage_timer", "phase_enter", "phase_exit", "adds_dead", "add_died",
        "players_in_area", "shield_broken", "stacks_reached", "npc_killed", "counter_reached", "stagger_broken", "part_broken", "event",
    ];

    public static readonly string[] Actions =
    [
        "cast", "telegraph", "zone", "soak", "stack", "spread", "cover", "displace", "summon", "despawn_group", "shield",
        "stagger_check", "parts", "debuff", "cleanse", "tank_swap", "orb", "move", "enrage", "wipe", "phase", "notice", "caption",
        "env", "decal", "modify", "set_rotation", "counter", "delay", "repeat", "random", "mark", "emit",
    ];

    public static readonly string[] Shapes = ["circle", "donut", "cone", "rect", "line", "grid", "safe_zone", "skill_area"];

    public static readonly string[] Difficulties = ["Easy", "Normal", "Hard"];

    /// <summary>Official mode names to the engine's: Exploration is Easy, Conquest (Normal) is Normal, Conquest Hard is Hard.</summary>
    public static string? Difficulty(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "easy" or "exploration" => "Easy",
        "normal" or "conquest" or "conquest_normal" => "Normal",
        "hard" or "conquest_hard" or "advanced" => "Hard",
        _ => null,
    };

    /// <summary>A dungeon name's difficulty ("Fire_Temple_Hard" is Hard); null when it has no mode suffix.</summary>
    public static string? DifficultyOfDungeon(string? dungeon)
    {
        if (string.IsNullOrEmpty(dungeon)) return null;
        var last = dungeon.Split('_')[^1];
        return Difficulty(last);
    }
}
