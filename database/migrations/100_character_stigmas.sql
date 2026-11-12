-- Stigmas (docs/STIGMAS.md; ported from ckrealm/A2Core 043_character_stigmas.sql, AGPL-3.0): the stigma skills a character
-- bought with Stigma Shards and their stigma levels (1..20). Kept apart from game.character_skills, which the level trigger
-- manages: a stigma is never unlocked by level. Shards are not stored: a character has had a fixed number at its level
-- (tools/data/stigmas.json) and has spent what its stigma levels cost. Stigma slots are quick slots holding a stigma skill.
-- Numbered 100 to leave 097-099 to parallel branches.
CREATE TABLE game.character_stigmas (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    skill_id integer NOT NULL CHECK (skill_id > 0),
    level smallint NOT NULL CHECK (level BETWEEN 1 AND 20),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, skill_id)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_stigmas TO aion_app;
