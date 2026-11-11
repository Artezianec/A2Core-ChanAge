-- Custom NPCs (staff dashboard: NPCs). Server-placed NPCs that talk through a merchant's
-- interaction; every world on this database reads them, places them and runs their dialogs.
-- template_id is the NPC's own NpcData row (2999000-2999999, above every official id), which a
-- client patch pak can add with the custom name and dialog lines; base_template_id is the official
-- row it copies (its look, and what players without the pak see). Dialog progress is per character
-- (game.character_settings "custom_npc_flags"). Ported from KiskEmu (AGPL-3.0); their migration 102.
CREATE TABLE game.custom_npcs (
    template_id integer PRIMARY KEY CHECK (template_id BETWEEN 2999000 AND 2999999),
    slug text NOT NULL UNIQUE CHECK (slug ~ '^[A-Za-z0-9]{1,32}$'),
    base_template_id integer NOT NULL CHECK (base_template_id BETWEEN 2000000 AND 2998999),
    -- {"en-US": "...", "de-DE": "..."}
    name jsonb NOT NULL CHECK (jsonb_typeof(name) = 'object' AND name ? 'en-US'),
    -- [{"mapId", "x", "y", "z", "yaw"}]
    placements jsonb NOT NULL DEFAULT '[]' CHECK (jsonb_typeof(placements) = 'array' AND jsonb_array_length(placements) <= 20),
    -- [{"key", "lines": [{"en-US": ...}], "when": {...}, "actions": [...]}] (Aion.Application CustomNpcNode)
    nodes jsonb NOT NULL CHECK (jsonb_typeof(nodes) = 'array' AND jsonb_array_length(nodes) BETWEEN 1 AND 20),
    enabled boolean NOT NULL DEFAULT true,
    updated_at timestamptz NOT NULL DEFAULT now(),
    updated_by uuid
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.custom_npcs TO aion_app;
