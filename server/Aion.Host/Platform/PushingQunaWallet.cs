using Aion.Application.Gameplay;
using Aion.World;

namespace Aion.Host.Platform;

/// <summary>A wallet change to show: the account's new balance and its ledger entry (<see cref="EntryId"/> orders pushes).</summary>
public sealed record QunaBalancePush(Guid AccountId, long Balance, long EntryId);

/// <summary>
/// The host's Quna wallet (<see cref="Aion.Infrastructure.Postgres.PostgresQunaWallet"/>, migration 111): after every applied
/// change (not a repeat) the new balance goes to the account's sessions in the world, so the virtual Quna stack and
/// RefreshCurrency_NT follow a staff grant, a refund or a purchase made elsewhere. The push does not hold up the answer;
/// a session that misses one reads the balance again at its next inventory request or world entry.
/// Ported from KiskEmu (AGPL-3.0), where the realm pushes to the world server the account plays on; here the world is
/// this process (single host), so the push goes straight to <see cref="WorldPresence"/>.
/// </summary>
public sealed class PushingQunaWallet(IQunaWallet inner, Func<QunaBalancePush, Task<int>> push, ILogger<PushingQunaWallet> logger) : IQunaWallet
{
    public PushingQunaWallet(IQunaWallet inner, WorldPresence presence, ILogger<PushingQunaWallet> logger)
        : this(inner, change => Task.FromResult(QunaPushed(presence, change)), logger) { }

    /// <summary>The last push started (tests wait on it).</summary>
    public Task LastPush { get; private set; } = Task.CompletedTask;

    public bool AcceptsGrants => inner.AcceptsGrants;

    public Task<QunaResult> BalanceAsync(Guid account, CancellationToken ct = default) => inner.BalanceAsync(account, ct);

    public async Task<QunaResult> SpendAsync(QunaRequest request, CancellationToken ct = default) =>
        Pushed(request.AccountId, await inner.SpendAsync(request, ct));

    public async Task<QunaResult> GrantAsync(QunaRequest request, CancellationToken ct = default) =>
        Pushed(request.AccountId, await inner.GrantAsync(request, ct));

    public async Task<QunaResult> RefundAsync(Guid account, string spendRequestId, int? worldId = null, CancellationToken ct = default) =>
        Pushed(account, await inner.RefundAsync(account, spendRequestId, worldId, ct));

    public async Task<QunaResult?> AbsorbItemStackAsync(Guid account, Guid instance, int? worldId = null, CancellationToken ct = default) =>
        await inner.AbsorbItemStackAsync(account, instance, worldId, ct) is { } result ? Pushed(account, result) : null;

    public async Task<QunaResult> StaffChangeAsync(Guid actor, Guid account, string requestId, long delta, string? note, CancellationToken ct = default) =>
        Pushed(account, await inner.StaffChangeAsync(actor, account, requestId, delta, note, ct));

    public async Task<QunaResult> StaffRefundAsync(Guid actor, Guid account, string spendRequestId, CancellationToken ct = default) =>
        Pushed(account, await inner.StaffRefundAsync(actor, account, spendRequestId, ct));

    public Task<IReadOnlyList<QunaLedgerEntry>> LedgerAsync(Guid account, int limit, CancellationToken ct = default) => inner.LedgerAsync(account, limit, ct);

    QunaResult Pushed(Guid account, QunaResult result)
    {
        if (result is { Ok: true, Repeated: false })
            LastPush = PushAsync(new QunaBalancePush(account, result.Balance, result.EntryId));
        return result;
    }

    async Task PushAsync(QunaBalancePush change)
    {
        try { await push(change); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogWarning("Quna balance push for account {Account} failed: {Error}", change.AccountId, error.Message);
        }
    }

    /// <summary>Hands a pushed balance to every session of the account in this world (on its game loop); the number reached.</summary>
    public static int QunaPushed(WorldPresence? presence, QunaBalancePush push)
    {
        var reached = 0;
        foreach (var entry in presence?.All ?? [])
            if (entry.AccountId == push.AccountId || entry.Session.Store?.AccountId == push.AccountId)
            {
                var session = entry.Session;
                _ = session.InvokeAsync(async _ => (true, await session.QunaPushedAsync(push.Balance, push.EntryId)));
                reached++;
            }
        return reached;
    }
}
