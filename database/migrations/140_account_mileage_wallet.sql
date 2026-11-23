-- Account Mileage ledger (second premium-adjacent currency for BM Mileage_* tabs; docs/SHOP_FEATURES.md).
-- Pattern matches game.quna_* (migration 111) but lives in a separate file to avoid conflicting with wallet review work.

CREATE TABLE game.mileage_wallets (
    account_id uuid PRIMARY KEY REFERENCES auth.accounts(account_id),
    balance bigint NOT NULL DEFAULT 0 CHECK (balance >= 0),
    last_entry_id bigint NOT NULL DEFAULT 0,
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE game.mileage_ledger (
    entry_id bigserial PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES game.mileage_wallets(account_id),
    delta bigint NOT NULL CHECK (delta <> 0 AND abs(delta) <= 100000000000),
    balance_after bigint NOT NULL CHECK (balance_after >= 0),
    reason text NOT NULL CHECK (reason IN ('purchase', 'grant', 'earn', 'refund', 'staff', 'migration')),
    request_id text NOT NULL CHECK (length(request_id) BETWEEN 1 AND 200),
    world_id integer,
    character_id uuid,
    details jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    CHECK (reason = 'staff' OR (reason IN ('purchase') = (delta < 0))),
    CONSTRAINT mileage_ledger_account_request UNIQUE (account_id, request_id)
);
CREATE INDEX mileage_ledger_account ON game.mileage_ledger (account_id, entry_id DESC);

CREATE FUNCTION game.mileage_ledger_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'game.mileage_ledger is append-only';
END $$;
CREATE TRIGGER mileage_ledger_append_only BEFORE UPDATE OR DELETE ON game.mileage_ledger
    FOR EACH ROW EXECUTE FUNCTION game.mileage_ledger_append_only();
CREATE TRIGGER mileage_ledger_no_truncate BEFORE TRUNCATE ON game.mileage_ledger
    FOR EACH STATEMENT EXECUTE FUNCTION game.mileage_ledger_append_only();

CREATE FUNCTION game.mileage_apply(p_account uuid, p_request text, p_delta bigint, p_reason text, p_world integer, p_character uuid, p_details jsonb)
RETURNS TABLE (ok boolean, balance bigint, entry_id bigint, error text, repeated boolean)
LANGUAGE plpgsql AS $$
DECLARE
    v_balance bigint;
    v_last bigint;
    v_done game.mileage_ledger%ROWTYPE;
BEGIN
    INSERT INTO game.mileage_wallets AS w (account_id) VALUES (p_account) ON CONFLICT (account_id) DO NOTHING;
    SELECT w.balance, w.last_entry_id INTO v_balance, v_last FROM game.mileage_wallets w WHERE w.account_id = p_account FOR UPDATE;
    SELECT * INTO v_done FROM game.mileage_ledger l WHERE l.account_id = p_account AND l.request_id = p_request;
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
    INSERT INTO game.mileage_ledger (account_id, delta, balance_after, reason, request_id, world_id, character_id, details)
    VALUES (p_account, p_delta, v_balance + p_delta, p_reason, p_request, p_world, p_character, p_details)
    RETURNING * INTO v_done;
    UPDATE game.mileage_wallets w SET balance = v_done.balance_after, last_entry_id = v_done.entry_id, updated_at = now()
    WHERE w.account_id = p_account;
    RETURN QUERY SELECT true, v_done.balance_after, v_done.entry_id, NULL::text, false;
END $$;

GRANT SELECT, INSERT, UPDATE ON game.mileage_wallets TO aion_app;
GRANT SELECT, INSERT ON game.mileage_ledger TO aion_app;
GRANT USAGE, SELECT ON SEQUENCE game.mileage_ledger_entry_id_seq TO aion_app;
GRANT EXECUTE ON FUNCTION game.mileage_apply(uuid, text, bigint, text, integer, uuid, jsonb) TO aion_app;