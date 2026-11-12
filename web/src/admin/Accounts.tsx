// Accounts: search, detail, sanctions and tickets; staff tools (sessions, password, disable, TOTP, ranks).
// Server: StaffAccountEndpoints.cs and GameplayEndpoints.cs. The Quna tab is the account wallet (migration 111).
import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError, requestId } from '../api';
import { Badge, Button, ConfirmDialog, Status, Tabs, type ConfirmRequest, type StatusMessage } from '../ui/components';
import { describe, type Call } from './AdminApp';
import { ROLE_LABEL, STAFF_ROLES, can, type StaffProfile } from './types';

export interface AccountSummary {
  accountId: string; username: string; createdAt: string; disabledAt: string | null; lastSignInAt: string | null;
  roles: string[]; rankLabel: string | null;
}
interface AccountPage { accounts: AccountSummary[]; total: number; page: number; pageSize: number }
interface AccountDetail {
  account: AccountSummary; totpEnabled: boolean; sessions: number; staffSessions: number; quna: number | null;
  characters: { characterId: string; worldId: number; name: string; level: number; race: number; lastLoginAt: string | null }[];
  sanctions: { id: number; kind: string; reason: string; createdAt: string; expiresAt: string | null; createdBy: string | null; revokedAt: string | null; active: boolean }[];
  tickets: { id: number; createdAt: string; category: string; content: string; status: string; staffNote: string; handledAt: string | null }[];
}
type Filter = 'all' | 'staff' | 'disabled';

function formatWhen(iso: string | null | undefined, short = false): string {
  if (!iso) return '—';
  const d = new Date(iso);
  return short ? d.toLocaleString() : d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}

