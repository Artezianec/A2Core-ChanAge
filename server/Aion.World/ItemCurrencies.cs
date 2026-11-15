using System.Text.Json;

namespace Aion.World;

/// <summary>
/// Item.dat ItemType Currency (Kina, Quna, Mileage, ...; tools/data/item-currencies.json, build 26262663, from
/// Addiboii/A2Core 2ed0f24): held in the bag without a bag slot of their own, the client shows them in its currency bar.
/// </summary>
public static class ItemCurrencies
{
    static readonly Lazy<HashSet<int>> ids = new(() =>
    {
        using var stream = typeof(ItemCurrencies).Assembly.GetManifestResourceStream("item-currencies.json");
        if (stream is null) return [];
        using var doc = JsonDocument.Parse(stream);
        return [.. doc.RootElement.GetProperty("currencies").EnumerateArray().Select(e => e.GetInt32())];
    });

    public static IReadOnlyCollection<int> All => ids.Value;
    public static bool Is(int itemId) => ids.Value.Contains(itemId);
}
