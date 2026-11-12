// Staff dashboard: realm-wide figures (GET /api/admin/stats) and online history (GET /api/admin/population).
// Single-game-server layout (no multi-world registry — see extension points in StaffAccountEndpoints).
import { useCallback, useEffect, useMemo, useState } from 'react';
import { Button, Icon, Status } from '../ui/components';
import { describe, type Call } from './AdminApp';
import { can, type PopulationPoint, type StaffProfile } from './types';

interface DayCount { day: string; count: number }
interface NamedCount { name: string; count: number }
interface RealmStats {
  accounts: number; staffAccounts: number; disabledAccounts: number; newAccounts30Days: number;
  newAccounts: DayCount[]; characters: number; charactersByWorld: NamedCount[]; levelBands: NamedCount[]; races: NamedCount[];
  qunaInCirculation: number; openTickets: number; ticketsWaitingOverADay: number; activeMutes: number; activeBans: number;
}

const RANGES = [{ hours: 24, label: '24 hours' }, { hours: 24 * 7, label: '7 days' }, { hours: 24 * 30, label: '30 days' }];

function fmt(n: number | null | undefined): string {
  if (n == null || Number.isNaN(n)) return '–';
  return n.toLocaleString();
}

export function Home({ call, profile, active }: { call: Call; profile: StaffProfile; active: boolean }) {
  const [stats, setStats] = useState<RealmStats | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [hours, setHours] = useState(24);
  const [points, setPoints] = useState<PopulationPoint[] | null>(null);

  const load = useCallback(async () => {
    try {
      setStats(await call<RealmStats>('/api/admin/stats'));
      setError(null);
    } catch (e) {
      setError(describe(e));
    }
  }, [call]);

  useEffect(() => { if (active) load(); }, [active, load]);
  useEffect(() => {
    if (!active) return;
    setPoints(null);
    call<PopulationPoint[]>(`/api/admin/population?hours=${hours}`).then(setPoints).catch(() => setPoints([]));
  }, [active, call, hours]);

  const onlineNow = points?.length ? points[points.length - 1].players : null;

  return (
    <main className="page">
      <header className="page-head">
        <div>
          <h1>Dashboard</h1>
          <p className="muted">Population, accounts, moderation queue, and growth at a glance.</p>
        </div>
        <Button size="sm" onClick={load}><Icon name="refresh" size={16} /> Refresh</Button>
      </header>
      <Status message={error ? { text: error, kind: 'error' } : null} />

      <div className="stats">
        <div className="card stat stat-accent">
          <span className="stat-label">Online now</span>
          <span className="stat-value">{fmt(onlineNow)}</span>
        </div>
        <div className="card stat">
          <span className="stat-label">Characters</span>
          <span className="stat-value">{fmt(stats?.characters)}</span>
        </div>
        <div className="card stat">
          <span className="stat-label">Accounts</span>
          <span className="stat-value">{fmt(stats?.accounts)}</span>
          {stats && <span className="stat-note">{fmt(stats.newAccounts30Days)} new in 30 days</span>}
        </div>
        <div className="card stat">
          <span className="stat-label">Open tickets</span>
          <span className="stat-value">{fmt(stats?.openTickets)}</span>
          {stats && stats.ticketsWaitingOverADay > 0 && (
            <span className="stat-note warn">{stats.ticketsWaitingOverADay} waiting over a day</span>
          )}
        </div>
        <div className="card stat">
          <span className="stat-label">Active sanctions</span>
          <span className="stat-value">{stats ? fmt(stats.activeBans + stats.activeMutes) : '–'}</span>
          {stats && <span className="stat-note">{stats.activeBans} bans · {stats.activeMutes} mutes</span>}
        </div>
        {can(profile, 'viewEconomy') && (
          <div className="card stat">
            <span className="stat-label">Quna in circulation</span>
            <span className="stat-value">{fmt(stats?.qunaInCirculation)}</span>
            <span className="stat-note faint">Wallet UI not enabled yet</span>
          </div>
        )}
      </div>

      <section className="card card-body stack">
        <div className="panel-head">
          <h2>Players online</h2>
          <div className="segmented-sm" role="group" aria-label="Time range">
            {RANGES.map(r => (
              <button key={r.hours} type="button" aria-pressed={hours === r.hours} onClick={() => setHours(r.hours)}>{r.label}</button>
            ))}
          </div>
        </div>
        <PopulationChart points={points} hours={hours} />
      </section>

      {stats && (
        <div className="economy-grid">
          <section className="card card-body stack">
            <h2>Characters by level</h2>
            <BarList items={stats.levelBands} />
          </section>
          <section className="card card-body stack">
            <h2>Characters by race</h2>
            <BarList items={stats.races} />
          </section>
          <section className="card card-body stack">
            <h2>New accounts (30 days)</h2>
            <DayBars days={stats.newAccounts} />
          </section>
          <section className="card card-body stack">
            <h2>Accounts</h2>
            <dl className="facts">
              <div><dt>Total</dt><dd className="mono">{fmt(stats.accounts)}</dd></div>
              <div><dt>Staff</dt><dd className="mono">{fmt(stats.staffAccounts)}</dd></div>
              <div><dt>Disabled</dt><dd className="mono">{fmt(stats.disabledAccounts)}</dd></div>
            </dl>
          </section>
        </div>
      )}
    </main>
  );
}

