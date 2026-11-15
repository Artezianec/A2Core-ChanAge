using System.Globalization;
using System.Text.Json;

namespace Aion.World;

// Ported from zaraza87/A2Core dell-merge-2026-11 (29934bea, docs/DELL-CHANGES-2026-11.md): the Arcana part of their Systems3Tables
// (ArcanaCreate, the Arcana items, ArcanaEnchant, ArcanaMainEffect, ArcanaStatFilter), read from tools/data/arcana.json. Used by
// GameSession.Arcana.cs (on by default, GameSessionOptions.ExperimentalRandomShopArcana).
/// <summary>The Arcana tables from tools/data/arcana.json (client tables).</summary>
public sealed class ArcanaTables
{
    public sealed record ArcanaCreate(int Id, string Category, string Grade, int Item, int MainMax, int SubMax, int Material, int MaterialCount,
        int MaterialPerMain, int MaterialPerSub, long Kina, long KinaPerMain, long KinaPerSub);
    public sealed record ArcanaItem(int Id, string EnchantGroup, string EffectGroup, IReadOnlyList<(string Stat, int Value)> MainStats, int SubStats, string Grade, string Category);
    public sealed record EnchantStep(int NeedExp, int GetExp, long Kina);

    readonly Dictionary<int, ArcanaCreate> creates = [];
    readonly Dictionary<int, ArcanaItem> arcana = [];
    readonly Dictionary<string, List<EnchantStep>> enchant = [];
    readonly Dictionary<string, Dictionary<int, List<(string Stat, int Value)>>> mainEffect = [];
    readonly List<(string Category, string Class, int Kind, string Stat, int Id)> filters = [];
    readonly Dictionary<int, string> materials = [];
    public int EnchantMaterial { get; }
    public int CreateCooltimeMinutes { get; }

    static int I(JsonElement e) => e.GetInt32();
    static int Key(string name) => int.Parse(name, CultureInfo.InvariantCulture);

    ArcanaTables(JsonElement a)
    {
        foreach (var p in a.GetProperty("create").EnumerateObject())
        {
            var v = p.Value.EnumerateArray().ToArray();
            creates[Key(p.Name)] = new(Key(p.Name), v[0].GetString()!, v[1].GetString()!, I(v[2]), I(v[3]), I(v[4]), I(v[5]), I(v[6]), I(v[7]), I(v[8]),
                v[9].GetInt64(), v[10].GetInt64(), v[11].GetInt64());
        }
        foreach (var p in a.GetProperty("items").EnumerateObject())
        {
            var v = p.Value.EnumerateArray().ToArray();
            arcana[Key(p.Name)] = new(Key(p.Name), v[0].GetString()!, v[1].GetString()!, v[2].EnumerateArray().Select(s => (s[0].GetString()!, I(s[1]))).ToList(),
                I(v[3]), v[4].GetString()!, v[5].GetString()!);
        }
        foreach (var p in a.GetProperty("enchant").EnumerateObject())
            enchant[p.Name] = p.Value.EnumerateArray().Select(s => new EnchantStep(I(s[0]), I(s[1]), s[2].GetInt64())).ToList();
        foreach (var p in a.GetProperty("main_effect").EnumerateObject())
            mainEffect[p.Name] = p.Value.EnumerateObject().ToDictionary(l => Key(l.Name), l => l.Value.EnumerateArray().Select(s => (s[0].GetString()!, I(s[1]))).ToList());
        foreach (var f in a.GetProperty("stat_filter").EnumerateArray())
            filters.Add((f[0].GetString()!, f[1].GetString()!, I(f[2]), f[3].GetString()!, I(f[4])));
        if (a.TryGetProperty("materials", out var mats))
            foreach (var p in mats.EnumerateObject()) materials[Key(p.Name)] = p.Value.GetString()!;
        EnchantMaterial = a.GetProperty("material").GetInt32();
        CreateCooltimeMinutes = a.GetProperty("create_cooltime_min").GetInt32();
    }

    static readonly Lazy<ArcanaTables> embedded = new(() =>
    {
        using var stream = typeof(ArcanaTables).Assembly.GetManifestResourceStream("arcana.json") ?? throw new InvalidOperationException("arcana.json is not embedded");
        using var doc = JsonDocument.Parse(stream);
        return new ArcanaTables(doc.RootElement.GetProperty("arcana"));
    });
    public static ArcanaTables Embedded => embedded.Value;

    public ArcanaCreate? CreateOf(int id) => creates.GetValueOrDefault(id);
    public ArcanaItem? ArcanaOf(int itemId) => arcana.GetValueOrDefault(itemId);
    /// <summary>An enchant group's step at a level (ArcanaEnchant CurrentLevel), or null past its end.</summary>
    public EnchantStep? Step(string group, int level) => enchant.TryGetValue(group, out var steps) && level >= 0 && level < steps.Count ? steps[level] : null;
    public int MaxLevel(string group) => enchant.TryGetValue(group, out var steps) ? steps.Count : 0;
    public IReadOnlyList<(string Stat, int Value)> MainEffect(string group, int level) =>
        mainEffect.TryGetValue(group, out var levels) && levels.TryGetValue(level, out var list) ? list : [];
    /// <summary>An enchant material item's ArcanaEnchant group (its Item.dat InternalName), or null for any other item.</summary>
    public string? MaterialGroup(int itemId) => materials.GetValueOrDefault(itemId);
    /// <summary>A creation pick: the ArcanaStatFilter row of the category and kind (1 main stat, 2 sub skill), or null.</summary>
    public (string Class, string Stat)? Filter(string category, int kind, int id) =>
        filters.FirstOrDefault(f => f.Id == id && f.Category == category && f.Kind == kind) is { Stat: not null } f ? (f.Class, f.Stat) : null;
    /// <summary>A stat a creation may pick (ArcanaStatFilter): its row id for the category (1 main, 2 sub).</summary>
    public bool Selectable(string category, int kind, int statId) => filters.Any(f => f.Id == statId && f.Category == category && f.Kind == kind);
}
