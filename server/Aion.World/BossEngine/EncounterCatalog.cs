using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>An encounter for one boss NpcData id with its difficulty applied: the rules, rotation and numbers the engine runs.</summary>
public sealed class ResolvedEncounter
{
    public required EncounterDef Def { get; init; }
    public required string Difficulty { get; init; }
    public required string Dungeon { get; init; }
    public required IReadOnlyDictionary<string, double> Vars { get; init; }
    public double DamageScale { get; init; } = 1.0;
    public double TimeScale { get; init; } = 1.0;
    public IReadOnlyDictionary<int, int> SkillRemap { get; init; } = new Dictionary<int, int>();
    public required IReadOnlyList<ActionDef> Rotation { get; init; }
    public required IReadOnlyList<RuleDef> Rules { get; init; }
    /// <summary>Built at load from a boss-phases.json script (generic OURS phases) rather than authored.</summary>
    public bool Compiled { get; init; }
    /// <summary>The boss-phases.json script a compiled encounter came from (its slams are reused as they are).</summary>
    public BossScript? Script { get; init; }

    public string Id => Def.Id;
    public int Skill(int id) => SkillRemap.TryGetValue(id, out var to) ? to : id;
    public double N(Num? n, double fallback = 0) => n is { } v ? v.Resolve(Vars) : fallback;
    /// <summary>A time in ms with the difficulty's time scale.</summary>
    public long Ms(Num? n, double fallback = 0) => (long)Math.Round(N(n, fallback) * TimeScale);
}

/// <summary>A problem found while loading an encounter file (logged at startup and under PlaytestDebug "boss_mechanic_load").</summary>
public sealed record EncounterLoadError(string File, string Message)
{
    public override string ToString() => $"{File}: {Message}";
}

