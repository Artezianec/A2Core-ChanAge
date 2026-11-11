-- Review fixes for the account Quna wallet (docs/QUNA_WALLET.md). Do not edit 111: it may already be applied.
-- 1. request_id is unique per account, not globally (the docs already described (account_id, request_id)).
-- 2. A repeated request also compares reason, not only account and delta.
-- 3. game.quna_refund only gives a purchase back (not a staff take), after locking the wallet row.
-- 4. Idempotency lookup stays after the wallet row lock; first-row INSERT ... ON CONFLICT is unchanged.
-- 5. CREATE OR REPLACE keeps the existing signatures and grants.

ALTER TABLE game.quna_ledger DROP CONSTRAINT IF EXISTS quna_ledger_request_id_key;
ALTER TABLE game.quna_ledger DROP CONSTRAINT IF EXISTS quna_ledger_account_request;
ALTER TABLE game.quna_ledger ADD CONSTRAINT quna_ledger_account_request UNIQUE (account_id, request_id);

CREATE OR REPLACE FUNCTION game.quna_apply(p_account uuid, p_request text, p_delta bigint, p_reason text, p_world integer, p_character uuid, p_details jsonb)
RETURNS TABLE (ok boolean, balance bigint, entry_id bigint, error text, repeated boolean)
LANGUAGE plpgsql AS $$
DECLARE
    v_balance bigint;
    v_last bigint;
    v_done game.quna_ledger%ROWTYPE;
BEGIN
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
    INSERT INTO game.quna_ledger (account_id, delta, balance_after, reason, request_id, world_id, character_id, details)
    VALUES (p_account, p_delta, v_balance + p_delta, p_reason, p_request, p_world, p_character, p_details)
    RETURNING * INTO v_done;
    UPDATE game.quna_wallets w SET balance = v_done.balance_after, last_entry_id = v_done.entry_id, updated_at = now()
    WHERE w.account_id = p_account;
    RETURN QUERY SELECT true, v_done.balance_after, v_done.entry_id, NULL::text, false;
END $$;

-- Gives back a purchase (its request id) once: request 'refund:' || p_spend. Locks the wallet so concurrent
-- refunds of the same spend serialise; 'not_found' when there is no purchase spend of this account.
CREATE OR REPLACE FUNCTION game.quna_refund(p_account uuid, p_spend text, p_world integer)
RETURNS TABLE (ok boolean, balance bigint, entry_id bigint, error text, repeated boolean)
LANGUAGE plpgsql AS $$
DECLARE
    v_spend game.quna_ledger%ROWTYPE;
    v_balance bigint;
    v_last bigint;
BEGIN
    SELECT w.balance, w.last_entry_id INTO v_balance, v_last FROM game.quna_wallets w WHERE w.account_id = p_account FOR UPDATE;
    IF NOT FOUND THEN
        RETURN QUERY SELECT false, 0::bigint, 0::bigint, 'not_found'::text, false;
        RETURN;
    END IF;
    SELECT * INTO v_spend FROM game.quna_ledger l
    WHERE l.request_id = p_spend AND l.account_id = p_account AND l.reason = 'purchase' AND l.delta < 0;
    IF NOT FOUND THEN
        RETURN QUERY SELECT false, v_balance, v_last, 'not_found'::text, false;
        RETURN;
    END IF;
    RETURN QUERY SELECT * FROM game.quna_apply(p_account, 'refund:' || p_spend, -v_spend.delta, 'refund', COALESCE(p_world, v_spend.world_id),
        v_spend.character_id, jsonb_build_object('refunds', p_spend));
END $$;

-- Item row already locked (FOR UPDATE OF i) and the character must belong to p_account. Recreate only to keep
-- the body next to the other functions; behaviour is unchanged.
CREATE OR REPLACE FUNCTION game.quna_absorb_stack(p_account uuid, p_instance uuid, p_world integer)
RETURNS TABLE (ok boolean, balance bigint, entry_id bigint, error text, repeated boolean)
LANGUAGE plpgsql AS $$
DECLARE
    v_quantity bigint;
    v_character uuid;
    v_result record;
    v_deleted integer;
BEGIN
    SELECT i.quantity, i.character_id INTO v_quantity, v_character
    FROM game.item_instances i JOIN game.characters c ON c.character_id = i.character_id
    WHERE i.instance_id = p_instance AND i.template_id = 535300006 AND i.location = 'bag' AND i.quantity > 0 AND c.account_id = p_account
    FOR UPDATE OF i;
    IF NOT FOUND THEN
        RETURN QUERY SELECT false, COALESCE((SELECT w.balance FROM game.quna_wallets w WHERE w.account_id = p_account), 0),
            COALESCE((SELECT w.last_entry_id FROM game.quna_wallets w WHERE w.account_id = p_account), 0), 'not_found'::text, false;
        RETURN;
    END IF;
    SELECT * INTO v_result FROM game.quna_apply(p_account, 'stack:' || p_instance::text, v_quantity, 'grant', p_world, v_character,
        jsonb_build_object('source', 'item 535300006', 'instance', p_instance));
    IF v_result.ok THEN
        DELETE FROM game.item_instances WHERE instance_id = p_instance;
        GET DIAGNOSTICS v_deleted = ROW_COUNT;
        IF v_deleted <> 1 AND NOT v_result.repeated THEN
            RAISE EXCEPTION 'quna_absorb_stack: expected to delete 1 item row, deleted %', v_deleted;
        END IF;
    END IF;
    RETURN QUERY SELECT v_result.ok, v_result.balance, v_result.entry_id, v_result.error, v_result.repeated;
END $$;
