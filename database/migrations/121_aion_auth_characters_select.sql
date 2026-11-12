-- The staff Accounts page (IAccountDirectory, Authentication/StaffAccountEndpoints.cs) reads an account's characters
-- over the account connection: migration 096 granted aion_auth USAGE on schema game, but no table privileges, so
-- account search and account detail failed with "permission denied for table characters".
-- Those queries select from game.characters only. Staff tools, the Quna wallet and the world registry use the game
-- connection (aion_app) and need no further grants here. Do not edit 096; it is already applied in production.
DO $grant$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'aion_auth') THEN
        GRANT USAGE ON SCHEMA game TO aion_auth;
        GRANT SELECT ON game.characters TO aion_auth;
    END IF;
END
$grant$;
