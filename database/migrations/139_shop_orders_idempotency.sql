-- Idempotent BM / Quna shop purchases: one request_id per account, status tracks completion.

ALTER TABLE game.shop_orders
    ADD COLUMN request_id text CHECK (request_id IS NULL OR length(request_id) BETWEEN 1 AND 200),
    ADD COLUMN status text NOT NULL DEFAULT 'done' CHECK (status IN ('pending', 'done', 'refunded'));

CREATE UNIQUE INDEX shop_orders_account_request ON game.shop_orders (account_id, request_id) WHERE request_id IS NOT NULL;

-- Denormalized account on limit rows (owner_id may be character or account scope).
ALTER TABLE game.shop_purchases ADD COLUMN account_id uuid;
UPDATE game.shop_purchases sp
    SET account_id = c.account_id
    FROM game.characters c
    WHERE sp.owner_id = c.character_id AND sp.account_id IS NULL;
UPDATE game.shop_purchases SET account_id = owner_id WHERE account_id IS NULL;