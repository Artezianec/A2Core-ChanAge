-- QA bots: deduplicated stuck buckets, samples, internal account marker, retention.
ALTER TABLE game.characters ADD COLUMN IF NOT EXISTS qa_bot boolean NOT NULL DEFAULT false;
CREATE INDEX characters_qa_bot_idx ON game.characters (qa_bot) WHERE qa_bot;

CREATE TABLE game.qa_stuck_buckets (
    bucket_id bigserial PRIMARY KEY,
    quest_code text NOT NULL,
    quest_step integer NOT NULL,
    reason_code text NOT NULL,
    quest_title text,
    hit_count bigint NOT NULL DEFAULT 0,
    first_seen timestamptz NOT NULL DEFAULT now(),
    last_seen timestamptz NOT NULL DEFAULT now(),
    UNIQUE (quest_code, quest_step, reason_code)
);
CREATE INDEX qa_stuck_buckets_last_idx ON game.qa_stuck_buckets (last_seen DESC);

CREATE TABLE game.qa_stuck_samples (
    sample_id bigserial PRIMARY KEY,
    bucket_id bigint NOT NULL REFERENCES game.qa_stuck_buckets(bucket_id) ON DELETE CASCADE,
    run_id bigint REFERENCES game.qa_runs(run_id) ON DELETE SET NULL,
    bot_index integer NOT NULL DEFAULT 0,
    class_name text NOT NULL,
    character_level integer NOT NULL,
    map_id integer,
    position jsonb,
    reason_detail text,
    last_actions jsonb NOT NULL DEFAULT '[]'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX qa_stuck_samples_bucket_idx ON game.qa_stuck_samples (bucket_id, created_at DESC);

-- Dedicated internal QA accounts (excluded from public listings).
CREATE TABLE game.qa_internal_accounts (
    account_id uuid PRIMARY KEY REFERENCES auth.accounts(account_id) ON DELETE CASCADE,
    created_at timestamptz NOT NULL DEFAULT now()
);