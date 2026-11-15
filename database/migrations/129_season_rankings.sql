-- Season rankings: the ranking points per character, ESeasonContentsType and live season (SeasonSchedule Season_Main_Live
-- number), and the final results of an ended season (MyPrevSeasonRanking_NT, TakePrevSeasonRankingReward_RQ).
-- point: the ranking point (boss challenge / Transcendence / Ascension trial: the season's clear points summed; Abyss: the
-- Abyss ranking point; arena: the rating); best_point and extra: the best clear (the lists' order and the display JSON).
-- Ported from zaraza87/A2Core dell-merge-2026-11 (29934bea); no backfill (the old boss challenge points used another formula).
CREATE TABLE game.season_ranking_points (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    contents_type integer NOT NULL,
    season_no integer NOT NULL,
    point bigint NOT NULL DEFAULT 0,
    best_point bigint NOT NULL DEFAULT 0,
    extra text NOT NULL DEFAULT '',
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, contents_type, season_no)
);
CREATE INDEX season_ranking_points_order_idx ON game.season_ranking_points (contents_type, season_no, best_point DESC, point DESC);
-- rank / total: the whole list; class_rank / class_total: the character's class list (the class rewards); grade per list.
CREATE TABLE game.season_ranking_results (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    contents_type integer NOT NULL,
    season_no integer NOT NULL,
    class integer NOT NULL,
    rank integer NOT NULL,
    total integer NOT NULL,
    class_rank integer NOT NULL,
    class_total integer NOT NULL,
    point bigint NOT NULL,
    extra text NOT NULL DEFAULT '',
    grade integer NOT NULL,
    class_grade integer NOT NULL,
    grade_display integer NOT NULL,
    reward_taken boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, contents_type, season_no)
);
CREATE INDEX season_ranking_results_season_idx ON game.season_ranking_results (season_no, contents_type);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.season_ranking_points, game.season_ranking_results TO aion_app;
