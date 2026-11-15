using System.Globalization;
using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Adapted from Addiboii/A2Core (update-26262663) 13aa9ef and 101b443.
/// <summary>
/// Soul-bind stat pools, from tools/data/soulbind.json (tools/soulbind_data.py, Item.dat of build 26262663): per item its
/// SoulbindRandomStatCount and SubStats pool [EStat, MinValue, MaxValue, RandomWeight]; the EStat numbers by name.
/// </summary>
public static class SoulBindPools
{
    public sealed record Binding(int StatCount, int[][] Pool);

    sealed record Tables(Dictionary<int, Binding> Items, Dictionary<string, int> Stats);

    static readonly Lazy<Tables> tables = new(() =>
    {
        using var stream = typeof(SoulBindPools).Assembly.GetManifestResourceStream("soulbind.json");
        if (stream is null) return new([], []);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        var pools = root.GetProperty("pools").EnumerateArray()
            .Select(p => p.EnumerateArray().Select(r => r.EnumerateArray().Select(v => v.GetInt32()).ToArray()).ToArray()).ToList();
        var items = root.GetProperty("items").EnumerateObject().ToDictionary(
            p => int.Parse(p.Name, CultureInfo.InvariantCulture), p => new Binding(p.Value[0].GetInt32(), pools[p.Value[1].GetInt32()]));
        return new(items, root.GetProperty("stats").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32()));
    });

    public static Binding? Of(int itemId) => tables.Value.Items.GetValueOrDefault(itemId);

    /// <summary>An EStat number by name (0 when no pool uses it).</summary>
    public static int Stat(string name) => tables.Value.Stats.GetValueOrDefault(name);

    /// <summary>An EStat name by number (null when no pool uses it); for the stats item-stats.json does not map.</summary>
    public static string? NameOf(int stat) => tables.Value.Stats.FirstOrDefault(p => p.Value == stat).Key;

    /// <summary>
    /// The stats after a bind to <paramref name="count"/>. OBSERVED (community recordings 2026-11-04 and 11-06, every 0x5630 with
    /// stats): from bind count 2 the item holds SoulbindRandomStatCount distinct stats of its pool, kept from bind to bind, each
    /// within MinValue..MaxValue, and a further bind raises them toward MaxValue (count 4 to 5: +0 at the maximum, +1, +5, +1;
    /// 2 to 3: +7, +4, +2; count 9: three of four at the maximum).
    /// CANDIDATE: the first bind draws half the count, rounded up (the community fork's two first binds: 1 of 2 and 2 of
    /// 4; none in ours); a new stat is drawn by RandomWeight at a uniform value; a further bind adds a uniform 0..half the range.
    /// </summary>
    public static List<(int Type, int Value)> Bind(Binding binding, IReadOnlyList<(int Type, int Value)> held, int count, Random rng)
    {
        var range = binding.Pool.ToDictionary(p => p[0], p => (Min: p[1], Max: p[2]));
        var stats = held.Where(s => range.ContainsKey(s.Type))
            .Select(s => count <= 1 ? s : (s.Type, Math.Min(range[s.Type].Max, s.Value + rng.Next(0, (range[s.Type].Max - range[s.Type].Min) / 2 + 1))))
            .ToList();
        var want = count <= 1 ? (binding.StatCount + 1) / 2 : binding.StatCount;
        var left = binding.Pool.Where(p => p[3] > 0 && stats.All(s => s.Type != p[0])).ToList();
        while (stats.Count < want && left.Count > 0)
        {
            var roll = rng.NextInt64(left.Sum(p => (long)p[3]));
            var pick = left.First(p => (roll -= p[3]) < 0);
            stats.Add((pick[0], rng.Next(pick[1], pick[2] + 1)));
            left.Remove(pick);
        }
        return stats;
    }
}

public sealed partial class GameSession
{
    /// <summary>
    /// A stored item's soul-bind stats (upgrades.soul_stats, [[EStat, value], ...]). Once present they are the item's sub stats:
    /// they replace the stats rolled at creation (rolled_stats) in the stat sum and in the item detail.
    /// </summary>
    static List<(int Type, int Value)> SoulStatsOf(ItemState item) =>
        item.Upgrades.ValueKind == JsonValueKind.Object && item.Upgrades.TryGetProperty("soul_stats", out var stats) && stats.ValueKind == JsonValueKind.Array
            ? stats.EnumerateArray().Where(s => s.GetArrayLength() == 2).Select(s => (s[0].GetInt32(), s[1].GetInt32())).ToList() : [];
}

public sealed partial class GameSession
{
    /// <summary>
    /// The stats a bind starts from: the stored soul stats, or before the first bind the item's rolled sub stats
    /// (our rolls at creation draw from the same SubStats pool), so a bind keeps what the item already showed.
    /// </summary>
    static List<(int Type, int Value)> SoulBindHeld(ItemState item) =>
        SoulStatsOf(item) is { Count: > 0 } soul ? soul : RolledWireStats(item.RolledStats).Select(s => (s.Wire, s.Value)).ToList();
}
