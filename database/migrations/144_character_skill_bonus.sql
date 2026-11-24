-- Bonus skill points from items (Wisdom Stone / GetSkillPoint) and staff grants.
CREATE TABLE game.character_skill_bonus (
    character_id uuid PRIMARY KEY REFERENCES game.characters(character_id) ON DELETE CASCADE,
    points integer NOT NULL CHECK (points >= 0),
    updated_at timestamptz NOT NULL DEFAULT now()
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_skill_bonus TO aion_app;