/// <summary>
/// The authored encounters (tools/data/boss-mechanics/*.json, embedded as "boss-mechanics/&lt;file&gt;"), indexed by NpcData id. A boss
/// without one runs its boss-phases.json script compiled into the same rules (<see cref="Compile"/>). A file with errors is skipped
/// and its errors kept in <see cref="Errors"/>; the rest still load.
/// </summary>
public sealed class BossEncounters
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    readonly Dictionary<int, (EncounterDef Def, string Difficulty)> byNpc = [];
    readonly List<EncounterDef> encounters = [];
    readonly List<EncounterLoadError> errors = [];

    public IReadOnlyList<EncounterDef> All => encounters;
    public IReadOnlyList<EncounterLoadError> Errors => errors;
    public int Count => encounters.Count;
    public IEnumerable<int> NpcIds => byNpc.Keys;

    public static BossEncounters None { get; } = new();

    static readonly Lazy<BossEncounters> embedded = new(LoadEmbedded);
    public static BossEncounters Embedded => embedded.Value;

    static BossEncounters LoadEmbedded()
    {
        var asm = typeof(BossEncounters).Assembly;
        var files = asm.GetManifestResourceNames().Where(n => n.StartsWith("boss-mechanics/", StringComparison.Ordinal)).Order(StringComparer.Ordinal);
        var catalog = new BossEncounters();
        foreach (var name in files)
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            catalog.Add(name["boss-mechanics/".Length..], reader.ReadToEnd());
        }
        foreach (var error in catalog.errors)
            PlaytestDebug.Action("boss_mechanic_load", ("file", error.File), ("error", error.Message));
        return catalog;
    }

    /// <summary>Encounters from JSON texts (tests, tools): file name to content.</summary>
    public static BossEncounters Parse(params (string File, string Json)[] files)
    {
        var catalog = new BossEncounters();
        foreach (var (file, json) in files) catalog.Add(file, json);
        return catalog;
    }

    /// <summary>Parse one encounter file; false (and <see cref="Errors"/> filled) when it is invalid.</summary>
    public bool Add(string file, string json)
    {
        EncounterDef? def;
        try { def = JsonSerializer.Deserialize<EncounterDef>(json, Json); }
        catch (JsonException e) { errors.Add(new(file, $"JSON: {e.Message}")); return false; }
        if (def is null) { errors.Add(new(file, "empty file")); return false; }
        var problems = EncounterChecks.Check(def);
        foreach (var (difficulty, ids) in def.Npcs)
            foreach (var id in ids)
                if (byNpc.TryGetValue(id, out var other) && other.Def.Id != def.Id)
                    problems.Add($"npc {id} already belongs to encounter '{other.Def.Id}'");
        if (encounters.Any(e => e.Id == def.Id)) problems.Add($"duplicate encounter id '{def.Id}'");
        if (problems.Count > 0)
        {
            errors.AddRange(problems.Select(p => new EncounterLoadError(file, p)));
            return false;
        }
        encounters.Add(def);
        foreach (var (difficulty, ids) in def.Npcs)
            foreach (var id in ids) byNpc[id] = (def, EncounterVocabulary.Difficulty(difficulty)!);
        return true;
    }

    public bool Authored(int npcId) => byNpc.ContainsKey(npcId);

    /// <summary>
    /// The encounter the engine runs for this boss: its authored file, else its boss-phases.json script compiled into rules, else
    /// null. A script still naming hand-coded mechanics (an "official-overlay" with no authored file) is left to the legacy path.
    /// </summary>
    public ResolvedEncounter? Resolve(int npcId, BossPhases scripts)
    {
        if (byNpc.TryGetValue(npcId, out var hit))
        {
            var script = scripts.Of(npcId);
            return Apply(hit.Def, hit.Difficulty, script?.Dungeon ?? hit.Def.Dungeon ?? hit.Def.Id);
        }
        return scripts.Of(npcId) is { } generic ? Compile(npcId, generic) : null;
    }

    /// <summary>Applies a difficulty: merged vars, scales, skill remaps, the rotation, rules filtered, removed and added.</summary>
    public static ResolvedEncounter Apply(EncounterDef def, string difficulty, string dungeon)
    {
        var mode = def.Difficulty?.FirstOrDefault(p => EncounterVocabulary.Difficulty(p.Key) == difficulty).Value;
        var vars = new Dictionary<string, double>(def.Vars ?? [], StringComparer.Ordinal);
        foreach (var (k, v) in mode?.Vars ?? []) vars[k] = v;
        var remove = mode?.RulesRemove?.ToHashSet(StringComparer.Ordinal) ?? [];
        var rules = def.Rules.Where(r => !remove.Contains(r.Id)).Concat(mode?.RulesAdd ?? [])
            .Where(r => r.Difficulty is null || r.Difficulty.Any(d => EncounterVocabulary.Difficulty(d) == difficulty)).ToList();
        var remap = (mode?.SkillRemap ?? []).ToDictionary(p => int.Parse(p.Key, CultureInfo.InvariantCulture), p => p.Value);
        return new ResolvedEncounter
        {
            Def = def, Difficulty = difficulty, Dungeon = dungeon, Vars = vars, DamageScale = mode?.DamageScale ?? 1.0,
            TimeScale = mode?.TimeScale ?? 1.0, SkillRemap = remap, Rotation = mode?.Rotation ?? def.Rotation, Rules = rules,
        };
    }

    /// <summary>
    /// A boss-phases.json script as engine rules, with the legacy behaviour: the slam rotation; each phase once at its HP percent
    /// (multipliers, its slam, its notice, its adds); the frenzy on the encounter's enrage timer. Null when the script names a
    /// hand-coded mechanic (those need an authored encounter, else they stay on the legacy path).
    /// </summary>
    public static ResolvedEncounter? Compile(int npcId, BossScript script)
    {
        var slams = script.Rotation.Concat(script.Ordered.Select(p => p.Telegraph).OfType<BossTelegraph>());
        if (slams.Any(s => s.Mechanic is not null)) return null;
        var rules = new List<RuleDef>();
        var index = 0;
        foreach (var phase in script.Ordered)
        {
            var actions = new List<ActionDef>
            {
                new() { Type = "modify", Mode = "phase", DamageMul = phase.Damage, HasteMul = phase.Haste },
            };
            if (phase.Telegraph is { } slam) actions.Add(CastOf(slam));
            if (phase.Notice is { Length: > 0 } notice) actions.Add(new() { Type = "notice", Text = notice, TextRu = phase.NoticeRu, Size = 22, Color = "FFB040" });
            if (phase.Adds is { } adds) actions.Add(new() { Type = "summon", Npc = adds.Npc, Count = adds.Count, Radius = adds.Radius, Target = new AnchorDef("victim") });
            rules.Add(new RuleDef { Id = $"phase{index++}_{phase.Name}", When = new TriggerDef { Type = "hp_below", Pct = phase.At }, Once = true, Do = actions });
        }
        if (script.Frenzy is { } frenzy)
            rules.Add(new RuleDef
            {
                Id = "frenzy", When = new TriggerDef { Type = "enrage_timer", AfterMs = frenzy.AfterMs }, Once = true,
                Do = [new() { Type = "enrage", DamageMul = frenzy.Damage, HasteMul = frenzy.Haste, Text = frenzy.Notice, TextRu = frenzy.NoticeRu }],
            });
        var def = new EncounterDef
        {
            Id = $"compiled:{npcId}", Display = script.Display ?? script.Name, Dungeon = script.Dungeon, Source = script.Source ?? "OURS (boss-phases.json)",
            Npcs = new() { [EncounterVocabulary.DifficultyOfDungeon(script.Dungeon) ?? "Normal"] = [npcId] },
            Rotation = script.Rotation.Select(CastOf).ToList(), Rules = rules,
        };
        return new ResolvedEncounter
        {
            Def = def, Difficulty = EncounterVocabulary.DifficultyOfDungeon(script.Dungeon) ?? "Normal", Dungeon = script.Dungeon,
            Vars = new Dictionary<string, double>(), Rotation = def.Rotation, Rules = rules, Compiled = true, Script = script,
        };
    }

    static ActionDef CastOf(BossTelegraph slam) => new()
    {
        Type = "cast", Skill = slam.Skill, EveryMs = slam.EveryMs, WindUpMs = slam.WindUpMs, Radius = slam.Radius,
        Damage = new DamageDef { Scale = slam.Damage }, Warning = slam.Warning, WarningRu = slam.WarningRu, Once = slam.Once,
        StaggerCheck = slam.StaggerCheck, Land = slam.Land, Compiled = slam,
    };
}

