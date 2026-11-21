import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api, errorText } from '../api/client';
import type { Alert, AlertEvent, Runbook, RunNode, RunProgress } from '../api/types';
import { Pill } from '../components/Switch';
import { useToast } from '../components/Toast';
import { useLoad } from '../lib/useLoad';
import { ago, CHANNEL_LABEL, clock, listText, RISK, SEVERITY, STATUS } from '../lib/ui';
import { AUTO_START_AVAILABLE, LIVE, nodeView, paramsLine } from '../runs/runs';
import { useSession } from '../session';

interface Props {
  alert: Alert;
  runbook: Runbook | null;
  timelineTick: number;
  onClose: () => void;
  onChanged: (a: Alert) => void;
  onProgress: (p: RunProgress | null) => void;
}

function pretty(kind: string): string {
  return kind.replace(/_/g, ' ');
}

/** One timeline row (an alert_events row) in plain words. */
function timelineText(e: AlertEvent, userName: (id: string) => string): string {
  const who = e.actor === 'auto' ? 'Relay' : ['processor', 'notifier', 'api', 'ai-worker'].includes(e.actor) ? 'Relay' : userName(e.actor);
  const d = (e.data ?? {}) as Record<string, any>;
  const n = d.notification as Record<string, any> | undefined;
  switch (e.kind) {
    case 'opened': return 'Alert opened by its rule.';
    case 'reopened': return 'It happened again, so the alert reopened.';
    case 'severity_raised': return 'Severity went up.';
    case 'acked': return `${who} acknowledged it. Pending reminders were cancelled.`;
    case 'resolved': return e.actor === 'auto' ? 'Closed by itself: no repeat within the rule’s window.' : `${who} resolved it.`;
    case 'notification_sent': return n ? `${userName(n.user_id)} notified by ${(CHANNEL_LABEL[n.channel] ?? n.channel).toLowerCase()}.` : 'Someone was notified.';
    case 'notification_failed': return n ? `Could not reach ${userName(n.user_id)} by ${(CHANNEL_LABEL[n.channel] ?? n.channel).toLowerCase()}; retrying.` : 'A notification failed; retrying.';
    case 'notification_suppressed': return n ? `${userName(n.user_id)} not notified: ${n.reason ?? 'suppressed'}.` : 'A notification was held back.';
    case 'proposal_created': return `${who} suggested a change.`;
    case 'proposal_decided': return `${who} decided on a suggested change.`;
    default: return `${who}: ${pretty(e.kind)}.`;
  }
}

