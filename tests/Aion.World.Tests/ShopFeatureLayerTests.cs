using Aion.Application.Gameplay;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class ShopFeatureLayerTests
{
    [Fact]
    public void Mileage_categories_detected()
    {
        Assert.True(ShopFeatureLayer.IsMileageCategory("Mileage_Usable"));
        Assert.True(ShopFeatureLayer.IsMileageCategory("D_Mileage_Special"));
        Assert.False(ShopFeatureLayer.IsMileageCategory("Shop_Special"));
    }

    [Fact]
    public void Package_catalog_validates_at_startup()
    {
        ShopPackageCatalog.Embedded.ValidateAgainstOfficialParts();
    }
}