-- QA bots: runtime grants for aion_app and SECURITY DEFINER account provisioning (#541 prod).

GRANT SELECT, INSERT, UPDATE, DELETE ON
    game.qa_runs,
    game.qa_stuck_reports,
    game.qa_stuck_buckets,
    game.qa_stuck_samples,
    game.qa_internal_accounts
TO aion_app;

GRANT USAGE, SELECT ON SEQUENCE
    game.qa_runs_run_id_seq,
    game.qa_stuck_reports_report_id_seq,
    game.qa_stuck_buckets_bucket_id_seq,
    game.qa_stuck_samples_sample_id_seq
TO aion_app;

-- aion_app cannot INSERT into auth.accounts; provision QA slots with definer rights (same pattern as staff tools / quna).
CREATE OR REPLACE FUNCTION game.qa_ensure_internal_account(p_slot integer, p_account_id uuid, p_subject text)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, auth, game
AS $$
BEGIN
    IF p_slot < 0 OR p_slot > 127 OR p_subject IS NULL OR length(p_subject) = 0 THEN
        RAISE EXCEPTION 'invalid_qa_internal_account';
    END IF;
    INSERT INTO auth.accounts (account_id, provider, subject)
    VALUES (p_account_id, 'qa-bot', p_subject)
    ON CONFLICT (account_id) DO NOTHING;
    INSERT INTO game.qa_internal_accounts (account_id, slot_index)
    VALUES (p_account_id, p_slot)
    ON CONFLICT (account_id) DO UPDATE SET slot_index = EXCLUDED.slot_index;
END;
$$;
REVOKE ALL ON FUNCTION game.qa_ensure_internal_account(integer, uuid, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION game.qa_ensure_internal_account(integer, uuid, text) TO aion_app;