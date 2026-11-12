-- Shop purchase ledger and memberships, per world (ported from KiskEmu c25c2317 / 094_shop_orders.world.sql).

CREATE TABLE game.shop_orders (
    order_id bigserial PRIMARY KEY,
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    account_id uuid NOT NULL,
    kind text NOT NULL CHECK (kind IN ('npc', 'coin', 'guild', 'bm', 'custom', 'pass', 'membership')),
    goods_id integer NOT NULL,
    quantity integer NOT NULL CHECK (quantity > 0),
    currency_item integer NOT NULL,
    price bigint NOT NULL CHECK (price >= 0),
    ordered_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX shop_orders_ordered_at ON game.shop_orders (ordered_at DESC);
CREATE INDEX shop_orders_character ON game.shop_orders (character_id, ordered_at DESC);
GRANT SELECT, INSERT, DELETE ON game.shop_orders TO aion_app;
GRANT USAGE, SELECT ON SEQUENCE game.shop_orders_order_id_seq TO aion_app;

CREATE TABLE game.account_memberships (
    account_id uuid PRIMARY KEY,
    starts_at timestamptz NOT NULL,
    ends_at timestamptz NOT NULL,
    revoked boolean NOT NULL DEFAULT false,
    updated_at timestamptz NOT NULL DEFAULT now()
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.account_memberships TO aion_app;
