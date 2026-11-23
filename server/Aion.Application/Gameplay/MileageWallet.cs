using System.Text.Json;

namespace Aion.Application.Gameplay;

/// <summary>
/// Account Mileage for BM Mileage_Usable / Mileage_Special tabs (docs/SHOP_FEATURES.md). Separate from Quna; earned on
/// Quna spending at a configurable rate.
/// </summary>
public interface IMileageWallet
{
    Task<MileageResult> BalanceAsync(Guid account, CancellationToken ct = default);
    Task<MileageResult> SpendAsync(MileageRequest request, CancellationToken ct = default);
    Task<MileageResult> GrantAsync(MileageRequest request, CancellationToken ct = default);
    Task<MileageResult> EarnAsync(MileageRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<MileageLedgerEntry>> LedgerAsync(Guid account, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MileageLedgerEntry>>([]);
    bool AcceptsGrants => true;
}

public sealed record MileageRequest(Guid AccountId, string RequestId, long Amount, string Reason, int? WorldId = null, Guid? CharacterId = null,
    JsonElement? Details = null);

public sealed record MileageResult(bool Ok, long Balance, long EntryId = 0, string? Error = null, bool Repeated = false);

public sealed record MileageLedgerEntry(long EntryId, Guid AccountId, long Delta, long BalanceAfter, string Reason, string RequestId,
    int? WorldId, Guid? CharacterId, JsonElement? Details, DateTimeOffset At);

public static class MileageWalletRules
{
    public const string Purchase = "purchase", Grant = "grant", Earn = "earn", Refund = "refund", Staff = "staff", Migration = "migration";
    public const string Insufficient = "insufficient", InvalidAmount = "invalid_amount", InvalidReason = "invalid_reason",
        InvalidRequest = "invalid_request", RequestConflict = "request_conflict";
    public const long MaxAmount = 100_000_000_000;

    public static string? Invalid(MileageRequest request, bool debit)
    {
        if (request.AccountId == Guid.Empty || string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 200) return InvalidRequest;
        if (request.Amount is <= 0 or > MaxAmount) return InvalidAmount;
        var allowed = debit ? request.Reason is Purchase or Staff : request.Reason is Grant or Earn or Staff or Migration;
        return allowed ? null : InvalidReason;
    }
}

public sealed class MemoryMileageWallet : IMileageWallet
{
    readonly object gate = new();
    readonly Dictionary<Guid, (long Balance, long Last)> wallets = [];
    readonly List<MileageLedgerEntry> ledger = [];

    public event Action<Guid, long, long>? Changed;

    public Task<IReadOnlyList<MileageLedgerEntry>> LedgerAsync(Guid account, int limit, CancellationToken ct = default)
    {
        lock (gate)
            return Task.FromResult<IReadOnlyList<MileageLedgerEntry>>([.. ledger.Where(e => e.AccountId == account).Reverse().Take(Math.Max(0, limit))]);
    }

    public Task<MileageResult> BalanceAsync(Guid account, CancellationToken ct = default)
    {
        lock (gate)
        {
            var (balance, last) = wallets.GetValueOrDefault(account);
            return Task.FromResult(new MileageResult(true, balance, last));
        }
    }

    public Task<MileageResult> SpendAsync(MileageRequest request, CancellationToken ct = default) =>
        Task.FromResult(MileageWalletRules.Invalid(request, debit: true) is { } error ? Refused(request.AccountId, error) : Apply(request, -request.Amount));

    public Task<MileageResult> GrantAsync(MileageRequest request, CancellationToken ct = default) =>
        Task.FromResult(MileageWalletRules.Invalid(request, debit: false) is { } error ? Refused(request.AccountId, error) : Apply(request, request.Amount));

    public Task<MileageResult> EarnAsync(MileageRequest request, CancellationToken ct = default) =>
        GrantAsync(request with { Reason = MileageWalletRules.Earn }, ct);

    public long Seed(Guid account, long amount) =>
        Apply(new MileageRequest(account, "seed:" + Guid.NewGuid().ToString("N"), amount, MileageWalletRules.Migration), amount).Balance;

    MileageResult Refused(Guid account, string error)
    {
        lock (gate)
        {
            var (balance, last) = wallets.GetValueOrDefault(account);
            return new MileageResult(false, balance, last, error);
        }
    }

    MileageResult Apply(MileageRequest request, long delta)
    {
        MileageResult result;
        lock (gate)
        {
            if (ledger.FirstOrDefault(e => e.AccountId == request.AccountId && e.RequestId == request.RequestId) is { } done)
                return done.Delta == delta && done.Reason == request.Reason
                    ? new MileageResult(true, done.BalanceAfter, done.EntryId, Repeated: true)
                    : new MileageResult(false, wallets.GetValueOrDefault(request.AccountId).Balance, wallets.GetValueOrDefault(request.AccountId).Last, MileageWalletRules.RequestConflict);
            var (balance, last) = wallets.GetValueOrDefault(request.AccountId);
            if (balance + delta < 0) return new MileageResult(false, balance, last, MileageWalletRules.Insufficient);
            var entry = new MileageLedgerEntry(ledger.Count + 1, request.AccountId, delta, balance + delta, request.Reason, request.RequestId,
                request.WorldId, request.CharacterId, request.Details, DateTimeOffset.UtcNow);
            ledger.Add(entry);
            wallets[request.AccountId] = (entry.BalanceAfter, entry.EntryId);
            result = new MileageResult(true, entry.BalanceAfter, entry.EntryId);
        }
        Changed?.Invoke(request.AccountId, result.Balance, result.EntryId);
        return result;
    }
}