using Aion.Application.Gameplay;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class MileageWalletTests
{
    [Fact]
    public async Task Memory_wallet_earn_and_spend()
    {
        var wallet = new MemoryMileageWallet();
        var account = Guid.NewGuid();
        Assert.True((await wallet.GrantAsync(new MileageRequest(account, "g1", 1000, MileageWalletRules.Grant))).Ok);
        Assert.True((await wallet.SpendAsync(new MileageRequest(account, "s1", 400, MileageWalletRules.Purchase))).Ok);
        Assert.Equal(600, (await wallet.BalanceAsync(account)).Balance);
    }

    [Fact]
    public void Apply_mileage_wallet_remaps_mileage_tab_goods()
    {
        var features = new ShopFeatureOptions { UseMileageCurrency = true };
        var row = new ShopTables.GoodsRow(532760001, 1, 4000, 0, PlatformGoodsTables.Embedded.QunaItemId, 0, 0, 0);
        var mapped = ShopFeatureLayer.ApplyMileageWallet(row, 1212, features);
        Assert.NotNull(mapped);
        Assert.Equal(ShopFeatureLayer.MileageChargeItemId, mapped!.Currency);
        Assert.Equal(4000, mapped.Price);
    }
}