using Aion.Application.Chat;
using Aion.Application.Market;
using Aion.Core.Accounts;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>//ah chat market (docs/MARKET.md; ported from ckrealm/A2Core): parsing, gating, and command routing.</summary>
public sealed class MarketCommandTests
{
    static readonly MarketOptions Off = new();
    static readonly MarketOptions On = new() { Enabled = true };

    [Theory]
    [InlineData("150", 150L)]
    [InlineData("1.5k", 1500L)]
    [InlineData("2m", 2_000_000L)]
    public void ParseKinah_parses_amounts(string text, long expected) => Assert.Equal(expected, MarketCommands.ParseKinah(text));

    [Fact]
    public void ParseSell_splits_item_count_and_price()
    {
        Assert.Equal(("Iron Ore", 20L, 150L), MarketCommands.ParseSell(["Iron", "Ore", "20", "150"]));
        Assert.Equal(("Iron Ore", 20L, null), MarketCommands.ParseSell(["Iron", "Ore", "20", "auto"]));
        Assert.Equal(("610710006", 5L, null), MarketCommands.ParseSell(["610710006", "5"]));
    }

    [Fact]
    public void MarketOptions_default_is_off()
    {
        var options = new MarketOptions().Validated();
        Assert.False(options.Enabled);
        Assert.False(options.Seller.Enabled);
        Assert.False(options.Buyer.Enabled);
    }

    [Fact]
    public async Task HandleAsync_when_disabled_returns_message()
    {
        var commands = new MarketCommands(new FakeMarketStore(), Off, MarketPricing.Embedded);
        var session = GameSessionTestHelpers.Session(new FakeCharacterStore());
        var text = await commands.HandleAsync(session, "search ore", default);
        Assert.Contains("off on this server", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GmCommands_routes_ah_when_market_wired()
    {
        var presence = new WorldPresence();
        var store = new FakeCharacterStore();
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, 1_700_000_000_000);
        var name = session.CharacterName ?? "Hero";
        presence.Enter(name, session, Guid.NewGuid());
        var market = new MarketCommands(new FakeMarketStore(), On, MarketPricing.Embedded);
        var gm = new GmCommands(presence, new BuilderCommands(presence, new GmPolicy(), (_, _, _) => { }, GameData.Embedded))
        {
            Market = market,
        };
        var help = await gm.HandleAsync(new ChatIdentity("1", "1204", "en_US"), name, "//ah", default);
        Assert.Contains("search", help, StringComparison.OrdinalIgnoreCase);
    }

    sealed class FakeMarketStore : IMarketStore
    {
        public Task<IReadOnlyList<MarketListing>> SearchAsync(string? text, int? templateId, int? worldId, int offset, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MarketListing>>([]);
        public Task<IReadOnlyList<MarketListing>> PlayerListingsAsync(DateTimeOffset listedBefore, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MarketListing>>([]);
        public Task<IReadOnlyList<MarketListing>> ListingsOfAsync(Guid? seller, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MarketListing>>([]);
        public Task<MarketListing?> ListingAsync(long id, CancellationToken ct = default) => Task.FromResult<MarketListing?>(null);
        public Task<MarketListingResult> ListAsync(VerifiedAccount account, Guid character, Guid instance, long quantity, long priceEach, TimeSpan duration, int maxListings, CancellationToken ct = default) =>
            Task.FromResult(new MarketListingResult("ok"));
        public Task<MarketListing?> ListForBotAsync(string sellerName, int templateId, long quantity, long priceEach, TimeSpan duration, CancellationToken ct = default) =>
            Task.FromResult<MarketListing?>(null);
        public Task<MarketPurchase> BuyAsync(VerifiedAccount account, Guid character, long listingId, long quantity, double taxPercent, int? worldId, CancellationToken ct = default) =>
            Task.FromResult(new MarketPurchase("not_found"));
        public Task<MarketPurchase> BotBuyAsync(long listingId, double taxPercent, CancellationToken ct = default) =>
            Task.FromResult(new MarketPurchase("not_found"));
        public Task<string> CancelAsync(VerifiedAccount account, Guid character, long listingId, CancellationToken ct = default) => Task.FromResult("not_found");
        public Task<IReadOnlyList<MarketExpiry>> ExpireAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MarketExpiry>>([]);
        public Task<IReadOnlyList<(int TemplateId, string Name)>> FindItemsAsync(string text, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(int, string)>>([]);
        public Task<MarketPriceHistory> PriceHistoryAsync(int templateId, TimeSpan window, CancellationToken ct = default) =>
            Task.FromResult(new MarketPriceHistory(templateId, 0, 0, 0));
        public Task<long> BotSpentSinceAsync(DateTimeOffset since, CancellationToken ct = default) => Task.FromResult(0L);
    }
}