/// <summary>Structural checks the loader runs (the validator script adds client-data id checks).</summary>
public static class EncounterChecks
{
    static readonly HashSet<string> Triggers = [.. EncounterVocabulary.Triggers];
    static readonly HashSet<string> Actions = [.. EncounterVocabulary.Actions];
    static readonly HashSet<string> Shapes = [.. EncounterVocabulary.Shapes];
    static readonly HashSet<string> Anchors = [.. EncounterAnchors.Keywords];

    public static List<string> Check(EncounterDef def)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(def.Id)) problems.Add("missing id");
        if (def.Npcs.Count == 0) problems.Add("npcs lists no NpcData ids");
        foreach (var key in def.Npcs.Keys.Concat((IEnumerable<string>?)def.Difficulty?.Keys ?? []))
            if (EncounterVocabulary.Difficulty(key) is null) problems.Add($"unknown difficulty '{key}' (Easy/Exploration, Normal/Conquest, Hard)");
        var vars = new HashSet<string>((IEnumerable<string>?)def.Vars?.Keys ?? [], StringComparer.Ordinal);
        foreach (var mode in (IEnumerable<DifficultyDef>?)def.Difficulty?.Values ?? []) vars.UnionWith((IEnumerable<string>?)mode.Vars?.Keys ?? []);
        var ruleIds = new HashSet<string>(StringComparer.Ordinal);
        void Rules(IEnumerable<RuleDef> rules, string where)
        {
            foreach (var rule in rules)
            {
                if (!ruleIds.Add(rule.Id) && where == "rules") problems.Add($"duplicate rule id '{rule.Id}'");
                CheckTrigger(rule.When, $"{where}/{rule.Id}", problems, vars);
                foreach (var d in rule.Difficulty ?? []) if (EncounterVocabulary.Difficulty(d) is null) problems.Add($"{where}/{rule.Id}: unknown difficulty '{d}'");
                CheckActions(rule.Do, $"{where}/{rule.Id}", problems, vars);
            }
        }
        Rules(def.Rules, "rules");
        CheckActions(def.Rotation, "rotation", problems, vars);
        foreach (var cast in def.Rotation.Where(a => a.Type != "cast")) problems.Add($"rotation: only cast actions may rotate (got '{cast.Type}')");
        foreach (var (name, mode) in def.Difficulty ?? [])
        {
            Rules(mode.RulesAdd ?? [], $"difficulty/{name}/rules_add");
            CheckActions(mode.Rotation, $"difficulty/{name}/rotation", problems, vars);
            foreach (var id in mode.RulesRemove ?? []) if (!def.Rules.Any(r => r.Id == id)) problems.Add($"difficulty/{name}: rules_remove names unknown rule '{id}'");
            foreach (var key in (IEnumerable<string>?)mode.SkillRemap?.Keys ?? [])
                if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) problems.Add($"difficulty/{name}: skill_remap key '{key}' is not a skill id");
        }
        return problems;
    }

    static void CheckNum(Num? n, string where, string field, List<string> problems, HashSet<string> vars)
    {
        if (n is { Var: { } v } && !vars.Contains(v)) problems.Add($"{where}: {field} references unknown var '${v}'");
    }

    static void CheckAnchor(AnchorDef? a, string where, List<string> problems)
    {
        if (a is null) return;
        if (!Anchors.Contains(a.Base)) problems.Add($"{where}: unknown anchor '{a.Kind}'");
        if (a.Base is "mark" or "group" && string.IsNullOrEmpty(a.Arg)) problems.Add($"{where}: anchor '{a.Kind}' needs a name ('{a.Base}:<name>')");
    }

    static void CheckTrigger(TriggerDef t, string where, List<string> problems, HashSet<string> vars)
    {
        if (!Triggers.Contains(t.Type)) { problems.Add($"{where}: unknown trigger '{t.Type}'"); return; }
        void Need(bool ok, string field) { if (!ok) problems.Add($"{where}: trigger {t.Type} needs '{field}'"); }
        switch (t.Type)
        {
            case "hp_below": Need(t.Pct is not null, "pct"); break;
            case "hp_every": Need(t.Step is not null, "step"); break;
            case "timer": Need(t.AfterMs is not null || t.EveryMs is not null, "after_ms or every_ms"); break;
            case "enrage_timer": Need(t.AfterMs is not null, "after_ms"); break;
            case "phase_enter" or "phase_exit": Need(t.Phase is not null, "phase"); break;
            case "adds_dead" or "add_died": Need(t.Group is not null, "group"); break;
            case "players_in_area": Need(t.Area is not null, "area"); Need(t.Min is not null || t.Max is not null, "min or max"); break;
            case "shield_broken": Need(t.Shield is not null, "shield"); break;
            case "stacks_reached": Need(t.Debuff is not null && t.Stacks is not null, "debuff and stacks"); break;
            case "npc_killed": Need(t.Npc is not null, "npc"); break;
            case "counter_reached": Need(t.Counter is not null && t.Value is not null, "counter and value"); break;
            case "part_broken": Need(t.Part is not null, "part"); break;
            case "event": Need(t.Event is not null, "event"); break;
        }
        if (t.From is not (null or "fight" or "phase")) problems.Add($"{where}: timer 'from' must be fight or phase");
        CheckAnchor(t.At, where, problems);
        if (t.Area is { } area) CheckShape(area.Shape, where, problems);
        foreach (var (n, f) in new[] { (t.Pct, "pct"), (t.Step, "step"), (t.AfterMs, "after_ms"), (t.EveryMs, "every_ms"), (t.Value, "value"), (t.HoldMs, "hold_ms") })
            CheckNum(n, where, f, problems, vars);
    }

    static void CheckShape(string? shape, string where, List<string> problems)
    {
        if (shape is not null && !Shapes.Contains(shape)) problems.Add($"{where}: unknown shape '{shape}'");
    }

    /// <summary>Fields each action type cannot do without.</summary>
    static void CheckActions(IEnumerable<ActionDef>? actions, string where, List<string> problems, HashSet<string> vars)
    {
        foreach (var (a, i) in (actions ?? []).Select((a, i) => (a, i)))
        {
            var at = $"{where}[{i}:{a.Id ?? a.Type}]";
            if (!Actions.Contains(a.Type)) { problems.Add($"{at}: unknown action '{a.Type}'"); continue; }
            void Need(bool ok, string field) { if (!ok) problems.Add($"{at}: {a.Type} needs '{field}'"); }
            switch (a.Type)
            {
                case "cast": Need(a.Skill is not null, "skill"); break;
                case "telegraph": Need(a.Shape is not null || a.Steps is not null || a.Skill is not null, "shape, steps or skill"); break;
                case "zone": Need(a.Shape is not null || a.Grid is not null, "shape or grid"); break;
                case "soak" or "stack" or "spread": Need(a.Radius is not null, "radius"); break;
                case "cover": Need(a.Id is not null, "id"); break;
                case "displace": Need(a.Mode is "knockback" or "pull" or "to_radius" or "to_point", "mode (knockback, pull, to_radius, to_point)"); break;
                case "summon": Need(a.Npc is not null || a.Wave is not null, "npc or wave"); break;
                case "despawn_group": Need(a.Group is not null, "group"); break;
                case "shield": Need(a.Id is not null, "id"); Need(a.Invuln == true || a.Hp is not null || a.HpPct is not null || a.Until is not null, "invuln, hp, hp_pct or until"); break;
                case "stagger_check": Need(a.WindowMs is not null, "window_ms"); break;
                case "parts": Need(a.Parts is { Count: > 0 } || a.FromClient == true, "parts or from_client"); break;
                case "debuff" or "cleanse": Need(a.Debuff is not null, "debuff"); break;
                case "tank_swap": Need(a.Debuff is not null, "debuff"); break;
                case "orb": Need(a.Radius is not null && a.Required is not null, "radius and required"); break;
                case "move": Need(a.To is not null, "to"); break;
                case "phase": Need(a.Phase is not null, "phase"); break;
                case "notice": Need(a.Text is not null, "text"); break;
                case "caption": Need(a.Talk is not null, "talk"); break;
                case "env": Need(a.EnvObj is not null, "env_obj"); Need(a.Op is null or "spawn" or "despawn", "op (spawn or despawn)"); break;
                case "decal": Need(a.Skill is not null, "skill"); break;
                case "set_rotation": Need(a.Rotation is { Count: > 0 }, "rotation"); break;
                case "counter": Need(a.Counter is not null && (a.Add is not null || a.Set is not null), "counter and add or set"); break;
                case "delay": Need(a.Ms is not null && a.Do is not null, "ms and do"); break;
                case "repeat": Need(a.EveryMs is not null && a.Do is not null, "every_ms and do"); Need(a.Count is not null || a.DurationMs is not null || a.Ends is not null, "count, duration_ms or ends"); break;
                case "random": Need(a.Choices is { Count: > 0 }, "choices"); break;
                case "mark": Need(a.Name is not null, "name"); break;
                case "emit": Need(a.Event is not null, "event"); break;
            }
            CheckShape(a.Shape, at, problems);
            foreach (var s in a.Steps ?? []) CheckShape(s.Shape, at, problems);
            if (a.Ends is not (null or "cast_land")) problems.Add($"{at}: ends must be 'cast_land'");
            if (a.OffsetSpace is not (null or "world" or "facing")) problems.Add($"{at}: offset_space must be world or facing");
            if (a.CountMode is not (null or "consume" or "per_player" or "per_tick")) problems.Add($"{at}: count_mode must be consume, per_player or per_tick");
            if (a.Mode is not null && a.Type == "modify" && a.Mode is not ("phase" or "mul" or "set")) problems.Add($"{at}: modify mode must be phase, mul or set");
            foreach (var anchor in new[] { a.At, a.Toward, a.To, a.Target }) CheckAnchor(anchor, at, problems);
            if (a.Damage is { } d)
            {
                if (d.Factor is { Length: not 2 } || d.Fixed is { Length: not 2 }) problems.Add($"{at}: damage factor/fixed must be [min, max]");
                foreach (var n in (d.Factor ?? []).Concat(d.Fixed ?? [])) CheckNum(n, at, "damage", problems, vars);
                CheckNum(d.Scale, at, "damage.scale", problems, vars);
            }
            foreach (var (n, f) in new[]
            {
                (a.WindUpMs, "wind_up_ms"), (a.EveryMs, "every_ms"), (a.Radius, "radius"), (a.Inner, "inner"), (a.Angle, "angle"),
                (a.Length, "length"), (a.Width, "width"), (a.Distance, "distance"), (a.DelayMs, "delay_ms"), (a.DurationMs, "duration_ms"),
                (a.LingerMs, "linger_ms"), (a.TickMs, "tick_ms"), (a.FirstTickMs, "first_tick_ms"), (a.IntervalMs, "interval_ms"),
                (a.OverlapScale, "overlap_scale"), (a.Hp, "hp"), (a.HpPct, "hp_pct"), (a.WindowMs, "window_ms"), (a.DamageMul, "damage_mul"),
                (a.HasteMul, "haste_mul"), (a.Add, "add"), (a.Set, "set"), (a.Ms, "ms"),
            })
                CheckNum(n, at, f, problems, vars);
            foreach (var offset in a.Offsets ?? [])
            {
                if (offset.Length is not 2) problems.Add($"{at}: each offset is [x, y]");
                foreach (var n in offset) CheckNum(n, at, "offsets", problems, vars);
            }
            foreach (var (list, name) in new[]
            {
                (a.During, "during"), (a.OnLand, "on_land"), (a.OnBreak, "on_break"), (a.OnHit, "on_hit"), (a.OnSuccess, "on_success"),
                (a.OnFail, "on_fail"), (a.OnExpire, "on_expire"), (a.OnMax, "on_max"), (a.OnComplete, "on_complete"), (a.OnTouch, "on_touch"),
                (a.Do, "do"), (a.Rotation, "rotation"),
            })
                CheckActions(list, $"{at}/{name}", problems, vars);
            foreach (var choice in a.Choices ?? []) CheckActions(choice.Do, $"{at}/choices", problems, vars);
            foreach (var part in a.Parts ?? []) CheckActions(part.OnBreak, $"{at}/parts/{part.Id}", problems, vars);
        }
    }
}