export function AccountList({ call, profile, active, staffOnly = false, onOpenCharacter }:
  { call: Call; profile: StaffProfile; active: boolean; staffOnly?: boolean; onOpenCharacter?: (characterId: string) => void }) {
  const [query, setQuery] = useState('');
  const [submitted, setSubmitted] = useState('');
  const [filter, setFilter] = useState<Filter>(staffOnly ? 'staff' : 'all');
  const [page, setPage] = useState(0);
  const [data, setData] = useState<AccountPage | null>(null);
  const [message, setMessage] = useState<StatusMessage | null>(null);
  const [selected, setSelected] = useState<string | null>(null);

  useEffect(() => { if (!active) setSelected(null); }, [active]);

  useEffect(() => {
    if (!active || selected) return;
    let live = true;
    const params = new URLSearchParams({ filter: staffOnly ? 'staff' : filter, page: String(page) });
    if (submitted) params.set('q', submitted);
    call<AccountPage>('/api/admin/accounts?' + params)
      .then(result => { if (live) { setData(result); setMessage(null); } })
      .catch(error => { if (live) setMessage({ text: describe(error), kind: 'error' }); });
    return () => { live = false; };
  }, [call, active, submitted, filter, page, staffOnly, selected]);

  if (selected) {
    return (
      <AccountView call={call} profile={profile} accountId={selected} staffOnly={staffOnly}
        onBack={() => setSelected(null)} onOpenCharacter={onOpenCharacter} />
    );
  }

  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  return (
    <main className="page">
      <header className="page-head">
        <div>
          <h1>{staffOnly ? 'Staff' : 'Accounts'}</h1>
          <p className="muted">
            {staffOnly ? 'Accounts with a staff rank. Change a rank on the account page.' : 'Search by account or character name.'}
          </p>
        </div>
      </header>
      <form className="row wrap" onSubmit={e => { e.preventDefault(); setPage(0); setSubmitted(query.trim()); }}>
        <input className="input search-input" type="search" placeholder="Account or character name" aria-label="Search accounts" maxLength={64}
          value={query} onChange={e => setQuery(e.target.value)} />
        <Button type="submit">Search</Button>
        {!staffOnly && (
          <div className="segmented-sm" role="group" aria-label="Show">
            {(['all', 'staff', 'disabled'] as Filter[]).map(f => (
              <button key={f} type="button" aria-pressed={filter === f} onClick={() => { setFilter(f); setPage(0); }}>
                {f === 'all' ? 'All' : f === 'staff' ? 'Staff' : 'Disabled'}
              </button>
            ))}
          </div>
        )}
      </form>
      <Status message={message} />
      <section className="card">
        <div className="panel-head">
          <h2>{data ? `${data.total.toLocaleString()} ${data.total === 1 ? 'account' : 'accounts'}` : 'Accounts'}</h2>
          {pages > 1 && (
            <div className="row">
              <Button size="sm" disabled={page === 0} onClick={() => setPage(p => p - 1)}>Previous</Button>
              <span className="small muted">Page {page + 1} of {pages}</span>
              <Button size="sm" disabled={page + 1 >= pages} onClick={() => setPage(p => p + 1)}>Next</Button>
            </div>
          )}
        </div>
        {data && data.accounts.length === 0 ? <p className="empty pad">No accounts match.</p>
          : (
            <div className="table-wrap">
              <table className="data-table">
                <thead><tr><th scope="col">Account</th><th scope="col">Rank</th><th scope="col">Status</th><th scope="col">Created</th><th scope="col">Latest session</th></tr></thead>
                <tbody>
                  {data?.accounts.map(a => (
                    <tr key={a.accountId}>
                      <td>
                        <button type="button" className="link strong" onClick={() => setSelected(a.accountId)}>{a.username}</button>
                      </td>
                      <td>{a.rankLabel ?? <span className="muted">Player</span>}</td>
                      <td>{a.disabledAt ? <Badge tone="danger">Disabled</Badge> : <Badge tone="ok">Active</Badge>}</td>
                      <td className="nowrap">{formatWhen(a.createdAt)}</td>
                      <td className="nowrap muted">{formatWhen(a.lastSignInAt, true)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
      </section>
    </main>
  );
}

type Tab = 'overview' | 'characters' | 'quna' | 'sanctions' | 'tickets';

function AccountView({ call, profile, accountId, staffOnly, onBack, onOpenCharacter }:
  { call: Call; profile: StaffProfile; accountId: string; staffOnly: boolean; onBack: () => void; onOpenCharacter?: (id: string) => void }) {
  const [detail, setDetail] = useState<AccountDetail | null>(null);
  const [message, setMessage] = useState<StatusMessage | null>(null);
  const [tab, setTab] = useState<Tab>('overview');
  const [confirm, setConfirm] = useState<ConfirmRequest | null>(null);
  const [password, setPassword] = useState<string | null>(null);
  const [roleOpen, setRoleOpen] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);

  const load = useCallback(() => call<AccountDetail>(`/api/admin/accounts/${accountId}`)
    .then(d => { setDetail(d); })
    .catch(error => setMessage({ text: error instanceof ApiError && error.status === 404 ? 'There is no such account.' : describe(error), kind: 'error' })), [call, accountId]);
  useEffect(() => { setDetail(null); setTab('overview'); setPassword(null); load(); }, [load]);

  async function act(name: string, action: () => Promise<void>, done: string) {
    setBusy(name); setMessage(null);
    try { await action(); setMessage({ text: done, kind: 'ok' }); await load(); }
    catch (error) {
      setMessage({ text: error instanceof ApiError && error.status === 403 ? 'Your staff rank does not allow this for this account.' : describe(error), kind: 'error' });
    } finally { setBusy(null); }
  }

  if (!detail) {
    return (
      <main className="page">
        <p className="breadcrumb"><button type="button" className="link" onClick={onBack}>{staffOnly ? 'Staff' : 'Accounts'}</button></p>
        <Status message={message ?? { text: 'Loading…', kind: '' }} />
      </main>
    );
  }
  const a = detail.account;
  const staffTarget = a.roles.length > 0;
  const canModerate = can(profile, 'disableAccounts') && (!staffTarget || can(profile, 'manageStaff'));
  const canManage = can(profile, 'manageAccounts');
  const activeSanctions = detail.sanctions.filter(s => s.active);
  const openTickets = detail.tickets.filter(t => t.status === 'open').length;

  return (
    <main className="page">
      <nav aria-label="Breadcrumb" className="breadcrumb row">
        <button type="button" className="link" onClick={onBack}>{staffOnly ? 'Staff' : 'Accounts'}</button>
        <span aria-hidden="true">/</span>
        <span>{a.username}</span>
      </nav>
      <header className="page-head">
        <div>
          <h1>{a.username}</h1>
          <div className="row wrap">
            {a.disabledAt ? <Badge tone="danger">Disabled {formatWhen(a.disabledAt)}</Badge> : <Badge tone="ok">Active</Badge>}
            <Badge>{a.rankLabel ?? 'Player'}</Badge>
            <Badge tone={detail.totpEnabled ? 'ok' : undefined}>{detail.totpEnabled ? 'Two-step on' : 'Two-step off'}</Badge>
            {activeSanctions.map(s => (
              <Badge key={s.id} tone="accent">{sanctionLabel(s.kind)}{s.expiresAt ? ` until ${formatWhen(s.expiresAt)}` : ''}</Badge>
            ))}
          </div>
        </div>
        {!a.disabledAt && (
          <div className="row wrap">
            {canModerate && (
              <Button busy={busy === 'revoke'} onClick={() => setConfirm({
                title: 'End every session', body: `Sign ${a.username} out on every device?`, confirmLabel: 'End sessions',
                onConfirm: () => act('revoke', () => call(`/api/admin/accounts/${a.accountId}/sessions/revoke`, { method: 'POST' }), 'Every session of this account has ended.'),
              })}>End sessions</Button>
            )}
            {canManage && (
              <Button busy={busy === 'password'} onClick={() => setConfirm({
                title: 'Temporary password', body: `Give ${a.username} a new random password? They are signed out everywhere.`,
                note: 'You see the new password once. Pass it on privately.',
                confirmLabel: 'Make a password',
                onConfirm: () => act('password', async () => {
                  const r = await call<{ temporaryPassword: string }>(`/api/admin/accounts/${a.accountId}/password`, { method: 'POST' });
                  setPassword(r.temporaryPassword);
                }, 'The password was changed.'),
              })}>Temporary password</Button>
            )}
            {canModerate && (
              <Button variant="danger" busy={busy === 'disable'} onClick={() => setConfirm({
                title: 'Disable account', danger: true, body: `Disable ${a.username}? They cannot sign in or play.`, confirmLabel: 'Disable account',
                onConfirm: () => act('disable', () => call(`/api/admin/accounts/${a.accountId}/disable`, { method: 'POST' }), 'The account is disabled.'),
              })}>Disable account</Button>
            )}
          </div>
        )}
      </header>
      {password && (
        <div className="notice pad">
          <p><strong>Temporary password for {a.username}:</strong> <code className="select-all">{password}</code></p>
          <p className="small muted">Shown once. Pass it on privately.</p>
          <Button size="sm" onClick={() => setPassword(null)}>Done</Button>
        </div>
      )}
      <Status message={message} />
      <Tabs label="Account" value={tab} onChange={setTab} tabs={[
        { id: 'overview', label: 'Overview' },
        { id: 'characters', label: `Characters · ${detail.characters.length}` },
        ...(detail.quna === null ? [] : [{ id: 'quna' as Tab, label: 'Quna' }]),
        { id: 'sanctions', label: `Sanctions · ${detail.sanctions.length}` },
        { id: 'tickets', label: `Tickets · ${detail.tickets.length}` },
      ]} />
      {tab === 'overview' && (
        <section className="card card-body stack">
          <h2>Details</h2>
          <dl className="facts">
            <div><dt>Created</dt><dd>{formatWhen(a.createdAt)}</dd></div>
            <div><dt>Latest session</dt><dd>{formatWhen(a.lastSignInAt, true)}</dd></div>
            <div><dt>Signed-in devices</dt><dd>{detail.sessions}{detail.staffSessions ? ` (+${detail.staffSessions} staff)` : ''}</dd></div>
            <div><dt>Quna</dt><dd>{detail.quna === null ? '—' : detail.quna.toLocaleString()}</dd></div>
            <div><dt>Open tickets</dt><dd>{openTickets}</dd></div>
            <div><dt>Account id</dt><dd><code className="small">{a.accountId}</code></dd></div>
            <div>
              <dt>Staff rank</dt>
              <dd className="row">
                {a.rankLabel ?? 'None'}
                {can(profile, 'manageStaff') && !a.disabledAt && (
                  <button type="button" className="link small" onClick={() => setRoleOpen(true)}>Change</button>
                )}
              </dd>
            </div>
            <div>
              <dt>Two-step verification</dt>
              <dd className="row">
                {detail.totpEnabled ? 'On' : 'Off'}
                {detail.totpEnabled && canManage && (
                  <button type="button" className="link small" onClick={() => setConfirm({
                    title: 'Reset two-step verification', danger: true, confirmLabel: 'Reset',
                    body: `Turn off two-step verification for ${a.username}?`,
                    note: staffTarget ? 'Staff set up a new authenticator at their next dashboard sign-in.' : undefined,
                    onConfirm: () => act('totp', () => call(`/api/admin/accounts/${a.accountId}/totp/reset`, { method: 'POST' }), 'Two-step verification is off for this account.'),
                  })}>Reset</button>
                )}
              </dd>
            </div>
          </dl>
          <CharacterTable detail={detail} onOpen={onOpenCharacter} />
        </section>
      )}
      {tab === 'characters' && <CharacterTable detail={detail} onOpen={onOpenCharacter} />}
      {tab === 'quna' && (
        <QunaWallet call={call} accountId={a.accountId} accountName={a.username}
          canChange={can(profile, 'manageQuna') && !a.disabledAt} onChanged={() => { void load(); }} />
      )}
      {tab === 'sanctions' && <SanctionTable detail={detail} />}
      {tab === 'tickets' && <TicketTable detail={detail} />}
      <ConfirmDialog request={confirm} onClose={() => setConfirm(null)} />
      <RoleDialog open={roleOpen} account={a} onClose={() => setRoleOpen(false)} onSave={rank => act('role', async () => {
        if (rank) await call(`/api/admin/accounts/${a.accountId}/roles`, { method: 'POST', body: { role: rank, granted: true } });
        for (const role of a.roles.filter(r => r !== rank))
          await call(`/api/admin/accounts/${a.accountId}/roles`, { method: 'POST', body: { role, granted: false } });
      }, rank ? `${a.username} is now ${ROLE_LABEL[rank]}.` : `${a.username} has no staff rank now.`)} />
    </main>
  );
}

interface QunaEntry {
  entryId: number; delta: number; balanceAfter: number; reason: string; requestId: string; characterId: string | null; at: string;
  note: string | null; actor: string | null; refundable: boolean;
}
const QUNA_REASON: Record<string, string> = { purchase: 'Purchase', grant: 'Credited item', refund: 'Refund', staff: 'Staff', migration: 'Moved from items' };

// The account's Quna wallet (ported from KiskEmu's account page, AGPL-3.0): the balance and its ledger for staff who can see
// accounts; administrators (manageQuna) add or take Quna and give a purchase back. Every change is in the ledger and the
// activity log with the administrator and the note; players in the world see the new balance at once.
function QunaWallet({ call, accountId, accountName, canChange, onChanged }:
  { call: Call; accountId: string; accountName: string; canChange: boolean; onChanged: () => void }) {
  const [balance, setBalance] = useState<number | null>(null);
  const [entries, setEntries] = useState<QunaEntry[]>([]);
  const [amount, setAmount] = useState('');
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<StatusMessage | null>(null);
  const [confirm, setConfirm] = useState<ConfirmRequest | null>(null);

  const load = useCallback(() => call<{ balance: number; entries: QunaEntry[] }>(`/api/admin/accounts/${accountId}/quna?limit=100`)
    .then(r => { setBalance(r.balance); setEntries(r.entries); })
    .catch(error => setMessage({ text: describe(error), kind: 'error' })), [call, accountId]);
  useEffect(() => { setBalance(null); setEntries([]); void load(); }, [load]);

  const refunded = new Set(entries.filter(e => e.reason === 'refund' && e.requestId.startsWith('refund:')).map(e => e.requestId.slice(7)));

  async function run(action: () => Promise<{ balance: number }>, done: (balance: number) => string) {
    setBusy(true); setMessage(null);
    try {
      const r = await action();
      setMessage({ text: done(r.balance), kind: 'ok' });
      setAmount(''); setNote('');
      await load();
      onChanged();
    } catch (error) {
      setMessage({ text: error instanceof ApiError && error.status === 403 ? 'Only administrators change Quna.' : describe(error), kind: 'error' });
    } finally { setBusy(false); }
  }

  function change(sign: 1 | -1) {
    const value = Number(amount);
    if (!Number.isInteger(value) || value <= 0) { setMessage({ text: 'Enter a whole number of Quna above 0.', kind: 'error' }); return; }
    const text = note.trim();
    const id = requestId();
    setConfirm({
      title: sign > 0 ? 'Add Quna' : 'Take Quna', danger: sign < 0,
      body: `${sign > 0 ? 'Add' : 'Take'} ${value.toLocaleString()} Quna ${sign > 0 ? 'to' : 'from'} ${accountName}'s wallet?`,
      note: 'Recorded in the wallet ledger and the activity log under your account' + (text ? ', with your note.' : '.'),
      confirmLabel: sign > 0 ? 'Add Quna' : 'Take Quna',
      onConfirm: () => run(() => call<{ balance: number }>(`/api/admin/accounts/${accountId}/quna`,
        { method: 'POST', body: { requestId: id, amount: sign * value, note: text } }),
        b => `${sign > 0 ? 'Added' : 'Took'} ${value.toLocaleString()} Quna. Balance ${b.toLocaleString()}.`),
    });
  }

  function refund(entry: QunaEntry) {
    setConfirm({
      title: 'Give Quna back', confirmLabel: 'Give back',
      body: `Give ${(-entry.delta).toLocaleString()} Quna back to ${accountName} for this ${QUNA_REASON[entry.reason]?.toLowerCase() ?? entry.reason}?`,
      note: 'The purchased goods stay with the player; take them back separately if needed. A refund is given once.',
      onConfirm: () => run(() => call<{ balance: number }>(`/api/admin/accounts/${accountId}/quna/refund`,
        { method: 'POST', body: { requestId: entry.requestId } }), b => `Quna given back. Balance ${b.toLocaleString()}.`),
    });
  }

  return (
    <section className="card card-body stack">
      <div className="row wrap">
        <h2>Quna wallet</h2>
        <Badge tone="accent">{balance === null ? '…' : `${balance.toLocaleString()} Quna`}</Badge>
        <span className="muted small">One wallet per account, shared by its characters.</span>
      </div>
      <Status message={message} />
      {canChange && (
        <form className="row wrap" onSubmit={e => { e.preventDefault(); change(1); }}>
          <label htmlFor="quna-amount">Quna</label>
          <input id="quna-amount" className="input input-sm" type="number" min={1} step={1} required value={amount} onChange={e => setAmount(e.target.value)} />
          <input aria-label="Note" className="input input-sm" maxLength={200} placeholder="Note (optional)" value={note} onChange={e => setNote(e.target.value)} />
          <Button type="submit" size="sm" busy={busy}>Add</Button>
          <Button type="button" size="sm" variant="danger" disabled={busy} onClick={() => change(-1)}>Take</Button>
        </form>
      )}
      {entries.length === 0 ? <p className="empty">No Quna changes yet.</p>
        : (
          <div className="table-wrap">
            <table className="data-table">
              <thead><tr><th>When</th><th>What</th><th className="num">Change</th><th className="num">Balance</th><th>Note</th>{canChange && <th><span className="visually-hidden">Refund</span></th>}</tr></thead>
              <tbody>
                {entries.map(e => (
                  <tr key={e.entryId}>
                    <td className="nowrap">{formatWhen(e.at, true)}</td>
                    <td>{QUNA_REASON[e.reason] ?? e.reason}</td>
                    <td className="num mono">{e.delta > 0 ? '+' : ''}{e.delta.toLocaleString()}</td>
                    <td className="num mono">{e.balanceAfter.toLocaleString()}</td>
                    <td className="muted">{e.note ?? ''}</td>
                    {canChange && (
                      <td>{e.refundable && !refunded.has(e.requestId) && (
                        <button type="button" className="link small" disabled={busy} onClick={() => refund(e)}>Give back</button>
                      )}</td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      <ConfirmDialog request={confirm} onClose={() => setConfirm(null)} />
    </section>
  );
}

function sanctionLabel(kind: string): string {
  return ({ mute: 'Muted', ban: 'Banned', kick: 'Kicked', warn: 'Warned' } as Record<string, string>)[kind] ?? kind;
}

function CharacterTable({ detail, onOpen }: { detail: AccountDetail; onOpen?: (id: string) => void }) {
  return (
    <section className="card">
      <h2 className="pad">Characters</h2>
      {detail.characters.length === 0 ? <p className="empty pad">No characters.</p>
        : (
          <div className="table-wrap">
            <table className="data-table">
              <thead><tr><th>Name</th><th className="num">Level</th><th>Last played</th><th><span className="visually-hidden">Open</span></th></tr></thead>
              <tbody>
                {detail.characters.map(c => (
                  <tr key={c.characterId}>
                    <td className="strong">{c.name}</td>
                    <td className="num mono">{c.level}</td>
                    <td className="nowrap muted">{formatWhen(c.lastLoginAt, true)}</td>
                    <td>{onOpen && (
                      <button type="button" className="link" onClick={() => onOpen(c.characterId)}>Open character</button>
                    )}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
    </section>
  );
}

function SanctionTable({ detail }: { detail: AccountDetail }) {
  return (
    <section className="card">
      <h2 className="pad">Sanctions</h2>
      {detail.sanctions.length === 0 ? <p className="empty pad">No sanctions.</p>
        : (
          <div className="table-wrap">
            <table className="data-table">
              <thead><tr><th>Kind</th><th>Reason</th><th>From</th><th>Until</th><th>By</th></tr></thead>
              <tbody>
                {detail.sanctions.map(s => (
                  <tr key={s.id} className={s.active ? '' : 'row-dim'}>
                    <td>{sanctionLabel(s.kind)}{!s.active && <span className="muted small"> · ended</span>}</td>
                    <td>{s.reason}</td>
                    <td className="nowrap">{formatWhen(s.createdAt, true)}</td>
                    <td className="nowrap">{s.revokedAt ? `Lifted ${formatWhen(s.revokedAt)}` : s.expiresAt ? formatWhen(s.expiresAt, true) : 'Permanent'}</td>
                    <td>{s.createdBy ?? '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
    </section>
  );
}

function TicketTable({ detail }: { detail: AccountDetail }) {
  return (
    <section className="card">
      <h2 className="pad">Tickets</h2>
      {detail.tickets.length === 0 ? <p className="empty pad">No tickets.</p>
        : (
          <ul className="history pad">
            {detail.tickets.map(t => (
              <li key={t.id} className="stack-sm">
                <div className="row wrap">
                  <strong>#{t.id} · {t.category}</strong>
                  <Badge tone={t.status === 'open' ? 'accent' : undefined}>{t.status === 'open' ? 'Open' : 'Closed'}</Badge>
                  <span className="muted small">{formatWhen(t.createdAt, true)}</span>
                </div>
                <p className="pre">{t.content}</p>
                {t.staffNote && <p className="ticket-note">{t.staffNote}</p>}
              </li>
            ))}
          </ul>
        )}
    </section>
  );
}

const RANK_NOTE: Record<string, string> = {
  '': 'No dashboard access.',
  gamemaster: 'Helps players, announces, answers tickets, sees the live map and the logs.',
  moderator: 'Also sanctions and disables players, edits characters, rewards, events and monsters.',
  admin: 'Everything, including staff ranks, Quna, the shop, worlds and the server.',
};

function RoleDialog({ open, account, onClose, onSave }: { open: boolean; account: AccountSummary; onClose: () => void; onSave: (rank: string) => void }) {
  const ref = useRef<HTMLDialogElement>(null);
  const current = STAFF_ROLES.slice().reverse().find(r => account.roles.includes(r)) ?? '';
  const [rank, setRank] = useState<string>(current);
  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    if (open && !dialog.open) { setRank(current); dialog.showModal(); }
    if (!open && dialog.open) dialog.close();
  }, [open, current]);
  return (
    <dialog ref={ref} className="dialog" onClose={onClose} aria-labelledby="role-title">
      <form method="dialog" onSubmit={e => { e.preventDefault(); onClose(); if (rank !== current) onSave(rank); }}>
        <div className="dialog-body">
          <h2 id="role-title">Staff rank of {account.username}</h2>
          <fieldset className="plain">
            <legend className="visually-hidden">Rank</legend>
            {['', ...STAFF_ROLES].map(r => (
              <label key={r || 'none'} className="check block">
                <input type="radio" name="rank" value={r} checked={rank === r} onChange={() => setRank(r)} />
                <span><strong>{r ? ROLE_LABEL[r] : 'None'}</strong><span className="block small muted">{RANK_NOTE[r]}</span></span>
              </label>
            ))}
          </fieldset>
          <p className="small muted">A new staff member sets up an authenticator at their first dashboard sign-in.</p>
        </div>
        <div className="dialog-actions">
          <Button variant="ghost" onClick={onClose}>Cancel</Button>
          <Button type="submit" variant="primary" disabled={rank === current}>Save</Button>
        </div>
      </form>
    </dialog>
  );
}
