-- Equipped wing persisted across relog (WingChange_RQ 0x5674); 0 = none / the race default for ascended characters.
-- Per-wing enchant levels already live in game.character_wings.enchant_level (012); the wing list sends them.
ALTER TABLE game.characters
    ADD COLUMN IF NOT EXISTS equipped_wing_id integer NOT NULL DEFAULT 0 CHECK (equipped_wing_id >= 0);
