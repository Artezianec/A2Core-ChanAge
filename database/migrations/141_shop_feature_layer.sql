-- Shop feature layer on top of migration 139 idempotency (docs/SHOP_FEATURES.md).

ALTER TABLE game.quna_ledger ADD COLUMN IF NOT EXISTS quna_kind text
    CHECK (quna_kind IS NULL OR quna_kind IN ('paid', 'free'));
-- One-time backfill: ledger rows are runtime-append-only; disable only for this migration body.
ALTER TABLE game.quna_ledger DISABLE TRIGGER quna_ledger_append_only;
UPDATE game.quna_ledger SET quna_kind = CASE WHEN reason = 'purchase' THEN 'paid' ELSE 'free' END WHERE quna_kind IS NULL;
ALTER TABLE game.quna_ledger ENABLE TRIGGER quna_ledger_append_only;

CREATE OR REPLACE FUNCTION game.quna_apply(p_account uuid, p_request text, p_delta bigint, p_reason text, p_world integer, p_character uuid, p_details jsonb)
RETURNS TABLE (ok boolean, balance bigint, entry_id bigint, error text, repeated boolean)
LANGUAGE plpgsql AS $$
DECLARE
    v_balance bigint;
    v_last bigint;
    v_done game.quna_ledger%ROWTYPE;
    v_kind text;
BEGIN
    v_kind := CASE WHEN p_reason = 'purchase' THEN 'paid' ELSE 'free' END;
    INSERT INTO game.quna_wallets AS w (account_id) VALUES (p_account) ON CONFLICT (account_id) DO NOTHING;
    SELECT w.balance, w.last_entry_id INTO v_balance, v_last FROM game.quna_wallets w WHERE w.account_id = p_account FOR UPDATE;
    SELECT * INTO v_done FROM game.quna_ledger l WHERE l.account_id = p_account AND l.request_id = p_request;
    IF FOUND THEN
        IF v_done.delta = p_delta AND v_done.reason = p_reason THEN
            RETURN QUERY SELECT true, v_done.balance_after, v_done.entry_id, NULL::text, true;
        ELSE
            RETURN QUERY SELECT false, v_balance, v_last, 'request_conflict'::text, false;
        END IF;
        RETURN;
    END IF;
    IF v_balance + p_delta < 0 THEN
        RETURN QUERY SELECT false, v_balance, v_last, 'insufficient'::text, false;
        RETURN;
    END IF;
    INSERT INTO game.quna_ledger (account_id, delta, balance_after, reason, request_id, world_id, character_id, details, quna_kind)
    VALUES (p_account, p_delta, v_balance + p_delta, p_reason, p_request, p_world, p_character, p_details, v_kind)
    RETURNING * INTO v_done;
    UPDATE game.quna_wallets w SET balance = v_done.balance_after, last_entry_id = v_done.entry_id, updated_at = now()
    WHERE w.account_id = p_account;
    RETURN QUERY SELECT true, v_done.balance_after, v_done.entry_id, NULL::text, false;
END $$;

ALTER TABLE game.shop_orders
    ADD COLUMN IF NOT EXISTS deliver_via text NOT NULL DEFAULT 'bag' CHECK (deliver_via IN ('bag', 'mail')),
    ADD COLUMN IF NOT EXISTS refunded_at timestamptz,
    ADD COLUMN IF NOT EXISTS refund_actor uuid;

CREATE TABLE IF NOT EXISTS game.shop_account_packages (
    account_id uuid NOT NULL REFERENCES auth.accounts(account_id),
    goods_id integer NOT NULL,
    tier_unlocked integer NOT NULL DEFAULT 0 CHECK (tier_unlocked >= 0),
    first_purchase_claimed boolean NOT NULL DEFAULT false,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id, goods_id)
);
GRANT SELECT, INSERT, UPDATE ON game.shop_account_packages TO aion_app;

CREATE TABLE IF NOT EXISTS game.shop_gift_log (
    gift_id bigserial PRIMARY KEY,
    from_character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    to_character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    goods_id integer NOT NULL,
    quantity integer NOT NULL CHECK (quantity > 0),
    quna_request_id text,
    created_at timestamptz NOT NULL DEFAULT now()
);
GRANT SELECT, INSERT ON game.shop_gift_log TO aion_app;
GRANT USAGE, SELECT ON SEQUENCE game.shop_gift_log_gift_id_seq TO aion_app;