function PopulationChart({ points, hours }: { points: PopulationPoint[] | null; hours: number }) {
  const shape = useMemo(() => {
    if (!points || points.length < 2) return null;
    const start = Date.now() - hours * 3600_000;
    const max = Math.max(4, ...points.map(p => p.players));
    const top = Math.ceil(max / 4) * 4;
    const line = points.map(p => {
      const x = ((new Date(p.at).getTime() - start) / (hours * 3600_000)) * 100;
      return `${Math.max(0, x).toFixed(2)},${(40 - (p.players / top) * 40).toFixed(2)}`;
    }).join(' ');
    return { line, top, peak: Math.max(...points.map(p => p.players)) };
  }, [points, hours]);
  if (points === null) return <p className="muted">Loading…</p>;
  if (!shape) return <p className="muted">No samples yet. The server records population every five minutes.</p>;
  return (
    <div className="stack-sm">
      <p className="muted small">Peak {fmt(shape.peak)} · scale to {fmt(shape.top)}</p>
      <svg className="bars bars-wide" viewBox="0 0 100 40" preserveAspectRatio="none" role="img" aria-label={`Players online, peak ${shape.peak}`}>
        {[0, 10, 20, 30, 40].map(y => <line key={y} x1="0" x2="100" y1={y} y2={y} stroke="var(--line)" strokeWidth="0.15" />)}
        <polyline points={shape.line} fill="none" stroke="var(--accent)" strokeWidth="0.6" />
      </svg>
      <div className="row-between faint small"><span>{hours <= 24 ? `${hours} h ago` : `${hours / 24} days ago`}</span><span>Now</span></div>
    </div>
  );
}

function BarList({ items }: { items: NamedCount[] }) {
  const max = Math.max(1, ...items.map(i => i.count));
  if (items.length === 0) return <p className="muted">No characters yet.</p>;
  return (
    <ul className="stack-sm">
      {items.map(i => (
        <li key={i.name} className="row" style={{ gap: 8 }}>
          <span style={{ width: '6rem', flex: 'none' }}>{i.name}</span>
          <span className="grow" style={{ background: 'var(--panel-2)', borderRadius: 4, height: 8, overflow: 'hidden' }}>
            <span style={{ display: 'block', height: '100%', width: `${(i.count / max) * 100}%`, background: 'var(--accent)' }} />
          </span>
          <span className="mono num">{fmt(i.count)}</span>
        </li>
      ))}
    </ul>
  );
}

function DayBars({ days }: { days: DayCount[] }) {
  const byDay = new Map(days.map(d => [d.day.slice(0, 10), d.count]));
  const list = Array.from({ length: 30 }, (_, i) => {
    const date = new Date(Date.now() - (29 - i) * 86400_000).toISOString().slice(0, 10);
    return { date, count: byDay.get(date) ?? 0 };
  });
  const max = Math.max(1, ...list.map(d => d.count));
  const total = list.reduce((s, d) => s + d.count, 0);
  return (
    <div className="stack-sm">
      <p className="muted small">{fmt(total)} in 30 days</p>
      <svg className="bars bars-small" viewBox="0 0 360 40" preserveAspectRatio="none" role="img" aria-label={`${total} new accounts in 30 days`}>
        {list.map((d, i) => (
          <rect key={d.date} x={i * 12 + 1} width="10" y={40 - d.count / max * 38} height={Math.max(d.count ? 1 : 0, d.count / max * 38)}>
            <title>{`${d.date}: ${d.count}`}</title>
          </rect>
        ))}
      </svg>
    </div>
  );
}
