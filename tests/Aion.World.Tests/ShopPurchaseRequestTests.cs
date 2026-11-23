using Aion.Application.Gameplay;
using Xunit;

namespace Aion.World.Tests;

public sealed class ShopPurchaseRequestTests
{
    static readonly Guid Account = Guid.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    static readonly Guid Character = Guid.Parse("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

    [Fact]
    public void Client_serial_makes_distinct_fingerprints()
    {
        var lines = new List<(int, int)> { (1242, 1) };
        var a = ShopPurchaseRequest.Fingerprint(1204, Account, Character, "bm", lines, 1);
        var b = ShopPurchaseRequest.Fingerprint(1204, Account, Character, "bm", lines, 2);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Allocate_reuses_id_inside_retry_window_only()
    {
        const string fp = "fpabc";
        var first = ShopPurchaseRequest.Allocate(fp, 1_000, null, null, null);
        Assert.Equal(fp, first);
        var retry = ShopPurchaseRequest.Allocate(fp, 2_500, first, "done", 1_000);
        Assert.Equal(first, retry);
        var later = ShopPurchaseRequest.Allocate(fp, 5_000, first, "done", 1_000);
        Assert.StartsWith(fp + ":", later, StringComparison.Ordinal);
    }
}