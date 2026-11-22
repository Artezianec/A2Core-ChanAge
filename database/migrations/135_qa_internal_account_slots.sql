-- QA internal accounts: slot_index for pool provisioning (#541).
-- Idempotent when migration 134 already applied (with or without slot_index from an earlier draft).

ALTER TABLE game.qa_internal_accounts ADD COLUMN IF NOT EXISTS slot_index integer NOT NULL DEFAULT 0;

-- Multiple rows at slot_index 0 (default) would block the unique index; assign stable slots by age.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM game.qa_internal_accounts
        GROUP BY slot_index HAVING count(*) > 1
    ) THEN
        UPDATE game.qa_internal_accounts AS a
        SET slot_index = ranked.slot
        FROM (
            SELECT account_id,
                   (row_number() OVER (ORDER BY created_at, account_id) - 1)::integer AS slot
            FROM game.qa_internal_accounts
        ) AS ranked
        WHERE a.account_id = ranked.account_id;
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS qa_internal_accounts_slot_idx ON game.qa_internal_accounts (slot_index);