export function AlertPanel({ alert, runbook, timelineTick, onClose, onChanged, onProgress }: Props) {
  const session = useSession();
  const say = useToast();
  const [runTick, setRunTick] = useState(0);
  const detail = useLoad(() => api.alert(alert.id), [alert.id, timelineTick, runTick]);
  useEffect(() => {
    if (detail.data) onProgress(detail.data.runbook ?? null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [detail.data]);
  const [busy, setBusy] = useState(false);
  const [fixOpen, setFixOpen] = useState(false);
  const [fixKind, setFixKind] = useState<'threshold' | 'template'>('threshold');
  const [fixText, setFixText] = useState('');
  const mine = session.isMine(alert.team_id);
  const s = SEVERITY[alert.severity] ?? SEVERITY.info;
  const st = STATUS[alert.status];

  const act = async (what: 'ack' | 'resolve') => {
    setBusy(true);
    try {
      const updated = what === 'ack' ? await api.ack(alert.id) : await api.resolve(alert.id);
      onChanged(updated);
      say(what === 'ack' ? 'Acknowledged. Pending reminders are cancelled.' : 'Resolved.');
      void detail.reload();
    } catch (e) {
      say(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  const sendFix = async () => {
    if (!fixText.trim()) return say('Write what should change first.');
    try {
      await api.propose(alert.id, fixKind, { team_id: alert.team_id, rule_id: alert.rule_id, text: fixText.trim() });
      setFixOpen(false);
      setFixText('');
      say(`Proposal sent. ${session.teamName(alert.team_id)} decides in Proposals.`);
    } catch (e) {
      say(errorText(e));
    }
  };

  const minutes = Math.max(1, (new Date(alert.last_seen).getTime() - new Date(alert.first_seen).getTime()) / 60000);
  const fields = Object.entries(alert.fields ?? {});
  const d = detail.data;

  return (
    <aside className="panel" aria-label="Alert details">
      <div className="row">
        <span style={{ fontWeight: 800, fontSize: 18, flex: 1 }}>Alert</span>
        <button type="button" className="icon-btn" aria-label="Close alert" onClick={onClose}>✕</button>
      </div>
      <div className="row" style={{ gap: 6 }}>
        <Pill fg={s.fg} bg={s.bg}>{s.label}</Pill>
        <Pill fg={st.fg} bg={st.bg}>{st.label}</Pill>
        {alert.incident_id && <Pill fg="#5B21B6" bg="#F3E8FF">Part of an incident</Pill>}
      </div>
      <div>
        <h2 className="wrap" style={{ margin: 0, fontSize: 24, fontWeight: 800, letterSpacing: '-0.02em', lineHeight: 1.2 }}>{alert.title}</h2>
        {alert.body && <p className="wrap" style={{ margin: '8px 0 0', fontSize: 15, color: 'var(--soft)' }}>{alert.body}</p>}
        <p className="small" style={{ margin: '6px 0 0' }}>
          Team {session.teamName(alert.team_id)} · rule {alert.rule_id}
          {alert.acked_by ? ` · acknowledged by ${session.userName(alert.acked_by)}` : ''}
          {alert.resolved_by ? ` · resolved by ${alert.resolved_by === 'auto' ? 'itself' : session.userName(alert.resolved_by)}` : ''}
        </p>
      </div>

      {mine ? (
        <div className="row">
          {alert.status === 'open' && <button type="button" className="btn dark" disabled={busy} onClick={() => void act('ack')}>Acknowledge</button>}
          {alert.status !== 'resolved' && <button type="button" className="btn light" disabled={busy} onClick={() => void act('resolve')}>Resolve</button>}
          <button type="button" className="btn light" aria-expanded={fixOpen} onClick={() => setFixOpen(!fixOpen)}>Suggest a fix</button>
        </div>
      ) : (
        <div className="tile" style={{ fontSize: 14, color: 'var(--soft)', lineHeight: 1.5 }}>
          You are not in <strong>{session.teamName(alert.team_id)}</strong>, so you can follow this alert but not act on it.
        </div>
      )}

      {fixOpen && mine && (
        <div className="banner" style={{ background: '#F3E8FF', padding: 18, gap: 10 }}>
          <label htmlFor="fix" style={{ fontWeight: 800, fontSize: 15 }}>What should change about this alert?</label>
          <div role="group" aria-label="Kind of change" className="row" style={{ gap: 6 }}>
            {([['threshold', 'When it fires'], ['template', 'How it looks']] as const).map(([k, l]) => (
              <button key={k} type="button" aria-pressed={fixKind === k} className={'chip sm' + (fixKind === k ? ' on' : '')} onClick={() => setFixKind(k)}>{l}</button>
            ))}
          </div>
          <textarea id="fix" rows={3} className="area" value={fixText} onChange={(e) => setFixText(e.target.value)}
            placeholder={fixKind === 'threshold' ? 'e.g. Wait for 3 failures in 1 minute before calling on-call.' : 'e.g. Show the counterparty name in the title.'} />
          <div className="small">It goes to Proposals, where {session.teamName(alert.team_id)} accepts or rejects it.</div>
          <div><button type="button" className="btn sm dark" onClick={() => void sendFix()}>Send proposal</button></div>
        </div>
      )}

      <div className="card" style={{ padding: 18, gap: 12, borderRadius: 20 }}>
        <div className="row" style={{ alignItems: 'flex-end', gap: 20 }}>
          <div>
            <div style={{ fontSize: 32, fontWeight: 800, lineHeight: 1 }}>×{alert.count}</div>
            <div className="small" style={{ marginTop: 4 }}>same problem since {clock(alert.first_seen)}</div>
          </div>
          <div className="small">
            Last seen {ago(alert.last_seen)}<br />
            about {(alert.count / minutes).toFixed(alert.count / minutes < 10 ? 1 : 0)} per minute
          </div>
        </div>
        {fields.length > 0 && (
          <dl style={{ display: 'grid', gridTemplateColumns: '120px minmax(0, 1fr)', gap: '6px 12px', margin: 0, fontSize: 14 }}>
            {fields.map(([k, v]) => (
              <div key={k} style={{ display: 'contents' }}>
                <dt style={{ color: 'var(--muted)' }}>{k}</dt>
                <dd className="wrap" style={{ margin: 0, fontWeight: 600 }}>{typeof v === 'string' ? v : JSON.stringify(v)}</dd>
              </div>
            ))}
          </dl>
        )}
      </div>

      <RunSection alert={alert} runbook={runbook} progress={detail.data ? detail.data.runbook ?? null : undefined} mine={mine} onRunChanged={() => setRunTick((x) => x + 1)} />

      <div className="stack" style={{ gap: 8 }}>
        <h3 className="h3">Who was told, and why</h3>
        {d && d.notifications.length === 0 && <div className="small">Nobody yet.</div>}
        {d?.notifications.map((n) => {
          const ok = n.status === 'sent';
          return (
            <div key={n.id} className="row" style={{ alignItems: 'flex-start', gap: 10, fontSize: 14, padding: '8px 0', borderBottom: '1px solid var(--grey)', flexWrap: 'nowrap' }}>
              <span className="sdot" style={{ width: 10, height: 10, marginTop: 6, background: ok ? '#22C55E' : n.status === 'failed' || n.status === 'dead' ? '#EF4444' : '#A1A1AA' }} />
              <div style={{ flex: 1 }}>
                <strong>{session.userName(n.user_id)}</strong> · {CHANNEL_LABEL[n.channel] ?? n.channel} <span style={{ color: 'var(--muted)' }}>· {n.status}</span>
                {n.reason && <div className="small">{n.reason}</div>}
                {n.error && <div className="small" style={{ color: 'var(--red)' }}>{n.error}</div>}
              </div>
            </div>
          );
        })}
      </div>

      <div className="stack" style={{ gap: 6 }}>
        <h3 className="h3">Timeline</h3>
        {detail.error && <div className="small" style={{ color: 'var(--red)' }}>{detail.error}</div>}
        {[...(d?.timeline ?? [])].sort((x, y) => x.at.localeCompare(y.at)).map((e) => (
          <div key={e.id} className="row" style={{ gap: 12, fontSize: 13, flexWrap: 'nowrap', alignItems: 'flex-start' }}>
            <span style={{ color: 'var(--muted)', width: 56, flex: 'none', fontVariantNumeric: 'tabular-nums' }}>{clock(e.at)}</span>
            <span>{timelineText(e, session.userName)}</span>
          </div>
        ))}
      </div>
    </aside>
  );
}

/** The run of the alert's runbook, from the API: preview, start (the person's start is the approval), progress. */
function RunSection({ alert, runbook, progress, mine, onRunChanged }: { alert: Alert; runbook: Runbook | null; progress: RunProgress | null | undefined; mine: boolean; onRunChanged: () => void }) {
  const say = useToast();
  const { teamName, userName } = useSession();
  const [reviewing, setReviewing] = useState(false);
  const [busy, setBusy] = useState(false);
  const runId = progress?.run_id ?? null;
  const runKey = progress ? `${progress.run_id}:${progress.status}:${progress.steps_done}` : 'none';
  const run = useLoad(() => (runId ? api.run(runId) : Promise.resolve(null)), [runKey]);
  const finished = !progress || !LIVE.includes(progress.status);
  const showPreview = !!runbook && (progress === null || (finished && reviewing));
  const preview = useLoad(() => (runbook && showPreview ? api.runPreview(alert.id, runbook.id) : Promise.resolve(null)), [runbook?.id, runbook?.version, showPreview]);
  // Live messages can be lost (contracts/events.md §5): while a run is live, also check every 2 s.
  useEffect(() => {
    if (finished) return;
    const t = setInterval(onRunChanged, 2000);
    return () => clearInterval(t);
  }, [finished, onRunChanged]);

  if (!runbook) {
    return (
      <section aria-label="Runbook" className="stack">
        <h3 className="h3">What to do</h3>
        <div className="tile small" style={{ color: 'var(--soft)' }}>No runbook fits this alert yet. <Link to="/runbooks" style={{ fontWeight: 700, textDecoration: 'none' }}>Add one</Link></div>
      </section>
    );
  }
  if (progress === undefined) return <section aria-label="Runbook" className="stack"><h3 className="h3">What to do</h3><div className="small">Loading…</div></section>;

  const canAct = mine && alert.status !== 'resolved';
  const r = run.data;
  const p = preview.data;
  const nodes: RunNode[] = showPreview ? (p?.nodes ?? []) : (r?.nodes ?? []);
  const risk = showPreview ? p?.risk : r?.risk;
  const needPerson = [...new Set(nodes.filter((n) => n.risk === 'approval').map((n) => n.action ?? n.text))];
  const live = !!r && LIVE.includes(r.status);

  const startRun = async () => {
    setBusy(true);
    try {
      await api.startRun(alert.id, runbook.id);
      setReviewing(false);
      say('Runbook started. Its steps run in order now.');
      onRunChanged();
    } catch (e) {
      say(errorText(e));
    } finally {
      setBusy(false);
    }
  };
  const act = async (fn: () => Promise<unknown>, msg: string) => {
    setBusy(true);
    try {
      await fn();
      say(msg);
      onRunChanged();
    } catch (e) {
      say(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  let title: string, sub: string, bg: string;
  if (showPreview) {
    title = reviewing ? 'Check what will run' : risk === 'approval' ? 'Needs a person to start' : 'Not started';
    sub = reviewing ? 'Every action is shown with the values from this alert.'
      : risk === 'approval' ? `It contains ${listText(needPerson)}, so a person starts it. Review what will run, then start it.`
        : AUTO_START_AVAILABLE ? 'It starts by itself shortly.' : 'Its actions could start by themselves; until automatic starts are switched on, start it yourself.';
    bg = '#FFFBEB';
  } else {
    const done = progress!.steps_done;
    const by = r?.trigger === 'auto' ? 'Started by itself.' : r?.approved_by ? `Started by ${userName(r.approved_by)}.` : '';
    const m: Record<string, [string, string, string]> = {
      scheduled: ['Starts soon', 'Cancel it if you do not want it to run.', '#E8F1FF'],
      running: [`Running · ${done} of ${progress!.steps_total} done`, `${by} Steps run in order; only text-only steps wait for a person.`, '#F4F4F6'],
      done: [`Done · ${done} of ${progress!.steps_total}`, by, '#F0FDF4'],
      failed: ['Failed', 'A step failed and the run stopped. A person takes it from here.', '#FEF2F2'],
      cancelled: ['Cancelled', `${r?.cancelled_by ? `Cancelled by ${userName(r.cancelled_by)}. ` : ''}Steps that finished keep their results.`, '#F4F4F6'],
    };
    [title, sub, bg] = m[progress!.status]!;
  }
  if (!mine) {
    title = `Only ${teamName(alert.team_id)} can start this runbook`;
    sub = 'You can see what it would do.';
    bg = '#F4F4F6';
  }
  const ordered = reviewing && showPreview ? [...nodes].sort((a, b) => (b.risk === 'approval' ? 1 : 0) - (a.risk === 'approval' ? 1 : 0)) : nodes;

  return (
    <section aria-label="Runbook" className="stack">
      <div className="between">
        <h3 className="h3">What to do</h3>
        <Link to={`/runbooks?id=${encodeURIComponent(runbook.id)}`} style={{ fontSize: 14, fontWeight: 700, textDecoration: 'none' }}>{runbook.title}</Link>
      </div>
      <div className="banner" style={{ background: bg }}>
        <div style={{ fontWeight: 800, fontSize: 15 }}>{title}</div>
        {sub && <div className="small" style={{ color: 'var(--soft)' }}>{sub}</div>}
        {canAct && (
          <div className="row" style={{ marginTop: 4 }}>
            {showPreview && !reviewing && <button type="button" className="btn sm dark" onClick={() => setReviewing(true)}>Review and start</button>}
            {!showPreview && finished && <button type="button" className="btn sm on-grey" onClick={() => setReviewing(true)}>Run again</button>}
            {live && <button type="button" className="btn sm on-grey" disabled={busy} onClick={() => void act(() => api.cancelRun(r!.id), 'Runbook cancelled.')}>Cancel run</button>}
          </div>
        )}
      </div>
      {(showPreview ? preview.error : run.error) && <div className="small" style={{ color: 'var(--red)' }}>{showPreview ? preview.error : run.error}</div>}
      <ol className="plain" style={{ gap: 8 }}>
        {ordered.map((n, i) => {
          const v = nodeView(showPreview ? { ...n, status: undefined } : n, n.step_index ?? nodes.indexOf(n) ?? i, live);
          const rk = RISK[n.action ? n.risk : 'manual'];
          return (
            <li key={n.id} className="step">
              <span className="mark" style={{ color: v.markFg, background: v.markBg }}>{v.mark}</span>
              <div style={{ flex: 1, minWidth: 0 }}>
                <div className="wrap" style={{ fontWeight: 600, fontSize: 14 }}>{n.text}</div>
                <div className="mono small wrap" style={{ fontSize: 12, marginTop: 2 }}>{paramsLine(n)}</div>
                {v.note && <div className="wrap" style={{ fontSize: 12, fontWeight: 600, color: v.noteColor, marginTop: 4 }}>{v.note}</div>}
              </div>
              <Pill fg={rk.fg} bg={rk.bg}>{rk.label}</Pill>
              {canAct && v.canMarkDone && <button type="button" className="btn xs dark" disabled={busy} onClick={() => void act(() => api.nodeDone(r!.id, n.id), 'Marked done.')}>Mark done</button>}
            </li>
          );
        })}
      </ol>
      {canAct && reviewing && showPreview && (
        <div className="tile stack" style={{ gap: 8 }}>
          <div className="small" style={{ color: 'var(--soft)' }}>Starting it is your approval for every step above, with exactly these values. The steps then run in order without stopping. You can cancel at any time.</div>
          <div className="row">
            <button type="button" className="btn sm dark" disabled={busy || !p} onClick={() => void startRun()}>Start runbook</button>
            <button type="button" className="btn sm on-grey" onClick={() => setReviewing(false)}>Not now</button>
          </div>
        </div>
      )}
    </section>
  );
}
