import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { useLiveFeed } from '../api/live';
import type { Alert, LiveMessage, Runbook, RunProgress, Severity } from '../api/types';
import { Search } from '../components/Icons';
import { Shell } from '../components/Shell';
import { Pill } from '../components/Switch';
import { proposalsChanged } from '../lib/proposalsSignal';
import { useLoad } from '../lib/useLoad';
import { ago, SEVERITY, STATUS } from '../lib/ui';
import { progressChip, waitsForPerson } from '../runs/runs';
import { useSession } from '../session';
import { AlertPanel } from './AlertPanel';

type View = 'attention' | 'waiting' | 'assigned' | 'resolved' | `team:${string}`;

export function AlertsPage() {
  const session = useSession();
  const [params, setParams] = useSearchParams();
  const selected = params.get('alert');
  const view = (params.get('view') as View) || 'attention';
  const [sev, setSev] = useState<'all' | Severity>('all');
  const [query, setQuery] = useState('');
  const [timelineTick, setTimelineTick] = useState(0);
  // Run progress per alert (the `runbook` of GET /alerts/{id}); undefined = not loaded yet.
  const [progress, setProgress] = useState<Record<string, RunProgress | null>>({});
  const loading = useRef(new Set<string>());

  const alerts = useLoad(() => api.alerts(), []);
  const runbooks = useLoad(() => api.runbooks(), []);

  const runbookById = useMemo(() => new Map((runbooks.data ?? []).map((r) => [r.id, r])), [runbooks.data]);
  const bookOf = useCallback((a: Alert): Runbook | null => {
    const rb = a.runbook_id ? runbookById.get(a.runbook_id) : undefined;
    return rb && rb.status === 'active' && rb.canonical ? rb : null;
  }, [runbookById]);

  const loadProgress = useCallback((ids: string[]) => {
    ids.filter((id) => !loading.current.has(id)).forEach((id) => {
      loading.current.add(id);
      api.alert(id)
        .then((d) => setProgress((p) => ({ ...p, [id]: d.runbook ?? null })))
        .catch(() => {})
        .finally(() => loading.current.delete(id));
    });
  }, []);

  // Progress for the open alerts of my teams that have a runbook (the chips and "Waiting for a person").
  useEffect(() => {
    const ids = (alerts.data ?? []).filter((a) => session.isMine(a.team_id) && a.status !== 'resolved' && bookOf(a) && !(a.id in progress)).map((a) => a.id);
    if (ids.length) loadProgress(ids);
  }, [alerts.data, bookOf, session, progress, loadProgress]);

  const onMessage = useCallback((m: LiveMessage) => {
    if (m.type === 'alert') {
      alerts.setData((prev) => {
        const list = prev ?? [];
        return list.some((a) => a.id === m.alert.id) ? list.map((a) => (a.id === m.alert.id ? m.alert : a)) : [m.alert, ...list];
      });
      if (m.alert.id === selected) setTimelineTick((t) => t + 1);
    } else if (m.type === 'count') {
      alerts.setData((prev) => (prev ?? []).map((a) => (a.id === m.alert_id ? { ...a, count: m.count, last_seen: m.last_seen } : a)));
    } else if (m.type === 'timeline') {
      // A proposal filed by the ai-worker is a timeline row too; the badge coalesces these into one fetch per 2 s.
      proposalsChanged();
      // Run and step changes arrive as timeline messages: refresh that alert's progress.
      if (m.alert_id in progress) loadProgress([m.alert_id]);
      if (m.alert_id === selected) setTimelineTick((t) => t + 1);
    }
  }, [alerts, selected, progress, loadProgress]);
  const live = useLiveFeed(onMessage, () => {
    void alerts.reload();
    setProgress({});
    setTimelineTick((t) => t + 1);
  });

  const all = alerts.data ?? [];
  const mine = (a: Alert) => session.isMine(a.team_id);
  const views: Record<string, { label: string; test: (a: Alert) => boolean }> = {
    attention: { label: 'Needs attention', test: (a) => mine(a) && a.status !== 'resolved' },
    waiting: { label: 'Waiting for a person', test: (a) => mine(a) && waitsForPerson(progress[a.id], !!bookOf(a), a.status === 'resolved') },
    assigned: { label: 'Assigned to me', test: (a) => session.onCallTeamIds.includes(a.team_id) && a.status !== 'resolved' && (!a.acked_by || a.acked_by === session.user?.id) },
    resolved: { label: 'Resolved', test: (a) => mine(a) && a.status === 'resolved' },
  };
  const testFor = (v: View) => (v.startsWith('team:') ? (a: Alert) => a.team_id === v.slice(5) && a.status !== 'resolved' : views[v]!.test);
  const setView = (v: View) => setParams((p) => { p.set('view', v); return p; });
  const select = (id: string | null) => setParams((p) => { if (id) p.set('alert', id); else p.delete('alert'); return p; });

  const q = query.trim().toLowerCase();
  const rows = all.filter(testFor(view)).filter((a) => sev === 'all' || a.severity === sev)
    .filter((a) => !q || `${a.title} ${a.body ?? ''} ${session.teamName(a.team_id)} ${a.rule_id}`.toLowerCase().includes(q));
  const current = all.find((a) => a.id === selected) ?? null;

  const myTeams = session.teams.filter((t) => session.isMine(t.id));
  const otherTeams = session.teams.filter((t) => !session.isMine(t.id));
  const sideItem = (v: View, label: string, warn = false) => {
    const n = all.filter(testFor(v)).length;
    return (
      <button key={v} type="button" className={'item' + (view === v ? ' on' : '')} onClick={() => setView(v)}>
        {v.startsWith('team:') && <span aria-hidden>#</span>}
        <span className="grow">{label}</span>
        {n > 0 && <span className={'count' + (warn ? ' warn' : '')}>{n}</span>}
      </button>
    );
  };

  const heading = view.startsWith('team:') ? `# ${session.teamName(view.slice(5))}` : views[view]!.label;
  const subheading = view === 'waiting'
    ? 'Alerts of your teams whose runbook nobody has started yet. Starting it is the approval.'
    : view.startsWith('team:')
      ? (session.isMine(view.slice(5)) ? 'Your team. Open alerts, newest first.' : 'You are not in this team. You can follow its alerts.')
      : myTeams.length ? `Alerts of your teams: ${myTeams.map((t) => t.name + (session.onCallTeamIds.includes(t.id) ? ' (you are on call)' : '')).join(', ')}.` : 'You are not in a team yet.';

  return (
    <Shell side={
      <>
        <div>
          <h2>Kestrel Markets</h2>
          <div className="sub row" style={{ gap: 6, marginTop: 2 }}>
            <span className="sdot" style={{ background: live ? '#22C55E' : '#A1A1AA' }} />{live ? 'Live' : 'Reconnecting…'} · {session.user?.name}
          </div>
        </div>
        <div>
          <h3>Views</h3>
          {sideItem('attention', views.attention!.label)}
          {sideItem('waiting', views.waiting!.label, true)}
          {sideItem('assigned', views.assigned!.label)}
          {sideItem('resolved', views.resolved!.label)}
        </div>
        {myTeams.length > 0 && (
          <div>
            <h3>Your teams</h3>
            {myTeams.map((t) => sideItem(`team:${t.id}`, t.name + (session.onCallTeamIds.includes(t.id) ? ' · you’re on call' : '')))}
          </div>
        )}
        {otherTeams.length > 0 && (
          <div>
            <h3>Other teams</h3>
            {otherTeams.map((t) => sideItem(`team:${t.id}`, t.name))}
          </div>
        )}
      </>
    }>
      <section className="content">
        <div className="row" style={{ alignItems: 'flex-end', gap: 12 }}>
          <div style={{ flex: 1, minWidth: 240 }}>
            <h1 className="h1">{heading}</h1>
            <p className="lead" style={{ marginTop: 6 }}>{subheading}</p>
          </div>
          <label className="search">
            <Search />
            <input type="search" placeholder="Search alerts" aria-label="Search alerts" value={query} onChange={(e) => setQuery(e.target.value)} />
          </label>
        </div>
        <div role="group" aria-label="Severity" className="row">
          {(['all', 'critical', 'error', 'warning', 'info'] as const).map((s) => (
            <button key={s} type="button" aria-pressed={sev === s} className={'chip grey' + (sev === s ? ' on' : '')} onClick={() => setSev(s)}>
              {s === 'all' ? 'All' : SEVERITY[s].label}
            </button>
          ))}
        </div>
        {alerts.error && <div className="banner" style={{ background: 'var(--red-soft)', color: '#7F1D1D' }}>Could not load alerts: {alerts.error}</div>}
        <ul className="plain">
          {rows.map((a) => {
            const s = SEVERITY[a.severity] ?? SEVERITY.info;
            const chip = mine(a) ? progressChip(progress[a.id], !!bookOf(a), a.status === 'resolved') : null;
            return (
              <li key={a.id}>
                <button type="button" className={'alert-row' + (a.id === selected ? ' on' : '')} onClick={() => select(a.id)}>
                  <span className="sev-icon" style={{ color: s.fg, background: s.bg }} aria-hidden>{s.glyph}</span>
                  <span style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 4 }}>
                    <span className="wrap" style={{ fontWeight: 700, fontSize: 16 }}>{a.title}</span>
                    <span className="small">
                      {session.teamName(a.team_id)} · last {ago(a.last_seen)}
                      {a.acked_by ? ` · on it: ${session.userName(a.acked_by)}` : ''}
                    </span>
                    {chip && <Pill fg={chip.fg} bg={chip.bg} style={{ alignSelf: 'flex-start', marginTop: 2 }}>{chip.text}</Pill>}
                  </span>
                  <span style={{ display: 'flex', flexDirection: 'column', alignItems: 'flex-end', gap: 6, flex: 'none' }}>
                    <span style={{ fontWeight: 800, fontSize: 18 }}>×{a.count}</span>
                    <Pill fg={STATUS[a.status].fg} bg={STATUS[a.status].bg}>{STATUS[a.status].label}</Pill>
                  </span>
                </button>
              </li>
            );
          })}
        </ul>
        {!alerts.loading && rows.length === 0 && <div className="empty">Nothing here right now.</div>}
        {alerts.loading && !alerts.data && <div className="empty">Loading alerts…</div>}
      </section>

      {current && (
        <AlertPanel
          key={current.id}
          alert={current}
          runbook={bookOf(current)}
          timelineTick={timelineTick}
          onClose={() => select(null)}
          onChanged={(a) => alerts.setData((prev) => (prev ?? []).map((x) => (x.id === a.id ? { ...x, ...a } : x)))}
          onProgress={(p) => setProgress((prev) => ({ ...prev, [current.id]: p }))}
        />
      )}
    </Shell>
  );
}
