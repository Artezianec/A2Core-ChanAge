using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    long accountMileageBalance;
    long mileageEntry = -1;
    long? shownMileage;

    bool MileageInWallet => Store?.MileageWallet is not null;

    public long MileageBalance() => accountMileageBalance;

    void NoteMileage(MileageResult result)
    {
        if (result.EntryId < mileageEntry) return;
        accountMileageBalance = result.Balance;
        mileageEntry = result.EntryId;
    }

    async Task RefreshAccountMileageBalanceAsync(CancellationToken ct)
    {
        if (Store is not { MileageWallet: { } wallet } store) return;
        try { NoteMileage(await wallet.BalanceAsync(store.AccountId, ct)); }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            PersistenceErrors.Add("mileage_balance");
            mileageEntry = Math.Max(mileageEntry, 0);
        }
    }

    public async Task<List<byte[]>> MileagePushedAsync(long balance, long entryId, CancellationToken ct = default)
    {
        if (!MileageInWallet || entryId < mileageEntry) return [];
        accountMileageBalance = balance;
        mileageEntry = entryId;
        return CharacterId is null ? [] : MileageRefreshIfChanged();
    }

    List<byte[]> MileageRefreshIfChanged()
    {
        if (shownMileage == accountMileageBalance) return [];
        shownMileage = accountMileageBalance;
        return [RefreshMileage(accountMileageBalance)];
    }

    (List<(Guid, long, int)> Costs, long Quna, long Mileage)? PurchaseCostsWithMileage(IEnumerable<(int Currency, long Price)> prices)
    {
        var costs = new List<(Guid, long, int)>();
        var quna = 0L;
        var mileage = 0L;
        foreach (var (currency, price) in prices)
        {
            if (price <= 0) continue;
            if (QunaInWallet && currency == PlatformGoodsTables.Embedded.QunaItemId)
            {
                if (!PacketSecurity.TryAdd(quna, price, out quna)) return null;
                continue;
            }
            if (MileageInWallet && currency == ShopFeatureLayer.MileageChargeItemId)
            {
                if (!PacketSecurity.TryAdd(mileage, price, out mileage)) return null;
                continue;
            }
            if (CostStacks(currency, price) is not { } stacks) return null;
            costs.AddRange(stacks);
        }
        return (costs, quna, mileage);
    }

    async Task<bool> EarnMileageOnQunaSpendAsync(long qunaSpent, object details, CancellationToken ct)
    {
        if (qunaSpent <= 0 || shopFeatures.MileageEarnPercent <= 0 || Store is not { MileageWallet: { } wallet } store || CharacterId is not { } character)
            return false;
        var earn = Math.Max(0, qunaSpent * shopFeatures.MileageEarnPercent / 100);
        if (earn == 0) return false;
        var request = new MileageRequest(store.AccountId, $"earn:{Guid.NewGuid():N}", earn, MileageWalletRules.Earn,
            store.WorldId, character, JsonSerializer.SerializeToElement(details));
        try
        {
            var credited = await wallet.EarnAsync(request, ct);
            NoteMileage(credited);
            return credited.Ok;
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            PersistenceErrors.Add("mileage_earn");
            return false;
        }
    }

    internal async Task<QunaPay> PayMileageThenQunaAsync(long mileage, long quna, object details, Func<QunaRequest?, MileageRequest?, ShopBuyContext?, Task<bool>> purchase, CancellationToken ct, string? purchaseRequestId = null)
    {
        MileageRequest? mileageSpend = null;
        if (mileage > 0)
        {
            if (Store is not { MileageWallet: { } wallet } store || CharacterId is not { } character) return QunaPay.Failed;
            mileageSpend = new MileageRequest(store.AccountId, $"w{store.WorldId}:{character:N}:{Guid.NewGuid():N}", mileage, MileageWalletRules.Purchase,
                store.WorldId, character, JsonSerializer.SerializeToElement(details));
            MileageResult spent;
            try { spent = await wallet.SpendAsync(mileageSpend, ct); }
            catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                PersistenceErrors.Add("mileage_spend");
                return QunaPay.Failed;
            }
            NoteMileage(spent);
            if (!spent.Ok) return spent.Error == MileageWalletRules.Insufficient ? QunaPay.NotEnough : QunaPay.Failed;
        }
        if (quna <= 0)
            return await purchase(null, mileageSpend, new ShopBuyContext(mileageSpend)) ? QunaPay.Done : QunaPay.Failed;
        return await PayQunaAsync(quna, details, async request =>
        {
            var ctx = new ShopBuyContext(mileageSpend, QunaRequestId: request?.RequestId);
            return await purchase(request, mileageSpend, ctx);
        }, ct, purchaseRequestId);
    }
}