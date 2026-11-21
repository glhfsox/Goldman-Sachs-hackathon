import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api, errorText } from '../api/client';
import type { Runbook } from '../api/types';
import { Plus } from '../components/Icons';
import { Shell } from '../components/Shell';
import { Pill } from '../components/Switch';
import { useToast } from '../components/Toast';
import { useLoad } from '../lib/useLoad';
import { ago, listText, RISK, RISK_RANK } from '../lib/ui';
import { useSession } from '../session';

type State = 'unaccepted' | 'accepted' | 'draft';
const stateOf = (r: Runbook): State => (r.status === 'draft' || !r.canonical ? 'draft' : r.accepted_by ? 'accepted' : 'unaccepted');

const STATUS: Record<State, { label: string; fg: string; bg: string; dot: string; group: string }> = {
  unaccepted: { label: 'Active · not accepted yet', fg: '#92400E', bg: '#FEF3C7', dot: '#F59E0B', group: 'Not accepted yet' },
  accepted: { label: 'Accepted', fg: '#15803D', bg: '#DCFCE7', dot: '#22C55E', group: 'Accepted' },
  draft: { label: 'Draft · being converted', fg: '#3F3F46', bg: '#E4E4E7', dot: '#A1A1AA', group: 'Converting or could not convert' },
};

const FORMATS: { id: string; label: string; field: string; placeholder: string }[] = [
  { id: 'yaml', label: 'YAML steps', field: 'Paste the YAML', placeholder: 'steps:\n  - action: retry_workflow\n    workflowId: "{{workflowId}}"' },
  { id: 'markdown', label: 'Markdown', field: 'Paste the Markdown page', placeholder: '# HighErrorRate on payments-api\n1. Check if errors are in one region only…' },
  { id: 'text', label: 'Free text', field: 'Paste the text', placeholder: 'When pricing latency goes over a second, it is usually the cache…' },
  { id: 'link', label: 'Wiki link', field: 'Link to the page', placeholder: 'https://wiki.example/…' },
];

