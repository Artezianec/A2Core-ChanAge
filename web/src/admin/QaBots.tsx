import { useCallback, useEffect, useState } from 'react';
import { Button, Status, type StatusMessage } from '../ui/components';
import type { Call } from './AdminApp';
import { describe } from './AdminApp';

interface QaRun {
  runId: number; startedAt: string; finishedAt: string | null; status: string;
  className: string; levelFrom: number; levelTo: number; botCount: number;
  questsRewarded: number; stuckCount: number; startedBy: string | null;
}

interface StuckSummary {
  questCode: string; questTitle: string | null; questStep: number;
  reasonCode: string; count: number; lastSeen: string | null;
}

interface StuckReport {
  reportId: number; questCode: string; questTitle: string | null; questStep: number;
  reasonCode: string; reasonDetail: string | null; characterLevel: number; mapId: number | null;
  lastActions: string[]; createdAt: string;
}

export function QaBots({ call, token }: { call: Call; token: string }) {
  const [runs, setRuns] = useState<QaRun[]>([]);
  const [summary, setSummary] = useState<StuckSummary[]>([]);
  const [reports, setReports] = useState<StuckReport[] | null>(null);
  const [selectedRun, setSelectedRun] = useState<number | null>(null);
  const [status, setStatus] = useState<StatusMessage | null>(null);
  const [loading, setLoading] = useState(true);

  const load = useCallback(async () => {
    setLoading(true);
    setStatus(null);
    try {
      const [runRows, sumRows] = await Promise.all([
        call<QaRun[]>('/api/admin/qa/runs?limit=30'),
        call<StuckSummary[]>('/api/admin/qa/stuck-summary?limit=200'),
      ]);
      setRuns(runRows);
      setSummary(sumRows);
    } catch (e) {
      setStatus({ kind: 'error', text: describe(e) });
    } finally {
      setLoading(false);
    }
  }, [call]);

  useEffect(() => { void load(); }, [load]);

  async function drillRun(runId: number) {
    setSelectedRun(runId);
    try {
      setReports(await call<StuckReport[]>(`/api/admin/qa/runs/${runId}/reports`));
    } catch (e) {
      setStatus({ kind: 'error', text: describe(e) });
    }
  }

  async function downloadExport(format: 'json' | 'csv') {
    const q = selectedRun ? `&runId=${selectedRun}` : '';
    const res = await fetch(`/api/admin/qa/export?format=${format}${q}`, { headers: { Authorization: `Bearer ${token}` } });
    if (!res.ok) { setStatus({ kind: 'error', text: 'Export failed.' }); return; }
    const blob = await res.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `qa-stuck.${format === 'csv' ? 'csv' : 'json'}`;
    a.click();
    URL.revokeObjectURL(url);
  }

  return (
    <div className="admin-page">
      <header className="page-header">
        <h1>QA bots</h1>
        <p className="muted">Auto-run pool starts when the game world is ready. Manual batch: <code>//qa run</code>; pause auto: <code>//qa stop</code>.</p>
        <div className="header-actions">
          <Button onClick={() => void load()} disabled={loading}>Refresh</Button>
          <Button variant="secondary" onClick={() => void downloadExport('csv')}>Export CSV</Button>
          <Button variant="secondary" onClick={() => void downloadExport('json')}>Export JSON</Button>
        </div>
      </header>
      <Status message={status} />
      <section className="card">
        <h2>Where bots got stuck</h2>
        {summary.length === 0 && !loading && <p className="muted">No stuck reports yet.</p>}
        <table className="data-table">
          <thead>
            <tr><th>Quest</th><th>Step</th><th>Reason</th><th>Count</th><th>Last seen</th></tr>
          </thead>
          <tbody>
            {summary.map((row, i) => (
              <tr key={i}>
                <td><code>{row.questCode}</code> {row.questTitle && <span className="muted">{row.questTitle}</span>}</td>
                <td>{row.questStep}</td>
                <td><code>{row.reasonCode}</code></td>
                <td>{row.count}</td>
                <td>{row.lastSeen ? new Date(row.lastSeen).toLocaleString() : '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
      <section className="card">
        <h2>Recent runs</h2>
        <table className="data-table">
          <thead>
            <tr><th>Run</th><th>Started</th><th>Class</th><th>Levels</th><th>Quests</th><th>Stuck</th><th>Status</th><th /></tr>
          </thead>
          <tbody>
            {runs.map((r) => (
              <tr key={r.runId}>
                <td>#{r.runId}</td>
                <td>{new Date(r.startedAt).toLocaleString()}</td>
                <td>{r.className}</td>
                <td>{r.levelFrom}–{r.levelTo} ({r.botCount} bots)</td>
                <td>{r.questsRewarded}</td>
                <td>{r.stuckCount}</td>
                <td>{r.status}</td>
                <td><Button variant="secondary" onClick={() => void drillRun(r.runId)}>Reports</Button></td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
      {reports && (
        <section className="card">
          <h2>Run #{selectedRun} stuck reports</h2>
          <table className="data-table">
            <thead>
              <tr><th>Quest</th><th>Step</th><th>Reason</th><th>Level</th><th>Actions</th><th>Time</th></tr>
            </thead>
            <tbody>
              {reports.map((rep) => (
                <tr key={rep.reportId}>
                  <td><code>{rep.questCode}</code></td>
                  <td>{rep.questStep}</td>
                  <td><code>{rep.reasonCode}</code> {rep.reasonDetail && <span className="muted">{rep.reasonDetail}</span>}</td>
                  <td>{rep.characterLevel}</td>
                  <td className="mono small">{rep.lastActions.slice(-5).join(' → ')}</td>
                  <td>{new Date(rep.createdAt).toLocaleString()}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      )}
    </div>
  );
}