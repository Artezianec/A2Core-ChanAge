# Shop feature layer

Extends the working Quna purchase path (docs/QUNA_WALLET.md) with Mileage, mail delivery, gifting, package bonuses, and staff refunds. Configuration: `Aion:Shop` in host settings (`ShopFeatureOptions`).

## Mileage wallet

- `game.mileage_wallets` / `game.mileage_ledger` (migration 140), `game.mileage_apply`.
- Earned on **paid Quna spends** at `MileageEarnPercent` (default **10%**, OURS — no official earn rate captured).
- When `UseMileageCurrency` is true (default), `Mileage_Usable` / `Mileage_Special` SKUs debit Mileage at catalog Mileage prices instead of converting to Quna (`ShopCurrency` policy unchanged when the flag is off).
- HTTP `virtual_currency/v2.0/me/balances` group **158** returns the wallet balance; `RefreshCurrency_NT` category **6** follows pushes.

## Quna ledger kind

Migration 141 adds `quna_kind` (`paid` | `free`) on `game.quna_ledger`: purchases are `paid`; grants, staff, migration and refunds credit as `free`. Purchase idempotency uses migration **139** (`request_id`, `status` pending/done/refunded).

## Mail delivery

When `MailDeliveryEnabled` and the bag is at the shop cap or the SKU is account-bound (`inventory_type` 1), rewards are sent as reward-tab mail (Post.dat **107**) instead of failing the purchase.

## Gifting

- Wire opcode **0x569A / 0x569B** (CANDIDATE — not in `opcode-names.json` traces; layout: goods id, qty, recipient name UTF-8).
- Requires `cp:1` in goods metadata, same server, not self.
- Charges the buyer; delivers to the recipient via mail.
- Staff: `POST /api/admin/shop/gift` validates name and giftable flag.

## Packages

`tools/data/shop_package_tiers.json` (embedded) defines step tiers and first-purchase bonuses; validated against `official_shop` parts at host start.

## Refunds

Staff (`ManageQuna`): `POST /api/admin/shop/accounts/{id}/orders/refund` `{ orderId }` within `RefundWindowHours` (default 168). Only **pending** orders paid with Quna (typically mail delivery) are refundable; returns Quna via `game.quna_staff_refund`.

## Migrations

- **139** — Shop order idempotency (`request_id`, `status`; from main)
- **140** — Mileage wallet
- **141** — Quna kind, deliver_via, packages, gift log