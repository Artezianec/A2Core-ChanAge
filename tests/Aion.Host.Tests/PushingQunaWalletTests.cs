using Aion.Application.Gameplay;
using Aion.Host.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aion.Host.Tests;

public sealed class PushingQunaWalletTests
{
    readonly List<QunaBalancePush> pushes = [];

    PushingQunaWallet Wallet(MemoryQunaWallet inner) =>
        new(inner, change => { lock (pushes) pushes.Add(change); return Task.FromResult(1); }, NullLogger<PushingQunaWallet>.Instance);

    [Fact]
    public async Task Every_applied_change_pushes_the_new_balance_once()
    {
        var account = Guid.NewGuid();
        var wallet = Wallet(new MemoryQunaWallet());
        var granted = await wallet.GrantAsync(new QunaRequest(account, "g1", 1_000, QunaWalletRules.Grant));
        await wallet.LastPush;
        var spent = await wallet.SpendAsync(new QunaRequest(account, "s1", 300, QunaWalletRules.Purchase));
        await wallet.LastPush;
        // A repeated request changes nothing and pushes nothing.
        Assert.True((await wallet.SpendAsync(new QunaRequest(account, "s1", 300, QunaWalletRules.Purchase))).Repeated);
        var refunded = await wallet.RefundAsync(account, "s1");
        await wallet.LastPush;
        Assert.Equal([new QunaBalancePush(account, 1_000, granted.EntryId), new QunaBalancePush(account, 700, spent.EntryId),
            new QunaBalancePush(account, 1_000, refunded.EntryId)], pushes);
    }

    [Fact]
    public async Task A_refused_change_pushes_nothing_and_a_failing_push_does_not_fail_the_change()
    {
        var account = Guid.NewGuid();
        var wallet = Wallet(new MemoryQunaWallet());
        Assert.False((await wallet.SpendAsync(new QunaRequest(account, "s", 5, QunaWalletRules.Purchase))).Ok);
        Assert.Empty(pushes);

        var failing = new PushingQunaWallet(new MemoryQunaWallet(), _ => throw new InvalidOperationException("offline"),
            NullLogger<PushingQunaWallet>.Instance);
        var granted = await failing.GrantAsync(new QunaRequest(account, "g", 10, QunaWalletRules.Grant));
        await failing.LastPush;
        Assert.True(granted.Ok);
    }

    [Fact]
    public async Task Staff_changes_are_staff_entries_with_the_actor_and_push()
    {
        var (actor, account) = (Guid.NewGuid(), Guid.NewGuid());
        var inner = new MemoryQunaWallet();
        var wallet = Wallet(inner);
        var added = await wallet.StaffChangeAsync(actor, account, "staff:1", 500, "event prize");
        await wallet.LastPush;
        var taken = await wallet.StaffChangeAsync(actor, account, "staff:2", -200, null);
        await wallet.LastPush;
        Assert.Equal((true, 500L), (added.Ok, added.Balance));
        Assert.Equal((true, 300L), (taken.Ok, taken.Balance));
        Assert.All(inner.Ledger, e => Assert.Equal(QunaWalletRules.Staff, e.Reason));
        Assert.Equal(2, pushes.Count);
        Assert.Equal(300, (await wallet.LedgerAsync(account, 10))[0].BalanceAfter);
    }

    [Fact]
    public async Task A_wallet_that_takes_no_grants_refuses_them()
    {
        var wallet = Wallet(new MemoryQunaWallet { AcceptsGrants = false });
        Assert.False(wallet.AcceptsGrants);
        var granted = await wallet.GrantAsync(new QunaRequest(Guid.NewGuid(), "g", 10, QunaWalletRules.Grant));
        Assert.Equal(QunaWalletRules.RealmOnly, granted.Error);
        Assert.Empty(pushes);
    }
}
