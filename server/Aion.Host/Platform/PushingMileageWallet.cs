using Aion.Application.Gameplay;
using Aion.World;

namespace Aion.Host.Platform;

public sealed record MileageBalancePush(Guid AccountId, long Balance, long EntryId);

public sealed class PushingMileageWallet(IMileageWallet inner, WorldPresence presence, ILogger<PushingMileageWallet> logger) : IMileageWallet
{
    public bool AcceptsGrants => inner.AcceptsGrants;

    public Task<MileageResult> BalanceAsync(Guid account, CancellationToken ct = default) => inner.BalanceAsync(account, ct);

    public async Task<MileageResult> SpendAsync(MileageRequest request, CancellationToken ct = default) =>
        Pushed(request.AccountId, await inner.SpendAsync(request, ct));

    public async Task<MileageResult> GrantAsync(MileageRequest request, CancellationToken ct = default) =>
        Pushed(request.AccountId, await inner.GrantAsync(request, ct));

    public async Task<MileageResult> EarnAsync(MileageRequest request, CancellationToken ct = default) =>
        Pushed(request.AccountId, await inner.EarnAsync(request, ct));

    public Task<IReadOnlyList<MileageLedgerEntry>> LedgerAsync(Guid account, int limit, CancellationToken ct = default) =>
        inner.LedgerAsync(account, limit, ct);

    MileageResult Pushed(Guid account, MileageResult result)
    {
        if (result is { Ok: true, Repeated: false }) _ = PushAsync(new MileageBalancePush(account, result.Balance, result.EntryId));
        return result;
    }

    async Task PushAsync(MileageBalancePush change)
    {
        try
        {
            foreach (var entry in presence.All)
                if (entry.AccountId == change.AccountId || entry.Session.Store?.AccountId == change.AccountId)
                    _ = entry.Session.InvokeAsync(async _ => (true, await entry.Session.MileagePushedAsync(change.Balance, change.EntryId)));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogWarning("Mileage balance push for account {Account} failed: {Error}", change.AccountId, error.Message);
        }
    }
}