-- Staff standing in the world is per character (migration 035), so granting a staff role on the dashboard left an
-- existing character a player: the name tag, builder commands and the Login_RS admin flag all need characters.staff.
-- Granting a role now also flags the account's existing characters, as migration 035 did for the staff accounts it
-- found. Characters created later stay players until flagged by hand; the flag can still be switched off per character.
-- The change applies when the player next enters the world (the session reads the flag when a character is selected).
CREATE OR REPLACE FUNCTION auth.set_staff_role(p_session bytea,p_target uuid,p_role text,p_granted boolean) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;
BEGIN
    IF p_role IS NULL OR p_role NOT IN ('gamemaster','moderator','admin') OR p_granted IS NULL THEN RETURN false; END IF;
    SELECT s.account_id INTO actor FROM auth.sessions s JOIN auth.accounts a USING(account_id)
      JOIN auth.account_roles r USING(account_id)
      WHERE s.token_hash=p_session AND s.kind='staff' AND s.expires_at>now() AND a.disabled_at IS NULL AND r.role='admin';
    IF actor IS NULL OR (actor=p_target AND p_role='admin' AND NOT p_granted) THEN RETURN false; END IF;
    PERFORM 1 FROM auth.accounts WHERE account_id=p_target AND disabled_at IS NULL FOR UPDATE;
    IF NOT FOUND THEN RETURN false; END IF;
    IF p_granted THEN
        INSERT INTO auth.account_roles VALUES(p_target,p_role) ON CONFLICT DO NOTHING;
        UPDATE game.characters SET staff=true WHERE account_id=p_target AND NOT staff;
    ELSE
        DELETE FROM auth.account_roles WHERE account_id=p_target AND role=p_role;
        DELETE FROM auth.sessions WHERE account_id=p_target AND kind='staff';
    END IF;
    INSERT INTO audit.events(action,actor_id,details)
      VALUES(CASE WHEN p_granted THEN 'role_granted' ELSE 'role_revoked' END,actor,
             jsonb_build_object('target',p_target,'role',p_role));
    RETURN true;
END $$;
