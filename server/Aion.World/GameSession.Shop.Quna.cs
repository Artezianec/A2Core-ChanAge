using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Quna (GameCash) is the account's wallet (docs/QUNA_WALLET.md, migration 111), not an item: every spend, refund and grant
// goes to Store.QunaWallet. The client still expects it as a bag stack of item 535300006 on 0x5611 and in the shop
// packets, so the bag shows a virtual stack (never stored) whose count is the wallet balance, refreshed after a spend or
// grant and when the host pushes a change (PushingQunaWallet). Without a wallet (development logins) Quna stays a plain
// bag item as before. Ported from KiskEmu (AGPL-3.0).
// Kinah is bag item 930100001 on 0x5611 (GoldCombined). The Quna counter (top left, Currency Info) reads neither the bag nor
// RefreshCurrency_NT: it is the currency_id 103 (GameCashPending) entry of the HTTP balances answer, ShopQunaBalanceService.JsonBody.
// HTTP GET virtual_currency/v2.0/me/balances uses currency_id 109 and 103 with currency_group_id 145 (GameCash), 110 with 158 (Mileage).
// RefreshCurrency_NT (0x3bd1) map keys in usmap are ECurrencyCategory (7 GameCash, 6 Mileage), not the HTTP currency_id field.
public sealed partial class GameSession
{
    public const ushort RefreshCurrencyNt = 0x3bd1;

    public const int QunaBmCurrencyGroupId = 145;
    public const int QunaVirtualCurrencyId = 109;
    public const int MileageCurrencyGroupId = 158;
    public const int MileageVirtualCurrencyId = 110;
    public const int GameCashCurrencyCategory = 7;
    public const int MileageCurrencyCategory = 6;
    public const int GoldCombinedCurrencyCategory = 93;

    /// <summary>The bag key of the virtual Quna stack (no stored item has it: wire ids are small sequence numbers).</summary>
    public const long VirtualQunaKey = 0x7F00_0000_1FE8_07A6;
    static readonly Guid VirtualQunaInstance = new("00000000-0000-0000-0000-00001fe807a6");

    long accountQunaBalance;
    // The ledger entry of accountQunaBalance: a push or answer with an older entry is stale and ignored.
    long qunaEntry = -1;
    // The balance the client was last shown (depot list or RefreshCurrency_NT); null before the first inventory load.
    long? shownQuna;

    /// <summary>Quna is the account wallet's (an account login on a host with the game database).</summary>
    bool QunaInWallet => Store?.QunaWallet is not null;

    public long QunaBalance() => QunaInWallet ? accountQunaBalance : QunaIn(inventory.Values);

    static long QunaIn(IEnumerable<InventoryItem> items) =>
        items.Where(i => i.ItemId == PlatformGoodsTables.Embedded.QunaItemId && i.Container == InventoryItem.Bag).Sum(i => i.Count);

    static bool IsQunaRow(ItemState row) => row.TemplateId == PlatformGoodsTables.Embedded.QunaItemId;

    /// <summary>Takes a wallet answer's balance unless a newer one (higher ledger entry) is already known.</summary>
    void NoteQuna(QunaResult result)
    {
        if (result.EntryId < qunaEntry) return;
        accountQunaBalance = result.Balance;
        qunaEntry = result.EntryId;
    }

    /// <summary>Reads the wallet balance (the first inventory load, and when the client asks for its inventory).</summary>
    async Task RefreshAccountQunaBalanceAsync(CancellationToken ct)
    {
        if (Store is not { QunaWallet: { } wallet } store) return;
        try { NoteQuna(await wallet.BalanceAsync(store.AccountId, ct)); }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The wallet is not reachable: the last known balance stays (spends still ask the wallet, a push or the next
            // inventory request reads it again); not asked again on every inventory change meanwhile.
            PersistenceErrors.Add("quna_balance");
            qunaEntry = Math.Max(qunaEntry, 0);
        }
    }

