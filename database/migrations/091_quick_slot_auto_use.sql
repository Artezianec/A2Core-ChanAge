-- QuickSlotInfo._toggle_yn: auto-use on consumable hotbar slots (F1–F8, wire slots 11–18).
ALTER TABLE game.quick_slots
    ADD COLUMN IF NOT EXISTS auto_use boolean NOT NULL DEFAULT false;
