# Quna wallet

Quna (the premium currency, item `535300006`) is an **account-wide ledger** in PostgreSQL, not a bag item.
Ported from [KiskEmu](https://github.com/Doxylamin/KiskEmu) (AGPL-3.0) and flattened to our single database.

## Model

- `game.quna_wallets` — one row per account: `balance >= 0`, `last_entry_id`.
- `game.quna_ledger` — append-only (update/delete triggers refuse). Every change is a row with a signed
  `delta`, `balance_after`, `reason` (`purchase`, `grant`, `refund`, `staff`, `migration`), a unique
  `(account_id, request_id)` and JSON `details`.
- `game.quna_apply(account, request, delta, reason, ...)` — the only way to change a balance. It locks the wallet
  row, so concurrent spends never overdraw. A repeated `request_id` returns the first result and changes
  nothing (`repeated = true`). The same id with a different amount or reason is refused (`request_conflict`).
- `game.quna_refund(account, spend_request)` — gives a purchase back once (request `refund:<spend>`).
- `game.quna_absorb_stack(account, instance, world)` — moves one bag stack into the wallet and deletes it in one
  transaction (request `stack:<instance>`), so a stack is credited exactly once.
- `game.quna_staff_apply` / `game.quna_staff_refund` — staff changes with the actor and note in `details`, plus
  one `audit.events` row (`quna_staff_grant`, `quna_staff_take`, `quna_staff_refund`).

## Server

- `IQunaWallet` (`Aion.Application/Gameplay/QunaWallet.cs`), `PostgresQunaWallet` (Infrastructure),
  `MemoryQunaWallet` (tests, development without a database).
- `PushingQunaWallet` (Host): after every applied change, the new balance goes to the account's sessions in the
  world, so the client's Quna display (the virtual bag stack plus `RefreshCurrency_NT 0x3bd1`) follows staff
  grants and refunds immediately. Pushes carry the ledger entry id, so a late push never shows an older balance.
- Shop, battle pass and subscription purchases spend from the wallet first (request id per purchase). If the
  purchase then fails, the spend is refunded.
- The platform balance (`virtual_currency/v2.0/me/balances`) reads the wallet.
- The client still sees Quna as a bag stack. It is **virtual** (not stored); it shows the wallet balance.
- Without a database (development logins), Quna stays a plain bag item, as before.

## Staff dashboard

Accounts → an account → **Quna** tab: the balance, the ledger, add/take with a note, and *Give back* on purchases.

- Viewing the ledger needs `ViewAccounts` (Moderator).
- Changing it needs `ManageQuna` (Administrator by default; see StaffAccess from #484).
- API: `GET /api/admin/accounts/{id}/quna?limit=`, `POST /api/admin/accounts/{id}/quna`
  `{requestId, amount, note}`, `POST /api/admin/accounts/{id}/quna/refund` `{requestId}`.
  The client sends a fresh `requestId` per form, so double-clicks and retries apply once.

## Migration 111 (existing Quna stacks)

`database/migrations/111_account_quna_wallet.sql` creates the tables and, in the same transaction, moves the Quna
stacks that already exist into the wallets:

- Counted: template `535300006` with `quantity > 0` in `bag`, `storage` and `account_storage` for every account,
  summed per account. The ledger gets one `migration` entry per account (request `migration:111:<account>`).
- Then exactly those stacks are deleted.
- Not touched: stacks in trade escrow or buyback. If they come back to a bag later, the game moves them into the
  wallet on the next inventory load (`quna_absorb_stack`, once per stack).
- The migration runs once (tracked in `public.schema_migrations`). Its ledger entries are idempotent too, so the
  amounts can never be counted twice.

Before applying it on a server, back up the database (`pg_dump`).

Migration numbers: 094–096 belong to #484 and 097–110 are reserved for work in progress, so this one is 111.

## Local test

```bash
# Unit and host tests
dotnet test tests/Aion.World.Tests --filter "FullyQualifiedName~QunaWallet"
dotnet test tests/Aion.Host.Tests

# SQL tests (disposable database, local PostgreSQL from tools/postgres_dev.py)
python3 tools/postgres_dev.py setup
AION_POSTGRES_TESTS=1 python3 -m unittest tests.test_postgres_dev.QunaWalletSqlTests -v
```

In the game: buy something in the shop, then check the account's Quna tab on the dashboard. Add Quna there while
the character is online; the display updates without relogging.