    /// <summary>
    /// Quna item stacks that reach a bag after migration 111 (a mail attachment or reward that still names the item, a
    /// stack back from trade escrow) go into the wallet: the store credits the stack and deletes it in one transaction
    /// (request "stack:&lt;instance&gt;"), so nothing is credited twice. A store or wallet that cannot (a linked world
    /// server, which may not add Quna) leaves the stack; it is then neither shown nor spent. Returns the rows to use.
    /// </summary>
    async Task<IReadOnlyList<ItemState>> SweepQunaStacksAsync(INativeCharacterStore store, Guid character,
        IReadOnlyList<ItemState> rows, CancellationToken ct)
    {
        var swept = false;
        foreach (var row in rows.Where(r => IsQunaRow(r) && r.Location == "bag" && r.Quantity > 0))
        {
            try
            {
                if (await store.AbsorbQunaStackAsync(character, row.InstanceId, ct) is not { } credited) break;
                if (!credited.Ok) continue;
                NoteQuna(credited);
                swept = true;
            }
            catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                PersistenceErrors.Add("quna_sweep");
            }
        }
        return swept ? await store.InventoryAsync(character, ct) : rows;
    }

    /// <summary>The stored rows plus the virtual Quna stack (when Quna is the wallet's and the balance is above 0).</summary>
    List<ItemState> WithVirtualQuna(IEnumerable<ItemState> rows)
    {
        var list = QunaInWallet ? rows.Where(r => !IsQunaRow(r)).ToList() : rows.ToList();
        if (QunaInWallet && accountQunaBalance > 0)
        {
            var empty = JsonSerializer.SerializeToElement(new { });
            list.Add(new ItemState(VirtualQunaInstance, "", PlatformGoodsTables.Embedded.QunaItemId, accountQunaBalance, "bag", null, 0, empty, empty, VirtualQunaKey));
        }
        return list;
    }

    public byte[] RefreshCurrency(int currencyGroupId, params (int MapKey, long Balance)[] entries)
    {
        var w = PacketWriter.Opcode(RefreshCurrencyNt).I32(currencyGroupId).Varint((uint)entries.Length);
        foreach (var (key, balance) in entries) w.I32(key).I64(balance);
        return w.ToArray();
    }

    byte[] RefreshGameCash(long balance) =>
        RefreshCurrency(QunaBmCurrencyGroupId, (GameCashCurrencyCategory, balance));

    byte[] RefreshMileage(long balance = 0) =>
        RefreshCurrency(MileageCurrencyGroupId, (MileageCurrencyCategory, balance));

    public IEnumerable<byte[]> InventoryDepotSyncPackets()
    {
        if (Store is null || CharacterId is null) yield break;
        var level = EquipItemLevel();
        yield return GamePackets.ItemDepotList([.. inventory.Values], containerSlotExtends.ToWireCounts());
        yield return GamePackets.ItemEquipLevelChange(level, level);
    }

    public IEnumerable<byte[]> QunaRefreshPackets()
    {
        foreach (var packet in InventoryDepotSyncPackets()) yield return packet;
        yield return QunaRefreshNow();
        yield return RefreshMileage();
    }

    /// <summary>RefreshCurrency_NT when the balance differs from what the client was last shown.</summary>
    List<byte[]> QunaRefreshIfChanged()
    {
        var balance = QunaBalance();
        if (shownQuna == balance) return [];
        shownQuna = balance;
        return [RefreshGameCash(balance)];
    }

    public byte[] QunaRefreshNow()
    {
        shownQuna = QunaBalance();
        return RefreshGameCash(shownQuna.Value);
    }

    public string WireAccountId => loginIdentity.AccountSid;

    /// <summary>The items as the client was last shown them (tests).</summary>
    internal IReadOnlyCollection<InventoryItem> ShownItems => inventory.Values;

    /// <summary>
    /// The host pushed this account's balance (its wallet changed: a staff grant, a refund, a purchase by another session):
    /// the virtual stack and RefreshCurrency_NT follow when it is newer than the balance known here.
    /// </summary>
    public async Task<List<byte[]>> QunaPushedAsync(long balance, long entryId, CancellationToken ct = default)
    {
        if (!QunaInWallet || entryId < qunaEntry) return [];
        accountQunaBalance = balance;
        qunaEntry = entryId;
        return CharacterId is null ? [] : await InventoryChangesAsync(ct);
    }

    enum QunaPay { Done, NotEnough, Failed }

    /// <summary>A store call through <see cref="Stored"/>: false when it refused or failed.</summary>
    async Task<bool> StoredOk(Func<Task<bool>> action) => (await Stored(async () => await action() ? [[]] : [])).Count > 0;

    /// <summary>
    /// A purchase's costs: the bag stacks of every currency but Quna, and the Quna to take from the wallet (Quna is a bag
    /// stack only without a wallet). Null when the bag cannot cover the other currencies.
    /// </summary>
    (List<(Guid Item, long Revision, int Count)> Costs, long Quna)? PurchaseCosts(IEnumerable<(int Currency, long Price)> prices)
    {
        var costs = new List<(Guid, long, int)>();
        var quna = 0L;
        foreach (var (currency, price) in prices)
        {
            if (price <= 0) continue;
            if (QunaInWallet && currency == PlatformGoodsTables.Embedded.QunaItemId)
            {
                if (!PacketSecurity.TryAdd(quna, price, out quna)) return null;
                continue;
            }
            if (CostStacks(currency, price) is not { } stacks) return null;
            costs.AddRange(stacks);
        }
        // Not checked against the balance known here: the wallet decides (this session may not have heard of a grant yet).
        return (costs, quna);
    }

    /// <summary>
    /// Runs the purchase. When it costs Quna, the debit is passed into the purchase so a Postgres ShopBuy can take it
    /// in the same transaction as the items (a crash or a refused grant rolls both back). A leftover spend is refunded
    /// as a safety net (the in-memory wallet, or a store that debits before a later failure). Without Quna, only the
    /// purchase runs.
    /// </summary>
    async Task<QunaPay> PayQunaAsync(long quna, object details, Func<Task<bool>> purchase, CancellationToken ct) =>
        await PayQunaAsync(quna, details, _ => purchase(), ct, debitInPurchase: false);

    /// <summary>Like <see cref="PayQunaAsync(long, object, Func{Task{bool}}, CancellationToken)"/>, but the purchase receives the spend and must debit it itself.</summary>
    async Task<QunaPay> PayQunaAsync(long quna, object details, Func<QunaRequest?, Task<bool>> purchase, CancellationToken ct) =>
        await PayQunaAsync(quna, details, purchase, ct, debitInPurchase: true);

    async Task<QunaPay> PayQunaAsync(long quna, object details, Func<QunaRequest?, Task<bool>> purchase, CancellationToken ct, bool debitInPurchase)
    {
        if (quna <= 0) return await purchase(null) ? QunaPay.Done : QunaPay.Failed;
        if (Store is not { QunaWallet: { } wallet } store || CharacterId is not { } character) return QunaPay.Failed;
        var request = new QunaRequest(store.AccountId, $"w{store.WorldId}:{character:N}:{Guid.NewGuid():N}", quna, QunaWalletRules.Purchase,
            store.WorldId, character, JsonSerializer.SerializeToElement(details));
        if (!debitInPurchase)
        {
            QunaResult spent;
            try { spent = await wallet.SpendAsync(request, ct); }
            catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                PersistenceErrors.Add("quna_spend");
                await RefundQunaAsync(wallet, store.AccountId, request.RequestId, store.WorldId, ct);
                return QunaPay.Failed;
            }
            NoteQuna(spent);
            if (!spent.Ok) return spent.Error == QunaWalletRules.Insufficient ? QunaPay.NotEnough : QunaPay.Failed;
            DebugShop($"Quna spent: {quna} (request {request.RequestId}, balance {spent.Balance})");
        }
        var done = false;
        try { done = await purchase(request); }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            PersistenceErrors.Add("quna_spend");
            await RefundQunaAsync(wallet, store.AccountId, request.RequestId, store.WorldId, ct);
            return QunaPay.Failed;
        }
        if (!done) await RefundQunaAsync(wallet, store.AccountId, request.RequestId, store.WorldId, ct);
        try { NoteQuna(await wallet.BalanceAsync(store.AccountId, ct)); }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        { PersistenceErrors.Add("quna_balance"); }
        if (done)
        {
            DebugShop($"Quna purchase: {quna} (request {request.RequestId}, balance {accountQunaBalance})");
            return QunaPay.Done;
        }
        return accountQunaBalance < quna ? QunaPay.NotEnough : QunaPay.Failed;
    }

    async Task RefundQunaAsync(IQunaWallet wallet, Guid account, string request, int world, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var refunded = await wallet.RefundAsync(account, request, world, CancellationToken.None);
                NoteQuna(refunded);
                if (refunded.Ok) DebugShop($"Quna refunded: request {request}, balance {refunded.Balance}");
                return;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // The ledger keeps the spend (its request id) for staff to refund by hand.
                if (attempt == 1) PersistenceErrors.Add("quna_refund");
            }
        }
    }

    /// <summary>
    /// A GM's grant of Quna to their own account's wallet (GrantShopCurrency quna, AcquireItem 535300006): reason "staff",
    /// the GM's account and character in the ledger; the packets show it. Refused where this process may not add Quna (a
    /// linked world server). Null when Quna is not a wallet here (development logins: the caller grants the item).
    /// </summary>
    public async Task<(bool Ok, string Message, List<byte[]> Packets)?> GrantQunaAsync(long amount, CancellationToken ct)
    {
        if (Store is not { QunaWallet: { } wallet } store) return null;
        if (!wallet.AcceptsGrants) return (false, "Quna is added on the staff dashboard (a character's account), not in a world.", []);
        var details = JsonSerializer.SerializeToElement(new { command = "GrantShopCurrency", actor = store.AccountId });
        QunaResult granted;
        try
        {
            granted = await wallet.GrantAsync(new QunaRequest(store.AccountId, $"gm:{Guid.NewGuid():N}", amount, QunaWalletRules.Staff,
                store.WorldId, CharacterId, details), ct);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (false, "The Quna wallet could not be reached; no Quna was granted.", []);
        }
        if (!granted.Ok) return (false, $"Quna not granted: {granted.Error}.", []);
        NoteQuna(granted);
        return (true, $"Granted {amount} Quna (balance {granted.Balance}).", CharacterId is null ? [] : await InventoryChangesAsync(ct));
    }
}
