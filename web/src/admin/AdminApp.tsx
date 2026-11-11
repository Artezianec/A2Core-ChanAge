// Staff dashboard: live overview with the game's maps, characters (level/class/roles, items), announcements,
// the activity log and server health, through the audited /api/admin endpoints. The server checks roles on every
// request; this page only hides controls. "Keep me signed in" keeps the session token in localStorage (a 30-day
// session); otherwise it lives in sessionStorage until the tab closes.
import { useCallback, useEffect, useState } from 'react';
import { api, ApiError } from '../api';
import { Button, Icon, type StatusMessage } from '../ui/components';
import { Activity } from './Activity';
import { Broadcast } from './Broadcast';
import { NewAccountDialog } from './NewAccount';
import { Dashboard } from './Dashboard';
import { Economy } from './Economy';
import { Events } from './Events';
import { Moderation } from './Moderation';
import { Overview } from './Overview';
import { Rewards } from './Rewards';
import { ServerView } from './Server';
import { SignIn } from './SignIn';
import { Home } from './Home';
import { AccountList } from './Accounts';
import { Worlds } from './Worlds';
import { ERRORS, ROLE_LABEL, can, type StaffProfile } from './types';

export function describe(error: unknown): string {
  if (!(error instanceof ApiError)) return 'Something went wrong.';
  if (error.status === 0) return 'The server could not be reached.';
  if (error.status === 429) return 'Too many requests. Wait a minute and try again.';
  return (error.code && ERRORS[error.code]) || 'The server could not complete this request.';
}

export interface Session { token: string; username: string; profile: StaffProfile }
export type Call = <T>(path: string, options?: { method?: string; body?: unknown }) => Promise<T>;
type View = 'dashboard' | 'overview' | 'accounts' | 'characters' | 'moderation' | 'broadcast' | 'rewards' | 'economy' | 'activity' | 'worlds' | 'server' | 'staff';

const STORE_KEY = 'aion-staff-session';
interface Saved { token: string; username: string }

function readSaved(): Saved | null {
  for (const store of [() => window.localStorage, () => window.sessionStorage]) {
    try {
      const raw = store().getItem(STORE_KEY);
      if (raw) {
        const saved = JSON.parse(raw) as Saved;
        if (typeof saved.token === 'string' && typeof saved.username === 'string') return saved;
      }
    } catch { /* storage blocked: sign in each time */ }
  }
  return null;
}
function writeSaved(saved: Saved | null, remember = false) {
  for (const [store, keep] of [[() => window.localStorage, remember], [() => window.sessionStorage, !remember]] as const) {
    try {
      if (saved && keep) store().setItem(STORE_KEY, JSON.stringify(saved));
      else store().removeItem(STORE_KEY);
    } catch { /* ignore */ }
  }
}

interface NavEntry { id: View; label: string; icon: string; permission?: string }
const NAV: { name: string; items: NavEntry[] }[] = [
  { name: 'Overview', items: [
    { id: 'dashboard', label: 'Dashboard', icon: 'overview', permission: 'viewDashboard' },
    { id: 'overview', label: 'Live map', icon: 'map', permission: 'viewDashboard' },
  ] },
  { name: 'Players', items: [
    { id: 'accounts', label: 'Accounts', icon: 'players', permission: 'viewAccounts' },
    { id: 'characters', label: 'Characters', icon: 'players', permission: 'viewDashboard' },
    { id: 'moderation', label: 'Moderation', icon: 'shield', permission: 'handleTickets' },
  ] },
  { name: 'Game', items: [
    { id: 'broadcast', label: 'Announcements & events', icon: 'broadcast', permission: 'announce' },
    { id: 'rewards', label: 'Rewards', icon: 'gift', permission: 'rewardCharacters' },
    { id: 'economy', label: 'Economy', icon: 'coins', permission: 'viewEconomy' },
  ] },
  { name: 'System', items: [
    { id: 'worlds', label: 'Worlds', icon: 'globe', permission: 'viewDashboard' },
    { id: 'server', label: 'Server', icon: 'server' },
    { id: 'staff', label: 'Staff', icon: 'shield', permission: 'viewAccounts' },
    { id: 'activity', label: 'Activity log', icon: 'activity', permission: 'viewAudit' },
  ] },
];

