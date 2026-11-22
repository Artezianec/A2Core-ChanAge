-- QA quest bots (docs/QA_BOTS.md): automated quest playthrough runs and stuck reports for staff.
CREATE TABLE game.qa_runs (
    run_id bigserial PRIMARY KEY,
    started_at timestamptz NOT NULL DEFAULT now(),
    finished_at timestamptz,
    status text NOT NULL DEFAULT 'running' CHECK (status IN ('running', 'stopped', 'completed', 'failed')),
    class_name text NOT NULL,
    level_from integer NOT NULL,
    level_to integer NOT NULL,
    bot_count integer NOT NULL DEFAULT 1,
    world_id integer,
    quests_rewarded integer NOT NULL DEFAULT 0,
    stuck_count integer NOT NULL DEFAULT 0,
    started_by text,
    note text
);
CREATE INDEX qa_runs_started_idx ON game.qa_runs (started_at DESC);

CREATE TABLE game.qa_stuck_reports (
    report_id bigserial PRIMARY KEY,
    run_id bigint NOT NULL REFERENCES game.qa_runs(run_id) ON DELETE CASCADE,
    bot_index integer NOT NULL DEFAULT 0,
    quest_code text NOT NULL,
    quest_title text,
    quest_step integer NOT NULL,
    class_name text NOT NULL,
    character_level integer NOT NULL,
    map_id integer,
    position jsonb,
    reason_code text NOT NULL,
    reason_detail text,
    last_actions jsonb NOT NULL DEFAULT '[]'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX qa_stuck_run_idx ON game.qa_stuck_reports (run_id, created_at);
CREATE INDEX qa_stuck_quest_idx ON game.qa_stuck_reports (quest_code, quest_step, reason_code);