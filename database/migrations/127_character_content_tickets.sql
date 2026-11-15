-- Entry tickets (ContentsTicket.dat ids, tools/data/content-tickets.json): the count a character had at updated_at. The
-- server adds the daily / weekly / cycle recharges passed since then when it loads the row (ContentTicketTables).
-- No row: the ticket is full. Ported from Addiboii/A2Core (update-26262663, migration 065, commit 87afb91) and
-- renumbered 127. It replaces the per-period counts of migration 045 (character_expedition_tickets, kept, no longer used).
CREATE TABLE IF NOT EXISTS game.character_content_tickets (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    ticket_id integer NOT NULL,
    remaining integer NOT NULL CHECK (remaining >= 0),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, ticket_id)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_content_tickets TO aion_app;