export function AdminApp() {
  const [session, setSession] = useState<Session | null>(null);
  const [restoring, setRestoring] = useState(() => readSaved() !== null);
  const [message, setMessage] = useState<StatusMessage | null>(null);
  const [creating, setCreating] = useState(false);
  const [view, setView] = useState<View>('dashboard');
  const [focus, setFocus] = useState<{ id: string; n: number } | null>(null);

  // A remembered session: still valid and still staff?
  useEffect(() => {
    const saved = readSaved();
    if (!saved) return;
    api<StaffProfile>('/api/admin/me', { token: saved.token })
      .then(profile => setSession({ ...saved, profile }))
      .catch(() => writeSaved(null))
      .finally(() => setRestoring(false));
  }, []);

  const signedOut = useCallback((text = '') => { writeSaved(null); setSession(null); setMessage(text ? { text, kind: '' } : null); }, []);
  const call: Call = useCallback(async (path, options = {}) => {
    try { return await api(path, { ...options, token: session?.token }); }
    catch (error) {
      if (error instanceof ApiError && error.status === 401) signedOut('Your session ended. Sign in again.');
      throw error;
    }
  }, [session, signedOut]);

  async function logout() {
    await api('/api/admin/wardrobe-access', { method: 'DELETE' }).catch(() => {});
    if (session) await api('/api/admin/session', { method: 'DELETE', token: session.token }).catch(() => {});
    signedOut('Signed out.');
  }

  const openCharacter = (id: string) => { setFocus(f => ({ id, n: (f?.n ?? 0) + 1 })); setView('characters'); };

  if (!session) {
    return (
      <div className="app app-signin">
        {restoring ? <main className="signin"><p className="muted">Signing in…</p></main>
          : <SignIn message={message} onSignedIn={(s, remember) => { writeSaved({ token: s.token, username: s.username }, remember); setSession(s); }} onMessage={setMessage} />}
      </div>
    );
  }

  const allowed = (permission: string) => can(session.profile, permission);
  const visible = NAV.map(g => ({ ...g, items: g.items.filter(i => !i.permission || allowed(i.permission)) })).filter(g => g.items.length > 0);
  const isAdmin = allowed('manageAccounts');
  const current = visible.some(g => g.items.some(i => i.id === view)) ? view : visible[0]?.items[0]?.id ?? 'characters';
  return (
    <div className="app app-shell">
      <aside className="sidebar">
        <div className="brand">
          <span className="brand-mark" aria-hidden="true" />
          <div>
            <strong>Staff</strong>
            <span className="muted small block">Game administration</span>
          </div>
        </div>
        <nav className="nav" aria-label="Dashboard">
          {visible.map(g => (
            <div className="nav-group" key={g.name}>
              <p className="nav-heading">{g.name}</p>
              {g.items.map(item => (
                <button key={item.id} type="button" className="nav-item" aria-current={current === item.id ? 'page' : undefined} onClick={() => setView(item.id)}>
                  <Icon name={item.icon} /> <span>{item.label}</span>
                </button>
              ))}
            </div>
          ))}
        </nav>
        <div className="sidebar-foot">
          {isAdmin && <Button size="sm" block onClick={() => setCreating(true)}><Icon name="plus" size={16} /> New account</Button>}
          <div className="me">
            <span className="avatar" aria-hidden="true">{session.username.slice(0, 1).toUpperCase()}</span>
            <div className="grow">
              <span className="truncate block">{session.username}</span>
              <span className="small accent">{session.profile.rankLabel ?? session.profile.roles.map(r => ROLE_LABEL[r] ?? r).join(' · ')}</span>
            </div>
            <button type="button" className="icon-btn" onClick={logout} title="Sign out" aria-label="Sign out"><Icon name="logout" /></button>
          </div>
        </div>
      </aside>
      <div className="content">
        <div className="view" hidden={current !== 'dashboard'}><Home call={call} profile={session.profile} active={current === 'dashboard'} /></div>
        <div className="view" hidden={current !== 'overview'}>
          <Overview call={call} token={session.token} active={current === 'overview'} onOpen={openCharacter} />
        </div>
        <div className="view" hidden={current !== 'accounts'}>
          <AccountList call={call} profile={session.profile} active={current === 'accounts'} onOpenCharacter={openCharacter} />
        </div>
        <div className="view" hidden={current !== 'characters'}><Dashboard session={session} call={call} focus={focus} /></div>
        <div className="view" hidden={current !== 'moderation'}><Moderation call={call} active={current === 'moderation'} /></div>
        <div className="view" hidden={current !== 'broadcast'}>
          <div className="page-stack"><Broadcast call={call} active={current === 'broadcast'} /><div className="page page-tail"><Events call={call} active={current === 'broadcast'} /></div></div>
        </div>
        <div className="view" hidden={current !== 'rewards'}><Rewards call={call} isAdmin={allowed('rewardEveryone')} /></div>
        <div className="view" hidden={current !== 'economy'}><Economy call={call} active={current === 'economy'} onOpen={openCharacter} /></div>
        <div className="view" hidden={current !== 'activity'}><Activity call={call} active={current === 'activity'} onOpen={openCharacter} /></div>
        <div className="view" hidden={current !== 'server'}><ServerView call={call} token={session.token} active={current === 'server'} isAdmin={isAdmin} /></div>
        <div className="view" hidden={current !== 'staff'}>
          <AccountList call={call} profile={session.profile} active={current === 'staff'} staffOnly onOpenCharacter={openCharacter} />
        </div>
        <div className="view" hidden={current !== 'worlds'}><Worlds call={call} active={current === 'worlds'} /></div>
      </div>
      <NewAccountDialog open={creating} call={call} onClose={() => setCreating(false)} />
    </div>
  );
}

