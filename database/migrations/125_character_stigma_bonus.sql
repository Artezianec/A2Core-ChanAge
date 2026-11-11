-- Bonus Stigma Shard points from items, shops and staff grants (docs/STIGMAS.md).
-- Separate from levelling income so a lower ShardRate cannot trim purchased progress.
-- Numbered 125: 124 left for a parallel field-boss branch.
CREATE TABLE game.character_stigma_bonus (
    character_id uuid PRIMARY KEY REFERENCES game.characters(character_id) ON DELETE CASCADE,
    shards integer NOT NULL CHECK (shards >= 0),
    updated_at timestamptz NOT NULL DEFAULT now()
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_stigma_bonus TO aion_app;
