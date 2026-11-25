using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class ShopPlatformTests
{
    [Fact]
    public void Platform_catalog_lists_client_bm_shop_goods()
    {
        var platform = PlatformGoodsTables.Embedded;
        // BMShopGoods.dat: 236 rows, 208 bOnSale. GameShop (NPC) goods are not BM goods.
        Assert.Equal(208, platform.Filter(null, 1, 500, saleStatusMask: 2).Count);
        Assert.Contains(platform.Filter(null, 1, 500, 2), r => r.GoodsId == 1152);
        Assert.DoesNotContain(platform.Filter(null, 1, 500), r => r.GoodsId == 103015101);
        Assert.Equal(103015101, platform.ResolveGameGoodsId(103015101));
    }

    [Fact]
    public void Platform_filter_by_bm_category_returns_on_sale_subset()
    {
        var tables = PlatformGoodsTables.Embedded;
        var usable = tables.Filter("shop_usable", 1, 500, 2);
        Assert.InRange(usable.Count, 1, tables.Count(null, 2));
        Assert.All(usable, row => Assert.Equal(2, row.SaleStatus));
    }

    [Fact]
    public void Premium_mode_defaults_to_quna()
    {
        Assert.Equal(ShopPremiumMode.Quna, new ShopOptions().PremiumMode);
        Assert.Equal(ShopPremiumMode.Quna, ShopOptions.Parse(null));
        Assert.Equal(ShopPremiumMode.Kina, ShopOptions.Parse("Kina"));
        Assert.Equal(ShopPremiumMode.Free, ShopOptions.Parse("Free"));
        Assert.Equal(ShopPremiumMode.Disabled, ShopOptions.Parse("Disabled"));
    }

    [Fact]
    public void Platform_quna_goods_omitted_from_shops_json_map_to_a_shop_row()
    {
        var platform = PlatformGoodsTables.Embedded;
        Assert.Null(platform.ResolveGameGoodsId(1242));
        Assert.Null(ShopTables.Embedded.RowByGoodsId(1242));
        var row = platform.ToShopGoods(1242);
        Assert.NotNull(row);
        Assert.Equal(534200008, row.ItemId);
        Assert.Equal(1, row.Bundle);
        Assert.Equal(300, row.Price);
        Assert.Equal(platform.QunaItemId, row.Currency);
        Assert.Equal(0, row.Level);
        // Cash membership stays on the dedicated path, not this mapping.
        Assert.Null(platform.ToShopGoods(100024));
        Assert.Null(platform.ToShopGoods(999_999_999));
        Assert.Equal(platform.QunaItemId, platform.CurrencyItemId("GameCash"));
        Assert.Equal(platform.KinaItemId, platform.CurrencyItemId("GoldCombined"));
        Assert.Equal(platform.QunaItemId, platform.CurrencyItemId("Cash"));
        Assert.Equal(platform.QunaItemId, platform.CurrencyItemId("Mileage"));
        // Cash skins are sold for Quna at the platform price. Quna top-up stays unsold.
        var cashSkin = platform.ToShopGoods(100115);
        Assert.NotNull(cashSkin);
        Assert.Equal(platform.QunaItemId, cashSkin!.Currency);
        Assert.Equal(29000, cashSkin.Price);
        Assert.Equal(534760341, cashSkin.ItemId);
        Assert.Null(platform.ToShopGoods(100003));
    }
    [Fact]
    public void Official_shop_quna_goods_missing_from_platform_goods_map_to_a_shop_row()
    {
        const int OfficialOnlyQunaGoods = 1252, OfficialOnlyQunaItem = 534200002;
        Assert.Null(ShopTables.Embedded.RowByGoodsId(OfficialOnlyQunaGoods));
        Assert.Null(PlatformGoodsTables.Embedded.ToShopGoods(OfficialOnlyQunaGoods));
        var row = OfficialShopGoods.Embedded.ToShopGoods(OfficialOnlyQunaGoods);
        Assert.NotNull(row);
        Assert.Equal(OfficialOnlyQunaItem, row.ItemId);
        Assert.Equal(1, row.Bundle);
        Assert.Equal(300, row.Price);
        Assert.Equal(PlatformGoodsTables.Embedded.QunaItemId, row.Currency);
        // Membership uses the dedicated path. Cash skins resolve as Quna. Multi-item packs grant every part.
        Assert.Null(OfficialShopGoods.Embedded.ToShopGoods(100024));
        Assert.Null(OfficialShopGoods.Embedded.ToShopGoods(999_999_999));
        var cashSkin = OfficialShopGoods.Embedded.ToShopGoods(100115);
        Assert.NotNull(cashSkin);
        Assert.Equal(PlatformGoodsTables.Embedded.QunaItemId, cashSkin!.Currency);
        Assert.Equal(29000, cashSkin.Price);
        // Founder packs have a null platform part: only game_item_key rows are granted.
        var parts = OfficialShopGoods.Embedded.Parts(1152);
        Assert.Equal([(503700002, 1)], parts);
        Assert.Contains(OfficialShopGoods.Embedded.Parts(100047), p => p.ItemId == 536310006);
    }

    [Fact]
    public void Official_shop_goods_use_character_level_from_game_data()
    {
        var row = OfficialShopGoods.Embedded.ToShopGoods(100106);
        Assert.NotNull(row);
        Assert.Equal(2, row!.Level);
    }
}