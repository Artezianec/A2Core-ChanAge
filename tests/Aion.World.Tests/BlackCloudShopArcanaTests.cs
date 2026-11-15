using Xunit;

namespace Aion.World.Tests;

/// <summary>The Black Cloud Merchants (random shop, 0x5691-0x5699) and Arcana (0x564f-0x5658) ports.
/// Ported from zaraza87/A2Core dell-merge-2026-11 (29934bea, docs/DELL-CHANGES-2026-11.md): their native checks as unit tests.</summary>
public sealed class BlackCloudShopArcanaTests
{
    static readonly RandomShopTables Tables = RandomShopTables.Embedded;

    static byte[] List(int category, (int Id, int Discount, bool Bought)[] slots, string next) => RandomShop.InfoNt(category,
        slots.Select(x => RandomShop.Wire(RandomShopRules.Default, Tables.GoodsOf(x.Id)!, x.Discount, x.Bought)).ToList(), DateTimeOffset.Parse(next).ToUnixTimeMilliseconds(), 0);

    [Fact]
    public void The_shop_and_arcana_are_on_by_default() => Assert.True(new GameSessionOptions().ExperimentalRandomShopArcana);

    [Fact]
    public void The_shop_tables_and_rules_load()
    {
        Assert.Equal(6, Tables.Shops.Count());
        Assert.Equal(4404, Tables.GoodsCount);
        Assert.Equal(1118, Tables.Pool("group_1_45_Dark").Count);
        Assert.Equal(884, Tables.Pool("group_skin_Light").Count);
        Assert.Equal(200, Tables.Pool("group_vehicle_Dark").Count);
        Assert.Equal(new long[] { 500, 1000, 2000, 3000, 4000, 6000, 8000, 10000, 12000, 15000 }, Tables.RefreshCosts("refresh_cost_01"));
        Assert.Equal(201, RandomShopTables.WireCategory("D_All"));
        Assert.Equal(204, RandomShopTables.WireCategory("D_Pet"));
        Assert.Equal(101, RandomShopTables.WireCategory("L_All"));
        Assert.Equal("group_vehicle_Dark", Tables.ShopOf(204)?.Group);
        var rules = RandomShop.Current;
        Assert.True(rules.Enabled);
        Assert.Equal(10, rules.Slots);
        Assert.Equal(60, rules.RefreshMinutes);
        Assert.Equal(new[] { 90, 80, 70, 50 }, rules.Discounts.Select(d => d.Percent));
    }

    [Fact]
    public void The_list_matches_the_official_bytes()
    {
        var all = Convert.FromHexString("9156c90a4e3803008bcadf1f0100000000000000015da086010000000000803801000000000055e737030041cadf1f0100000000000000015da0860100000000008038010000000000b7350300e3cfef1f0100000000000000005dd47b000000000000726f0000000000006534030085c0d11f0100000000000000015d007c9200000000000030750000000000c43703001757f11f0100000000000000005dc089010000000000606201000000000055d1370300a056f11f0100000000000000005d80d5040000000000c059040000000000a735030009d2ef1f0100000000000000025d80bb0000000000004083000000000000c5350300e5cfef1f0100000000000000005dd47b000000000000726f0000000000009c38030006cbdf1f0100000000000000005da086010000000000905f010000000000055b3403009ce7d11f0100000000000000015d005307000000000000dc05000000000080d0f1b0a101000000000000");
        Assert.Equal(all, List(201, [(211022, 1, false), (210919, 1, false), (210359, 0, false), (210021, 1, false), (210884, 0, false), (210897, 0, false),
            (210343, 2, false), (210373, 0, false), (211100, 0, false), (210011, 1, false)], "2026-11-06T13:00:00Z"));
        var pets = Convert.FromHexString("9156cc0ad25b0300dccadf1f0100000000000000015da08601000000000080380100000000005f745b030054cadf1f0100000000000000015da0860100000000008038010000000000d55b0300dfcadf1f0100000000000000015da0860100000000008038010000000000215c030015cbdf1f0100000000000000015da0860100000000008038010000000000765b03005acadf1f0100000000000000025da086010000000000701101000000000057075c0300fbcadf1f0100000000000000025da0860100000000007011010000000000645b030044cadf1f0100000000000000005da086010000000000905f0100000000006c5b03004ccadf1f0100000000000000025da0860100000000007011010000000000d65b0300e0cadf1f0100000000000000015da086010000000000803801000000000005875b03006bcadf1f0100000000000000015da086010000000000803801000000000080d0f1b0a101000000000000");
        Assert.Equal(pets, List(204, [(220114, 1, true), (220020, 1, true), (220117, 1, false), (220193, 1, false), (220022, 2, true), (220167, 2, false),
            (220004, 0, false), (220012, 2, false), (220118, 1, false), (220039, 1, false)], "2026-11-06T13:00:00Z"));
        Assert.Equal(GameSession.BlackCloudShopInfoNt, BitConverter.ToUInt16(all, 0));
    }