export function RunbooksPage() {
  const session = useSession();
  const say = useToast();
  const [params, setParams] = useSearchParams();
  const runbooks = useLoad(() => api.runbooks(), []);
  const actions = useLoad(() => api.actions(), []);
  const rules = useLoad(() => api.rules(), []);
  const selectedId = params.get('id');
  const isNew = params.get('new') === '1';
  const [editing, setEditing] = useState(false);
  const [showSource, setShowSource] = useState(false);
  const [texts, setTexts] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [form, setForm] = useState({ title: '', team: session.myTeamIds[0] ?? '', format: 'text', source: '' });

  const list = runbooks.data ?? [];
  const current = list.find((r) => r.id === selectedId) ?? (isNew ? null : list[0] ?? null);
  const risks = new Map((actions.data ?? []).map((a) => [a.name, a.risk]));
  const mine = current ? session.isMine(current.team_id) : false;

  useEffect(() => {
    setEditing(false);
    setShowSource(false);
  }, [current?.id]);

  const pick = (id: string) => setParams({ id });
  const replace = (r: Runbook) => runbooks.setData((prev) => (prev ?? []).some((x) => x.id === r.id) ? (prev ?? []).map((x) => (x.id === r.id ? r : x)) : [r, ...(prev ?? [])]);

  const accept = async (r: Runbook) => {
    setBusy(true);
    try {
      replace(await api.acceptRunbook(r.id, r.version));
      say(`Version ${r.version} accepted. Its runs may now start by themselves where their actions allow it.`);
    } catch (e) {
      say(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  const saveSteps = async (r: Runbook) => {
    if (!r.canonical) return;
    setBusy(true);
    try {
      const canonical = { ...r.canonical, steps: r.canonical.steps.map((s, i) => ({ ...s, text: texts[i]?.trim() || s.text })) };
      const saved = await api.saveRunbook(r.id, { canonical });
      replace(saved);
      setEditing(false);
      say(`Saved as version ${saved.version}. Someone on the team accepts it next.`);
    } catch (e) {
      say(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  const create = async () => {
    if (!form.title.trim() || !form.source.trim() || !form.team) return say('Give it a name, a team and the text first.');
    setBusy(true);
    try {
      const r = await api.createRunbook({ team_id: form.team, title: form.title.trim(), format: form.format, source: form.source });
      replace(r);
      setForm({ ...form, title: '', source: '' });
      setParams({ id: r.id });
      say('Sent to the AI. It turns into steps in a few seconds.');
      setTimeout(() => void runbooks.reload(), 5000);
    } catch (e) {
      say(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  const groups = (['unaccepted', 'accepted', 'draft'] as State[]).map((s) => ({ s, items: list.filter((r) => stateOf(r) === s) })).filter((g) => g.items.length);

  const steps = current?.canonical?.steps ?? [];
  const stepRisk = (action?: string) => (action ? risks.get(action) ?? 'approval' : 'manual');
  const runRisk = steps.reduce<'read_only' | 'auto' | 'approval'>((m, s) => {
    const r = stepRisk(s.action);
    return r !== 'manual' && RISK_RANK[r] > RISK_RANK[m] ? r : m;
  }, 'read_only');
  const approvalActions = [...new Set(steps.filter((s) => stepRisk(s.action) === 'approval').map((s) => s.action!))];
  const RUN = {
    approval: ['Runs of this runbook need a person to start', `Because it contains ${listText(approvalActions)}. Whoever handles the alert reviews every step with its real values, then starts it. Once started, the steps run in order without stopping.`, '#FFFBEB'],
    auto: ['Runs can start by themselves', 'Its actions are automatic: after a 30 s cancel window, if the risk check clears it, and only once someone accepts this version.', '#F0FDF4'],
    read_only: ['Runs start at once', 'Its actions only read, so they run as soon as an alert opens.', '#E8F1FF'],
  } as const;
  const usedBy = (rules.data ?? []).filter((r) => r.runbook_id === current?.id);
  const fmt = FORMATS.find((f) => f.id === form.format) ?? FORMATS[2]!;

  return (
    <Shell side={
      <>
        <div className="head">
          <h2>Runbooks</h2>
          <button type="button" className={'plus' + (isNew ? ' on' : '')} aria-label="New runbook" title="New runbook" onClick={() => setParams({ new: '1' })}><Plus /></button>
        </div>
        {groups.map((g) => (
          <div key={g.s}>
            <h3>{STATUS[g.s].group}</h3>
            {g.items.map((r) => (
              <button key={r.id} type="button" className={'item' + (!isNew && current?.id === r.id ? ' on' : '')} onClick={() => pick(r.id)}>
                <span className="sdot" style={{ background: STATUS[g.s].dot }} /><span className="grow">{r.title}</span>
              </button>
            ))}
          </div>
        ))}
      </>
    }>
      <section className="content">
        {isNew && (
          <>
            <h1 className="h1">New runbook</h1>
            <p className="lead">Paste what your team already has, in its own format. The AI turns it into steps.</p>
            <div className="card">
              <div className="row" style={{ gap: 12, alignItems: 'flex-start' }}>
                <div className="stack" style={{ flex: '2 1 240px', gap: 6 }}><label htmlFor="rbname" style={{ fontSize: 14, fontWeight: 700 }}>Name</label><input id="rbname" className="field" placeholder="e.g. Payments region failover" value={form.title} onChange={(e) => setForm({ ...form, title: e.target.value })} /></div>
                <div className="stack" style={{ flex: '1 1 180px', gap: 6 }}><label htmlFor="rbteam" style={{ fontSize: 14, fontWeight: 700 }}>Team</label>
                  <select id="rbteam" className="field" value={form.team} onChange={(e) => setForm({ ...form, team: e.target.value })}>
                    {session.teams.filter((t) => session.isMine(t.id)).map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
                  </select>
                </div>
              </div>
              <div className="stack" style={{ gap: 8 }}>
                <span style={{ fontSize: 14, fontWeight: 700 }}>Format</span>
                <div role="group" aria-label="Format" className="row">
                  {FORMATS.map((f) => <button key={f.id} type="button" aria-pressed={form.format === f.id} className={'chip' + (form.format === f.id ? ' on' : '')} onClick={() => setForm({ ...form, format: f.id })}>{f.label}</button>)}
                </div>
              </div>
              <div className="stack" style={{ gap: 6 }}>
                <label htmlFor="rbsrc" style={{ fontSize: 14, fontWeight: 700 }}>{fmt.field}</label>
                {form.format === 'link'
                  ? <input id="rbsrc" className="field" placeholder={fmt.placeholder} value={form.source} onChange={(e) => setForm({ ...form, source: e.target.value })} />
                  : <textarea id="rbsrc" rows={8} className="area" placeholder={fmt.placeholder} value={form.source} onChange={(e) => setForm({ ...form, source: e.target.value })} />}
                <span className="small">Use {'{{workflowId}}'} to insert values from the alert, like the workflow id or service name.</span>
              </div>
            </div>
            <div className="row" style={{ justifyContent: 'flex-end' }}>
              <button type="button" className="btn light" onClick={() => setParams({})}>Cancel</button>
              <button type="button" className="btn dark" disabled={busy} onClick={() => void create()}>Turn into steps</button>
            </div>
          </>
        )}

        {!isNew && !current && !runbooks.loading && <div className="empty">No runbooks yet. Add one with ＋.</div>}

        {!isNew && current && (
          <>
            <div className="row">
              <Pill fg={STATUS[stateOf(current)].fg} bg={STATUS[stateOf(current)].bg}>{STATUS[stateOf(current)].label}</Pill>
              <Pill fg="#111" bg="#F4F4F6">{current.format}</Pill>
              <span className="small">Team {session.teamName(current.team_id)} · version {current.version}{current.accepted_by ? ` · accepted by ${session.userName(current.accepted_by)}` : ''}</span>
            </div>
            <h1 className="h1">{current.title}</h1>

            {stateOf(current) === 'draft' && (
              <div className="banner" style={{ background: '#F4F4F6', fontSize: 15, lineHeight: 1.5, display: 'block' }}>
                <strong>Not converted yet.</strong> The AI turns it into steps within seconds. If it stays here, the AI could not map it to actions we can run; paste a clearer version as a new runbook.
              </div>
            )}

            {steps.length > 0 && (
              <>
                <div className="banner" style={{ background: RUN[runRisk][2] }}>
                  <div style={{ fontWeight: 800, fontSize: 15 }}>{RUN[runRisk][0]}</div>
                  <div style={{ fontSize: 14, color: 'var(--soft)', lineHeight: 1.5 }}>{RUN[runRisk][1]}</div>
                </div>
                <ol className="plain">
                  {steps.map((s, i) => {
                    const r = RISK[stepRisk(s.action)];
                    const detail = [s.action, ...Object.entries(s.params ?? {}).map(([k, v]) => `${k} ${typeof v === 'string' ? v : JSON.stringify(v)}`)].filter(Boolean).join(' · ') || (s.link ? s.link : 'Manual step');
                    return (
                      <li key={i} className="card" style={{ flexDirection: 'row', gap: 12, alignItems: 'flex-start', padding: '16px 18px', borderRadius: 20 }}>
                        <span className="mark" style={{ background: '#111', color: '#fff' }}>{i + 1}</span>
                        <div style={{ flex: 1, minWidth: 0 }}>
                          {editing
                            ? <input className="field" style={{ width: '100%', fontWeight: 700 }} aria-label={`Step ${i + 1} text`} value={texts[i] ?? s.text} onChange={(e) => setTexts(texts.map((t, j) => (j === i ? e.target.value : t)))} />
                            : <div className="wrap" style={{ fontWeight: 700, fontSize: 15 }}>{s.text}</div>}
                          <div className="small wrap" style={{ marginTop: 2 }}>{s.when ? `${s.when} · ` : ''}{detail}</div>
                        </div>
                        <Pill fg={r.fg} bg={r.bg}>{r.label}</Pill>
                      </li>
                    );
                  })}
                </ol>
              </>
            )}

            {showSource && !editing && <div className="dark-box"><div className="label" style={{ color: '#A1A1AA', marginBottom: 8 }}>Source ({current.format})</div><pre>{current.source}</pre></div>}

            {mine ? (
              <div className="row">
                {stateOf(current) === 'unaccepted' && !editing && <button type="button" className="btn dark" disabled={busy} onClick={() => void accept(current)}>Accept version {current.version}</button>}
                {!editing && steps.length > 0 && <button type="button" className="btn light" onClick={() => { setTexts(steps.map((s) => s.text)); setEditing(true); }}>Edit steps</button>}
                {!editing && <button type="button" className="btn light" onClick={() => setShowSource(!showSource)}>{showSource ? 'Hide source' : 'View source'}</button>}
                {editing && (
                  <>
                    <button type="button" className="btn dark" disabled={busy} onClick={() => void saveSteps(current)}>Save as version {current.version + 1}</button>
                    <button type="button" className="btn light" onClick={() => setEditing(false)}>Cancel</button>
                  </>
                )}
              </div>
            ) : (
              <div className="row">
                <button type="button" className="btn light" onClick={() => setShowSource(!showSource)}>{showSource ? 'Hide source' : 'View source'}</button>
                <span className="small">Only {session.teamName(current.team_id)} can change it.</span>
              </div>
            )}
            {editing && <p className="small">Saving makes a new version. Someone on the team accepts it before its runs can start by themselves.</p>}
          </>
        )}
      </section>

      <aside className="panel" aria-label="Runbook details">
        {isNew ? (
          <>
            <h2 className="h3" style={{ fontSize: 18 }}>How it works</h2>
            {[
              ['Paste it', 'YAML steps, a Markdown page, free text from a chat, or a wiki link.'],
              ['The AI turns it into steps', 'Only actions we know how to run. Anything else stays a manual step.'],
              ['Someone on the team accepts it', 'It shows on alerts at once; it starts by itself only after a person accepts this version.'],
            ].map(([t, x], i) => (
              <div key={t} className="row" style={{ gap: 12, alignItems: 'flex-start', flexWrap: 'nowrap' }}>
                <span className="mark" style={{ background: '#111', color: '#fff' }}>{i + 1}</span>
                <div><div style={{ fontWeight: 700, fontSize: 15 }}>{t}</div><div className="small" style={{ marginTop: 2 }}>{x}</div></div>
              </div>
            ))}
          </>
        ) : current && (
          <>
            <div className="stack" style={{ gap: 8 }}>
              <h2 className="h3">Used by</h2>
              {usedBy.length ? usedBy.map((r) => (
                <Link key={r.id} to={`/rules/${encodeURIComponent(r.id)}`} className="tile" style={{ color: 'var(--ink)', textDecoration: 'none', fontWeight: 600, fontSize: 14 }}>{r.name || r.id} ({session.teamName(r.team_id)})</Link>
              )) : <div className="small">No rule names it yet. Pick it in a rule’s “What to do”.</div>}
            </div>
            <div className="stack" style={{ gap: 6 }}>
              <h2 className="h3">History</h2>
              <div style={{ fontSize: 14, color: 'var(--soft)' }}>Version {current.version}, last changed {ago(current.updated_at)}</div>
              {current.accepted_by && <div style={{ fontSize: 14, color: 'var(--soft)' }}>Accepted by {session.userName(current.accepted_by)} {ago(current.accepted_at)}</div>}
            </div>
            <div className="stack" style={{ gap: 8 }}>
              <h2 className="h3">What the badges mean</h2>
              {(['read_only', 'auto', 'approval', 'manual'] as const).map((k) => (
                <div key={k} className="row" style={{ gap: 10, alignItems: 'flex-start', flexWrap: 'nowrap', fontSize: 14, color: 'var(--soft)', lineHeight: 1.5 }}>
                  <Pill fg={RISK[k].fg} bg={RISK[k].bg} style={{ flex: 'none' }}>{RISK[k].label}</Pill><span>{RISK[k].meaning}</span>
                </div>
              ))}
              <div className="small">The risk belongs to the action, not to the runbook. A rule can make it stricter, never looser.</div>
            </div>
          </>
        )}
      </aside>
    </Shell>
  );
}
