using System.Text.Json;
using Aion.Host.Platform;
using Xunit;

namespace Aion.Host.Tests;

public sealed class OfficialShopCatalogTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Goods_page_has_full_official_catalog()
    {
        using var doc = JsonDocument.Parse(OfficialShopCatalog.Goods(1, 400, Now));
        var root = doc.RootElement;
        Assert.Equal("201", root.GetProperty("total_count").GetString());
        var page = root.GetProperty("page");
        Assert.Equal(201, page.GetArrayLength());
        var cats = page.EnumerateArray().Select(g => g.TryGetProperty("goods_data", out var d) ? d.GetString() ?? "" : "").ToList();
        Assert.Contains(cats, c => c.StartsWith("catl:SkinShop_SkinSet,", StringComparison.Ordinal));
        Assert.Contains(cats, c => c.StartsWith("catl:SkinShop_Weapon,", StringComparison.Ordinal));
        Assert.Contains(cats, c => c.StartsWith("catl:Goods_Usable,", StringComparison.Ordinal));
        Assert.Contains(cats, c => c.StartsWith("catl:Goods_SocialMotion,", StringComparison.Ordinal));
        Assert.Contains(cats, c => c.StartsWith("catl:SkinShop_ShoulderWear,", StringComparison.Ordinal));
        Assert.Contains(cats, c => c.StartsWith("catl:Subscribe,", StringComparison.Ordinal));
        Assert.DoesNotContain(cats, c => c.Contains("Daily_RechargeQuna", StringComparison.Ordinal));
        Assert.DoesNotContain(page.EnumerateArray(), g => g.GetProperty("goods_id").GetString() == "100133");
        foreach (var goodsId in Enumerable.Range(100088, 8))
        {
            var row = page.EnumerateArray().Single(g => g.GetProperty("goods_id").GetString() == goodsId.ToString());
            var price = row.GetProperty("goods_basic_price_policies")[0];
            Assert.Equal("145", price.GetProperty("currency_group_id").GetString());
            Assert.Equal("0.0000", price.GetProperty("goods_basic_price").GetString());
            Assert.Equal(0, row.GetProperty("pgs").GetArrayLength());
            Assert.True(row.GetProperty("items")[0].GetProperty("game_item_key").GetString() is { Length: > 0 });
        }
        Assert.DoesNotContain(page.EnumerateArray().SelectMany(g => (g.TryGetProperty("items", out var items) ? items : default).EnumerateArray()),
            it => it.TryGetProperty("game_item_key", out var k) && k.GetString() == "535300006");
        Assert.Contains(page.EnumerateArray().SelectMany(g => g.GetProperty("display_names").EnumerateArray()),
            n => n.GetProperty("language_code").GetString() == "ru-RU");
        Assert.All(page.EnumerateArray(), g => Assert.Equal("", g.TryGetProperty("restricted_game_server_ids", out var r) ? r.GetString() : ""));
        Assert.All(page.EnumerateArray(), g => Assert.True(DateTimeOffset.Parse(g.GetProperty("effective_to").GetString()!) >= Now));
    }

    [Fact]
    public void Policy_reflects_supplied_purchase_counts_in_quota()
    {
        var purchased = new Dictionary<int, int> { [1218] = 1 };
        using var doc = JsonDocument.Parse(OfficialShopCatalog.Policy("1218", null, "145", Now, purchased));
        var limit = doc.RootElement.GetProperty("quotas")[0].GetProperty("limit_list")[0];
        Assert.Equal("1", limit.GetProperty("purchased_quantity").GetString());
        Assert.Equal("1", limit.GetProperty("remaining_quantity").GetString());
    }

    [Fact]
    public void Policy_returns_quota_per_requested_goods_with_zero_purchases()
    {
        using var doc = JsonDocument.Parse(OfficialShopCatalog.Policy("1242,1213,999999", null, "145", Now));
        var q = doc.RootElement.GetProperty("quotas");
        Assert.Equal(3, q.GetArrayLength());
        Assert.All(q.EnumerateArray(), x => Assert.Equal("1", x.GetProperty("is_purchasable").GetString()));
        Assert.All(q.EnumerateArray().SelectMany(x => x.GetProperty("limit_list").EnumerateArray()),
            l => Assert.Equal("0", l.GetProperty("purchased_quantity").GetString()));
    }

    [Fact]
    public void Me_goods_lists_cash_goods()
    {
        using var doc = JsonDocument.Parse(OfficialShopCatalog.MeGoods(null, Now));
        Assert.Equal(18, doc.RootElement.GetProperty("page").GetArrayLength());
    }

    [Fact]
    public void Gate_always_serves_official_catalog_without_switch()
    {
        var q = new Microsoft.AspNetCore.Http.QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["page_index"] = "1", ["page_size"] = "400" });
        Assert.True(PlatformApiGateResponses.TryReply("GET", "/local-api-gate/goods/v2.0/goods", q, null, out var body));
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("201", doc.RootElement.GetProperty("total_count").GetString());
        Assert.True(PlatformApiGateResponses.TryReply("GET", "/local-api-gate/goods/v2.0/me/goods", q, null, out var me));
        using var meDoc = JsonDocument.Parse(me);
        Assert.Equal(18, meDoc.RootElement.GetProperty("page").GetArrayLength());
    }

    [Fact]
    public void Bodies_are_raw_utf8_like_official()
    {
        foreach (var body in new[] { OfficialShopCatalog.Goods(1, 400, Now), OfficialShopCatalog.Policy("1213", null, "158", Now) })
        {
            var s = System.Text.Encoding.UTF8.GetString(body);
            Assert.DoesNotContain("\\u", s);
            Assert.Contains("+09:00", s);
        }
        var q = System.Text.Encoding.UTF8.GetString(OfficialShopCatalog.Policy("1213", null, "158", Now));
        Assert.True(q.IndexOf("\"remaining_seconds\"", StringComparison.Ordinal) < q.IndexOf("\"current_reset_time\"", StringComparison.Ordinal));
    }
    [Fact]
    public void Membership_cash_goods_are_repriced_to_quna()
    {
        using var doc = JsonDocument.Parse(OfficialShopCatalog.Goods(1, 400, Now));
        var membership = doc.RootElement.GetProperty("page").EnumerateArray()
            .First(g => g.GetProperty("goods_id").GetString() == "100024");
        var policy = membership.GetProperty("goods_basic_price_policies")[0];
        Assert.Equal("145", policy.GetProperty("currency_group_id").GetString());
        Assert.Equal("25000.0000", policy.GetProperty("goods_basic_price").GetString());
        Assert.Equal(0, membership.GetProperty("goods_basic_currency_price_policies").GetArrayLength());
        Assert.Equal(0, membership.GetProperty("pgs").GetArrayLength());

        using var all = JsonDocument.Parse(OfficialShopCatalog.Goods(1, 400, Now));
        Assert.All(all.RootElement.GetProperty("page").EnumerateArray().Where(g =>
            g.GetProperty("goods_basic_price_policies")[0].GetProperty("currency_group_id").GetString() == "145"),
            g => Assert.Equal(0, g.GetProperty("pgs").GetArrayLength()));
        var accessory = all.RootElement.GetProperty("page").EnumerateArray()
            .First(g => g.GetProperty("goods_id").GetString() == "100158");
        Assert.Equal("145", accessory.GetProperty("goods_basic_price_policies")[0].GetProperty("currency_group_id").GetString());
        Assert.Equal("300.0000", accessory.GetProperty("goods_basic_price_policies")[0].GetProperty("goods_basic_price").GetString());
        Assert.Equal(0, accessory.GetProperty("pgs").GetArrayLength());

        using var quotas = JsonDocument.Parse(OfficialShopCatalog.Policy("100024", null, "1", Now));
        var q = quotas.RootElement.GetProperty("quotas")[0];
        Assert.Equal("145", q.GetProperty("currency_group_id").GetString());
        Assert.Equal("25000.000000", q.GetProperty("current_payment_amount").GetString());
        Assert.Equal("1", q.GetProperty("is_purchasable").GetString());
    }
}