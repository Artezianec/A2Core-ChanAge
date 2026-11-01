-- Per-character inventory / warehouse slot extension counts (ItemDepotList_RS tail, ItemContainerSlotExtend).
ALTER TABLE game.characters
    ADD COLUMN inventory_slot_extend_by_item smallint NOT NULL DEFAULT 0,
    ADD COLUMN inventory_slot_extend_by_gold smallint NOT NULL DEFAULT 0,
    ADD COLUMN storage_slot_extend smallint NOT NULL DEFAULT 0,
    ADD COLUMN storage_slot_extend_by_item smallint NOT NULL DEFAULT 0,
    ADD COLUMN character_storage_slot_extend_by_gold smallint NOT NULL DEFAULT 0,
    ADD COLUMN character_storage_slot_extend_by_item smallint NOT NULL DEFAULT 0;
