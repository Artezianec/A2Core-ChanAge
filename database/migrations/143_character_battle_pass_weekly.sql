-- Daeva Pass weekly EXP accrual (BattlePassInfoList _period_exp).
CREATE TABLE game.character_battle_pass_weekly (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    group_id integer NOT NULL,
    week_start_ms bigint NOT NULL,
    exp integer NOT NULL CHECK (exp >= 0) DEFAULT 0,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, group_id)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_battle_pass_weekly TO aion_app;