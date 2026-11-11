-- Quna (NC GameCash, the paid currency) moves from item 535300006 stacks to one wallet per account with an append-only
-- ledger (docs/QUNA_WALLET.md). Every change is a ledger entry; request_id makes a repeated call return the first result.
-- Ported from KiskEmu (AGPL-3.0) 095_account_quna.realm.sql, flattened to A2Core's single database: the wallet sits next
-- to the items, so the existing stacks move into it in this same transaction (end of file).
-- Numbered 111: 094-096 are the staff ranks (#484), 097-110 are kept for other work in flight.

CREATE TABLE game.quna_wallets (
    account_id uuid PRIMARY KEY REFERENCES auth.accounts(account_id),
    balance bigint NOT NULL DEFAULT 0 CHECK (balance >= 0),
    -- The account's newest ledger entry: a world keeps the balance with the highest entry when answers and pushes cross.
    last_entry_id bigint NOT NULL DEFAULT 0,
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE game.quna_ledger (
    entry_id bigserial PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES game.quna_wallets(account_id),
    delta bigint NOT NULL CHECK (delta <> 0 AND abs(delta) <= 100000000000),
    balance_after bigint NOT NULL CHECK (balance_after >= 0),
    reason text NOT NULL CHECK (reason IN ('purchase', 'grant', 'refund', 'staff', 'migration')),
    request_id text NOT NULL UNIQUE CHECK (length(request_id) BETWEEN 1 AND 200),
    -- The world and character the change came from (null for staff and migration changes).
    world_id integer,
    character_id uuid,
    details jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    -- A purchase takes, a grant, refund or migration adds; a staff change may do either.
    CHECK (reason = 'staff' OR (reason = 'purchase') = (delta < 0))
);
CREATE INDEX quna_ledger_account ON game.quna_ledger (account_id, entry_id DESC);

-- Append-only: entries are never changed or removed (a mistake is corrected with a new entry).
CREATE FUNCTION game.quna_ledger_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'game.quna_ledger is append-only';
END $$;
CREATE TRIGGER quna_ledger_append_only BEFORE UPDATE OR DELETE ON game.quna_ledger
    FOR EACH ROW EXECUTE FUNCTION game.quna_ledger_append_only();
CREATE TRIGGER quna_ledger_no_truncate BEFORE TRUNCATE ON game.quna_ledger
    FOR EACH STATEMENT EXECUTE FUNCTION game.quna_ledger_append_only();

-- One change of an account's balance: p_delta < 0 takes (refused with 'insufficient' when the balance is lower), > 0 adds.
-- The same p_request again returns the first result (repeated = true); with another account or amount it is refused
-- ('request_conflict'). Changes of one account are serialised on its wallet row.
CREATE FUNCTION game.quna_apply(p_account uuid, p_request text, p_delta bigint, p_reason text, p_world integer, p_character uuid, p_details jsonb)
RETURNS TABLE (ok boolean, balance bigint, entry_id bigint, error text, repeated boolean)
LANGUAGE plpgsql AS $$
DECLARE
    v_balance bigint;
    v_last bigint;
    v_done game.quna_ledger%ROWTYPE;
BEGIN
    INSERT INTO game.quna_wallets AS w (account_id) VALUES (p_account) ON CONFLICT (account_id) DO NOTHING;
    SELECT w.balance, w.last_entry_id INTO v_balance, v_last FROM game.quna_wallets w WHERE w.account_id = p_account FOR UPDATE;
    SELECT * INTO v_done FROM game.quna_ledger l WHERE l.request_id = p_request;
    IF FOUND THEN
        IF v_done.account_id = p_account AND v_done.delta = p_delta THEN
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

-- Gives back a purchase (its request id) once: request 'refund:' || p_spend, 'not_found' when there is no such spend
-- of this account.
CREATE FUNCTION game.quna_refund(p_account uuid, p_spend text, p_world integer)
RETURNS TABLE (ok boolean, balance bigint, entry_id bigint, error text, repeated boolean)
LANGUAGE plpgsql AS $$
DECLARE
    v_spend game.quna_ledger%ROWTYPE;
BEGIN
    SELECT * INTO v_spend FROM game.quna_ledger l WHERE l.request_id = p_spend AND l.account_id = p_account AND l.delta < 0;
    IF NOT FOUND THEN
        RETURN QUERY SELECT false, COALESCE((SELECT w.balance FROM game.quna_wallets w WHERE w.account_id = p_account), 0),
            COALESCE((SELECT w.last_entry_id FROM game.quna_wallets w WHERE w.account_id = p_account), 0), 'not_found'::text, false;
        RETURN;
    END IF;
    RETURN QUERY SELECT * FROM game.quna_apply(p_account, 'refund:' || p_spend, -v_spend.delta, 'refund', COALESCE(p_world, v_spend.world_id),
        v_spend.character_id, jsonb_build_object('refunds', p_spend));
END $$;

-- A Quna item stack that reaches a bag after this migration (mail, a reward, back from trade escrow) moves into the
-- wallet: credited with request 'stack:' || instance and deleted in the same transaction, so it is credited once.
-- Only a stack of one of p_account's characters, in a bag.
CREATE FUNCTION game.quna_absorb_stack(p_account uuid, p_instance uuid, p_world integer)
RETURNS TABLE (ok boolean, balance bigint, entry_id bigint, error text, repeated boolean)
LANGUAGE plpgsql AS $$
DECLARE
    v_quantity bigint;
    v_character uuid;
    v_result record;
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
    END IF;
    RETURN QUERY SELECT v_result.ok, v_result.balance, v_result.entry_id, v_result.error, v_result.repeated;
END $$;

-- Staff changes (the dashboard's account page, StaffPermission.ManageQuna): the ledger entry (reason 'staff', the actor and
-- note in details) and an audit.events row commit together. A repeated request writes no second audit row.
CREATE FUNCTION game.quna_staff_apply(p_actor uuid, p_account uuid, p_request text, p_delta bigint, p_note text)
RETURNS TABLE (ok boolean, balance bigint, entry_id bigint, error text, repeated boolean)
LANGUAGE plpgsql AS $$
DECLARE
    v_result record;
BEGIN
    SELECT * INTO v_result FROM game.quna_apply(p_account, p_request, p_delta, 'staff', NULL, NULL,
        jsonb_build_object('actor', p_actor, 'note', COALESCE(p_note, '')));
    IF v_result.ok AND NOT v_result.repeated THEN
        INSERT INTO audit.events(action, actor_id, details) VALUES (CASE WHEN p_delta > 0 THEN 'quna_staff_grant' ELSE 'quna_staff_take' END, p_actor,
            jsonb_build_object('account', p_account, 'amount', abs(p_delta), 'balance', v_result.balance, 'request', p_request,
                'entry', v_result.entry_id, 'note', COALESCE(p_note, '')));
    END IF;
    RETURN QUERY SELECT v_result.ok, v_result.balance, v_result.entry_id, v_result.error, v_result.repeated;
END $$;

CREATE FUNCTION game.quna_staff_refund(p_actor uuid, p_account uuid, p_spend text)
RETURNS TABLE (ok boolean, balance bigint, entry_id bigint, error text, repeated boolean)
LANGUAGE plpgsql AS $$
DECLARE
    v_result record;
BEGIN
    SELECT * INTO v_result FROM game.quna_refund(p_account, p_spend, NULL);
    IF v_result.ok AND NOT v_result.repeated THEN
        INSERT INTO audit.events(action, actor_id, details) VALUES ('quna_staff_refund', p_actor,
            jsonb_build_object('account', p_account, 'refunds', p_spend, 'balance', v_result.balance, 'entry', v_result.entry_id));
    END IF;
    RETURN QUERY SELECT v_result.ok, v_result.balance, v_result.entry_id, v_result.error, v_result.repeated;
END $$;

GRANT SELECT, INSERT, UPDATE ON game.quna_wallets TO aion_app;
GRANT SELECT, INSERT ON game.quna_ledger TO aion_app;
GRANT USAGE, SELECT ON SEQUENCE game.quna_ledger_entry_id_seq TO aion_app;
GRANT EXECUTE ON FUNCTION game.quna_apply(uuid, text, bigint, text, integer, uuid, jsonb) TO aion_app;
GRANT EXECUTE ON FUNCTION game.quna_refund(uuid, text, integer) TO aion_app;
GRANT EXECUTE ON FUNCTION game.quna_absorb_stack(uuid, uuid, integer) TO aion_app;
GRANT EXECUTE ON FUNCTION game.quna_staff_apply(uuid, uuid, text, bigint, text) TO aion_app;
GRANT EXECUTE ON FUNCTION game.quna_staff_refund(uuid, uuid, text) TO aion_app;

-- Staff read an account's ledger on the dashboard (the host checks the staff permission first).
CREATE INDEX quna_ledger_created ON game.quna_ledger (created_at DESC);

-- One-time move of the existing Quna stacks (the old balance: game.item_instances of item 535300006 in a bag, a warehouse
-- or the account warehouse of the account's characters). Per account one 'migration' entry for the stacks' sum with the
-- fixed request id 'migration:111:<account>', so a rerun credits nothing twice; then exactly the credited stacks are
-- deleted (their grant records go with them, ON DELETE CASCADE). Everything commits with the migration or not at all.
-- Stacks elsewhere (trade escrow, buyback) stay items; when one comes back to a bag the world credits it at the owner's
-- next inventory load (request 'stack:<instance>:<revision>').
DO $quna$
DECLARE
    v_account uuid;
    v_amount numeric;
    v_stacks integer;
    v_result record;
BEGIN
    FOR v_account, v_amount, v_stacks IN
        SELECT s.account_id, sum(s.quantity), count(*)::integer FROM (
            SELECT COALESCE(i.account_storage_owner, c.account_id) AS account_id, i.quantity
            FROM game.item_instances i LEFT JOIN game.characters c ON c.character_id = i.character_id
            WHERE i.template_id = 535300006 AND i.quantity > 0 AND i.location IN ('bag', 'storage', 'account_storage')
        ) s
        WHERE s.account_id IN (SELECT a.account_id FROM auth.accounts a)
        GROUP BY s.account_id ORDER BY s.account_id
    LOOP
        IF v_amount > 100000000000 THEN
            RAISE EXCEPTION 'Quna migration: account % holds % Quna, above one ledger entry''s limit', v_account, v_amount;
        END IF;
        SELECT * INTO v_result FROM game.quna_apply(v_account, 'migration:111:' || v_account::text, v_amount::bigint, 'migration', NULL, NULL,
            jsonb_build_object('source', 'item 535300006', 'stacks', v_stacks, 'migration', '111'));
        IF NOT v_result.ok THEN
            RAISE EXCEPTION 'Quna migration of account % refused: %', v_account, v_result.error;
        END IF;
        -- Exactly the stacks just credited (same transaction: nothing changed them in between).
        DELETE FROM game.item_instances i
        WHERE i.template_id = 535300006 AND i.quantity > 0 AND i.location IN ('bag', 'storage', 'account_storage')
          AND (i.account_storage_owner = v_account
               OR (i.account_storage_owner IS NULL AND i.character_id IN (SELECT c.character_id FROM game.characters c WHERE c.account_id = v_account)));
    END LOOP;
END $quna$;
