-- Staff dashboard sessions are apart from player sessions (IStaffSessions, Authentication/StaffSessionEndpoints.cs):
-- they need the password, a staff role and an authenticator code. Every /api/admin request needs one; a player
-- session no longer opens the dashboard.
ALTER TABLE auth.sessions ADD COLUMN kind text NOT NULL DEFAULT 'account' CHECK (kind IN ('account','staff'));
CREATE INDEX sessions_staff_idx ON auth.sessions(account_id) WHERE kind='staff';

-- Remember-me duration for sign-in challenges that complete with TOTP (player and staff).
ALTER TABLE auth.totp_login_challenges
    ADD COLUMN session_seconds integer CHECK (session_seconds IS NULL OR session_seconds BETWEEN 60 AND 2592000);

-- A sign-in challenge is for a player sign-in ('account'), a staff sign-in's code step ('staff') or a staff sign-in that
-- sets up an authenticator first ('staff_enroll': staff must use two-step verification).
ALTER TABLE auth.totp_login_challenges
    ADD COLUMN purpose text NOT NULL DEFAULT 'account' CHECK (purpose IN ('account','staff','staff_enroll'));
