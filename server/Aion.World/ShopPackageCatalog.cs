using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>Package / step-up bundle rules for BM SKUs (tools/data/shop_package_tiers.json).</summary>
public sealed class ShopPackageCatalog
{
    public sealed record TierRule(
        [property: JsonPropertyName("tier")] int Tier,
        [property: JsonPropertyName("requires_goods")] int? RequiresGoods,
        [property: JsonPropertyName("parts")] int[][] Parts,
        [property: JsonPropertyName("first_purchase_bonus")] int[][]? FirstPurchaseBonus);

    sealed record File([property: JsonPropertyName("bundles")] Dictionary<string, TierRule[]> Bundles);

    readonly Dictionary<int, TierRule[]> byGoods;

    ShopPackageCatalog(Dictionary<int, TierRule[]> byGoods) => this.byGoods = byGoods;

    static readonly Lazy<ShopPackageCatalog> embedded = new(() =>
    {
        using var stream = typeof(ShopPackageCatalog).Assembly.GetManifestResourceStream("shop_package_tiers.json");
        if (stream is null) return new ShopPackageCatalog([]);
        var file = JsonSerializer.Deserialize<File>(stream) ?? new File([]);
        var map = new Dictionary<int, TierRule[]>();
        foreach (var (key, tiers) in file.Bundles)
            if (int.TryParse(key, out var goodsId)) map[goodsId] = tiers;
        return new ShopPackageCatalog(map);
    });

    public static ShopPackageCatalog Embedded => embedded.Value;

    public IReadOnlyList<TierRule> Tiers(int goodsId) => byGoods.GetValueOrDefault(goodsId) ?? [];

    public TierRule? RuleFor(int goodsId, int unlockedTier) =>
        Tiers(goodsId).FirstOrDefault(t => t.Tier == unlockedTier + 1)
        ?? Tiers(goodsId).OrderBy(t => t.Tier).LastOrDefault();

    public void ValidateAgainstOfficialParts()
    {
        foreach (var (goodsId, tiers) in byGoods)
        {
            var official = OfficialShopGoods.Embedded.Parts(goodsId);
            foreach (var tier in tiers)
            {
                foreach (var (template, count) in tier.Parts.Select(p => (p[0], p[1])))
                {
                    if (official.Count > 0 && !official.Any(o => o.ItemId == template))
                        throw new InvalidOperationException($"shop_package_tiers: goods {goodsId} tier {tier.Tier} part {template} missing from official_shop parts");
                    if (count < 1) throw new InvalidOperationException($"shop_package_tiers: goods {goodsId} invalid part count");
                }
            }
        }
    }
}