    [Fact]
    public void Lists_renew_hourly_and_rolls_respect_the_pool()
    {
        Assert.Equal(DateTimeOffset.Parse("2026-11-06T13:00:00Z").ToUnixTimeMilliseconds(),
            RandomShopRules.Default.NextRefresh(DateTimeOffset.Parse("2026-11-06T12:05:56.018Z").ToUnixTimeMilliseconds()));
        var shop = Tables.ShopOf(201)!;
        var rng = new Random(11);
        for (var i = 0; i < 100; i++)
        {
            var roll = RandomShop.Current.Roll(shop, 5, rng);
            Assert.Equal(10, roll.Count);
            Assert.Equal(10, roll.Select(x => x[0]).Distinct().Count());
            Assert.All(roll, x => Assert.True(Tables.GoodsOf(x[0])!.LevelMin <= 5 && x[1] is >= 0 and <= 3 && x[2] == 0));
        }
    }

    [Fact]
    public void The_arcana_tables_load()
    {
        var arcana = ArcanaTables.Embedded;
        var create = arcana.CreateOf(1)!;
        Assert.Equal(810130001, create.Item);
        Assert.Equal(2_000_000, create.Kina);
        var card = arcana.ArcanaOf(create.Item)!;
        Assert.NotEmpty(card.MainStats);
        Assert.True(arcana.MaxLevel(card.EnchantGroup) > 0);
        Assert.True(arcana.Step(card.EnchantGroup, 0)!.NeedExp > 0);
        Assert.Null(arcana.Step(card.EnchantGroup, arcana.MaxLevel(card.EnchantGroup)));
        Assert.Equal(1440, arcana.CreateCooltimeMinutes);
        Assert.All(card.MainStats, s => Assert.NotEqual(0, ItemStatWire.Embedded.WireOf(s.Stat)));
    }

    [Fact]
    public void Arcana_materials_picks_and_created_stats()
    {
        var arcana = ArcanaTables.Embedded;
        Assert.Equal("Arcana_Material_A_c_002a_b", arcana.MaterialGroup(830060002));
        Assert.True(arcana.Step(arcana.MaterialGroup(830060002)!, 0)!.GetExp > 0);
        Assert.Null(arcana.MaterialGroup(GameSession.KinaItemId));
        Assert.Equal(("None", "Time"), arcana.Filter("ArcanaGrail", 1, 10001));
        Assert.Equal(("Gladiator", "11020000"), arcana.Filter("ArcanaGrail", 2, 10003));
        Assert.Null(arcana.Filter("ArcanaBell", 1, 10001));
        var card = arcana.ArcanaOf(810130001)!;
        Assert.Equal(card.MainStats, GameSession.ArcanaCreatedStats(card, []));
        Assert.Equal([("Space", 20)], GameSession.ArcanaCreatedStats(card, ["Space"]));
    }
}
