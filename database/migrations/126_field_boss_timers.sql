-- Field boss respawn timers (docs/FIELD-BOSSES.md). Ported from KiskEmu (AGPL-3.0, commit d54d69472d,
-- database/migrations/104_field_boss_timers.world.sql) and renumbered 126: 125 is the stigma bonus,
-- 124 was left unused. Per world: the last kill of each official WorldMapFieldNamed boss
-- (tools/data/field-bosses.json) and its announced respawn, so a restart keeps a dead boss dead until
-- its time; and an optional staff respawn range in minutes (NULL: the server's default). A row whose
-- respawn_at has passed is a boss that is up. world_id is ours: this host shares one database across
-- worlds (migration 120), where KiskEmu stored timers in a per-world database.
CREATE TABLE IF NOT EXISTS game.field_bosses (
    world_id integer NOT NULL,
    code integer NOT NULL,
    killed_at timestamptz,
    respawn_at timestamptz,
    min_minutes integer CHECK (min_minutes BETWEEN 1 AND 10080),
    max_minutes integer CHECK (max_minutes BETWEEN 1 AND 10080),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (world_id, code),
    CHECK ((min_minutes IS NULL) = (max_minutes IS NULL) AND (min_minutes IS NULL OR min_minutes <= max_minutes))
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.field_bosses TO aion_app;
