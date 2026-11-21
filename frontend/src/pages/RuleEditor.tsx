import { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { api, ApiError, errorText } from '../api/client';
import { CHANNELS, SEVERITIES, type Channel, type Condition, type Leaf, type Recipient, type Rule, type Severity } from '../api/types';
import { Shell } from '../components/Shell';
import { Switch } from '../components/Switch';
import { useToast } from '../components/Toast';
import { describe, fieldLabel, isLeaf, OPS } from '../lib/conditions';
import { childrenOf, fillTemplate, makeGroup, modeOf, normalize, slug, templateRule, valueText } from '../lib/ruleForm';
import { channelsText, whoText, WINDOWS, windowText } from '../lib/ruleText';
import { useLoad } from '../lib/useLoad';
import { cap, CHANNEL_LABEL, COLORS, listText, RISK_RANK, SEVERITY } from '../lib/ui';
import { useSession } from '../session';

const FIELD_SUGGESTIONS = ['type', 'severity', 'env', 'service', 'source', 'raw.workflowName', 'raw.error.code', 'raw.step', 'payload.desk', 'payload.notional', 'labels.service'];
const SAMPLE: Record<string, string> = { workflowName: 'order-settlement', step: 'settle', 'error.code': 'TIMEOUT', 'error.message': 'Counterparty service did not respond in 5000 ms', type: 'WORKFLOW_FAILED', service: 'order-settlement', env: 'prod' };

export function RuleEditorPage() {
  const { id } = useParams();
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const session = useSession();
  const say = useToast();
  const isNew = !id;
  const teamParam = params.get('team') || session.myTeamIds[0] || '';
  const [rule, setRule] = useState<Rule | null>(isNew ? templateRule(teamParam, params.get('template')) : null);
  const [problems, setProblems] = useState<{ path: string; message: string }[]>([]);
  const [showJson, setShowJson] = useState(false);
  const [saving, setSaving] = useState(false);
  const [keyDraft, setKeyDraft] = useState('');
  const runbooks = useLoad(() => api.runbooks(), []);
  const actions = useLoad(() => api.actions(), []);

  useEffect(() => {
    if (isNew) return;
    api.rule(id!).then(setRule).catch((e) => say(errorText(e)));
  }, [id, isNew, say]);

  const mine = rule ? session.isMine(rule.team_id) : false;
  const team = session.teams.find((t) => t.id === rule?.team_id);
  const payload = useMemo(() => (rule ? { ...rule, when: normalize(rule.when) } : null), [rule]);

  if (!rule || !payload) {
    return <Shell side={<h2>Rules</h2>}><section className="content"><p className="lead">Loading the rule…</p></section></Shell>;
  }

  const set = (patch: Partial<Rule>) => setRule({ ...rule, ...patch });
  const mode = modeOf(rule.when);
  const top = childrenOf(rule.when);
  const setTop = (children: Condition[], m = mode) => set({ when: makeGroup(m, children) });
  const updateAt = (i: number, next: Condition | null) => setTop(next ? top.map((c, j) => (j === i ? next : c)) : top.filter((_, j) => j !== i));

  const save = async () => {
    setSaving(true);
    setProblems([]);
    const body: Rule = { ...payload, id: rule.id || slug(`${rule.team_id}-${rule.name || 'rule'}`) };
    try {
      await (isNew ? api.createRule(body) : api.saveRule(body));
      say(isNew ? 'Rule created. It applies to the next event within seconds.' : 'Rule saved. It applies to the next event within seconds.');
      navigate(`/rules?team=${encodeURIComponent(rule.team_id)}`);
    } catch (e) {
      if (e instanceof ApiError && e.details.length) setProblems(e.details);
      say(errorText(e));
    } finally {
      setSaving(false);
    }
  };

  // ---- runbook start explanation (risk = the highest risk of its actions)
  const teamRunbooks = (runbooks.data ?? []).filter((r) => r.team_id === rule.team_id && r.status === 'active');
  const rb = teamRunbooks.find((r) => r.id === rule.runbook_id) ?? (runbooks.data ?? []).find((r) => r.id === rule.runbook_id);
  const riskOfAction = new Map((actions.data ?? []).map((a) => [a.name, a.risk]));
  const rbActions = (rb?.canonical?.steps ?? []).filter((s) => s.action).map((s) => ({ name: s.action!, risk: riskOfAction.get(s.action!) ?? 'approval' }));
  const rbRisk = rbActions.reduce<'read_only' | 'auto' | 'approval'>((m, a) => (RISK_RANK[a.risk] > RISK_RANK[m] ? a.risk : m), 'read_only');
  const START = {
    approval: ['Runs wait for a person', `Because it contains ${listText([...new Set(rbActions.filter((a) => a.risk === 'approval').map((a) => a.name))])}. Whoever handles the alert reviews the steps and starts the run.`],
    auto: ['Runs start by themselves after 30 s', 'Its actions are automatic. Unless someone cancels, or the risk check holds the run for a person.'],
    read_only: ['Runs start at once', 'Its actions only read, so they run as soon as the alert opens.'],
  } as const;

  // ---- recipients
  const r0 = rule.route.recipients[0];
  const whoMode = !r0 ? 'oncall' : 'user' in r0 ? 'user' : r0.on_call ? 'oncall' : 'team';
  const setWho = (m: 'oncall' | 'team' | 'user', userId?: string) => {
    const rec: Recipient = m === 'user' ? { user: userId ?? team?.members[0]?.user_id ?? session.user!.id } : m === 'oncall' ? { team: rule.team_id, on_call: true } : { team: rule.team_id };
    set({ route: { ...rule.route, recipients: [rec] } });
  };
  const channels = rule.route.channels ?? null;
  const toggleChannel = (c: Channel) => {
    const cur = channels ?? [];
    set({ route: { ...rule.route, channels: cur.includes(c) ? cur.filter((x) => x !== c) : [...cur, c] } });
  };

  const color = COLORS[rule.present?.color ?? 'red'] ?? COLORS.red!;
  const title = rule.present?.title || rule.name || '{{type}} · {{service}}';
  const titlePreview = fillTemplate(title, SAMPLE);
  const sevText = rule.severity ? `a ${rule.severity}` : 'an';
  const summary = `When an event has ${describe(payload.when)}, open ${sevText} alert and notify ${whoText(rule, session.teamName, session.userName)} by ${channelsText(rule)}. Repeats within ${windowText(rule.window_s ?? 300)} only raise its counter.${rb ? ` It shows the runbook “${rb.title}”.` : ''}`;
  const readOnly = !mine;

  const leafRow = (leaf: Leaf, onChange: (l: Leaf | null) => void, joiner: string, key: string) => (
    <div key={key} style={{ display: 'grid', gridTemplateColumns: '44px minmax(0,1.2fr) minmax(0,1fr) minmax(0,1.4fr) 44px', gap: 8, alignItems: 'center' }}>
      <span style={{ fontSize: 12, fontWeight: 800, color: 'var(--muted)' }}>{joiner}</span>
      <input className="field" list="rule-fields" aria-label="Field" value={leaf.field} disabled={readOnly} onChange={(e) => onChange({ ...leaf, field: e.target.value })} />
      <select className="field" aria-label="Condition" value={leaf.op} disabled={readOnly} onChange={(e) => onChange({ ...leaf, op: e.target.value })}>
        {OPS.map((o) => <option key={o.op} value={o.op}>{o.label}</option>)}
      </select>
      {leaf.op === 'exists' ? <span /> : (
        <input className="field" aria-label="Value" value={valueText(leaf.value)} disabled={readOnly}
          placeholder={leaf.op === 'in' ? 'A, B, C' : ''} onChange={(e) => onChange({ ...leaf, value: e.target.value })} />
      )}
      {!readOnly ? <button type="button" className="icon-btn" style={{ width: 44, height: 44, background: '#fff' }} aria-label="Remove condition" onClick={() => onChange(null)}>✕</button> : <span />}
    </div>
  );

  return (
    <Shell side={
      <>
        <Link to={`/rules?team=${encodeURIComponent(rule.team_id)}`} className="item" style={{ color: 'var(--side-muted)', fontWeight: 600 }}>← # {team?.name ?? rule.team_id} rules</Link>
        <div>
          <h3>Steps</h3>
          {[['when', 'When'], ['severity', 'Severity'], ['repeats', 'Repeats'], ['notify', 'Notify'], ['looks', 'How it looks'], ['todo', 'What to do']].map(([k, l], i) => (
            <a key={k} className="item" href={`#${k}`}><span className="count" style={{ background: '#3F3F46' }}>{i + 1}</span><span className="grow">{l}</span></a>
          ))}
        </div>
      </>
    }>
      <section className="content" style={{ gap: 16 }}>
        <datalist id="rule-fields">{FIELD_SUGGESTIONS.map((f) => <option key={f} value={f} />)}</datalist>
        <div className="row" style={{ gap: 12 }}>
          <label htmlFor="rname" className="sr-only">Rule name</label>
          <input id="rname" className="title-input" placeholder="Name this rule" value={rule.name ?? ''} disabled={readOnly} onChange={(e) => set({ name: e.target.value })} />
          <button type="button" className="btn sm light" onClick={() => setShowJson(!showJson)}>{showJson ? 'Hide JSON' : 'Advanced: JSON'}</button>
        </div>
        {readOnly && <div className="tile small">You are not in {team?.name}, so this rule is read-only for you.</div>}
        {problems.length > 0 && (
          <div className="banner" style={{ background: 'var(--red-soft)', color: '#7F1D1D' }}>
            <strong>The API did not accept the rule:</strong>
            <ul style={{ margin: 0, paddingLeft: 18 }}>{problems.map((p, i) => <li key={i}><code>{p.path}</code> {p.message}</li>)}</ul>
          </div>
        )}
        {showJson && <div className="dark-box"><pre>{JSON.stringify({ ...payload, id: rule.id || slug(`${rule.team_id}-${rule.name || 'rule'}`) }, null, 2)}</pre></div>}

        <section id="when" className="card">
          <h2 className="h2">When an event matches</h2>
          <div role="group" aria-label="Match all or any" className="row" style={{ gap: 6 }}>
            {(['all', 'any'] as const).map((m) => (
              <button key={m} type="button" aria-pressed={mode === m} className={'chip' + (mode === m ? ' on' : '')} disabled={readOnly} onClick={() => setTop(top, m)}>{m} of these</button>
            ))}
          </div>
          {top.map((c, i) => {
            const joiner = i === 0 ? 'IF' : mode === 'all' ? 'AND' : 'OR';
            if (isLeaf(c)) return leafRow(c, (l) => updateAt(i, l), joiner, `leaf-${i}`);
            const gMode = modeOf(c);
            const kids = childrenOf(c);
            const setKids = (k: Condition[], gm = gMode) => updateAt(i, k.length ? makeGroup(gm, k) : null);
            return (
              <div key={`group-${i}`} className="white stack" style={{ marginLeft: 52, borderRadius: 18 }}>
                <div className="row">
                  <span style={{ fontSize: 12, fontWeight: 800, color: 'var(--muted)' }}>{joiner}</span>
                  {(['all', 'any'] as const).map((m) => (
                    <button key={m} type="button" className={'chip sm grey' + (gMode === m ? ' on' : '')} disabled={readOnly} onClick={() => setKids(kids, m)}>{m} of</button>
                  ))}
                  <span style={{ flex: 1 }} />
                  {!readOnly && <button type="button" className="icon-btn" style={{ width: 32, height: 32 }} aria-label="Remove group" onClick={() => updateAt(i, null)}>✕</button>}
                </div>
                {kids.map((k, j) => (isLeaf(k)
                  ? leafRow(k, (l) => setKids(l ? kids.map((x, n) => (n === j ? l : x)) : kids.filter((_, n) => n !== j)), j === 0 ? '' : gMode === 'all' ? 'AND' : 'OR', `g-${i}-${j}`)
                  : <div key={j} className="small mono">{JSON.stringify(k)}</div>))}
                {!readOnly && <div><button type="button" className="chip sm grey" onClick={() => setKids([...kids, { field: 'severity', op: 'sev_gte', value: 'critical' }])}>+ Condition</button></div>}
              </div>
            );
          })}
          {!readOnly && (
            <div className="row">
              <button type="button" className="chip" onClick={() => setTop([...top, { field: 'severity', op: 'sev_gte', value: 'error' }])}>+ Condition</button>
              <button type="button" className="chip" onClick={() => setTop([...top, { any: [{ field: 'severity', op: 'sev_gte', value: 'critical' }, { field: 'raw.error.code', op: 'in', value: 'TIMEOUT, REJECTED' }] }])}>+ Group (and / or)</button>
            </div>
          )}
        </section>

        <section id="severity" className="card">
          <h2 className="h2">Severity of its alerts</h2>
          <p className="lead">Events say how bad they are, but not always the way your team sees it.</p>
          <div className="row">
            <button type="button" aria-pressed={!rule.severity} className={'chip' + (!rule.severity ? ' on' : '')} disabled={readOnly} onClick={() => set({ severity: null })}>Take it from the event</button>
            <button type="button" aria-pressed={!!rule.severity} className={'chip' + (rule.severity ? ' on' : '')} disabled={readOnly} onClick={() => set({ severity: rule.severity ?? 'critical' })}>Always set it to</button>
            {rule.severity && (
              <select className="field" aria-label="Severity" value={rule.severity} disabled={readOnly} onChange={(e) => set({ severity: e.target.value as Severity })}>
                {SEVERITIES.map((s) => <option key={s} value={s}>{SEVERITY[s].label}</option>)}
              </select>
            )}
          </div>
          <p className="small">{rule.severity ? `Every alert of this rule is ${rule.severity}, whatever the event said. Conditions above still see the event’s own severity.` : 'Each alert takes the event’s severity and keeps the highest one seen.'}</p>
        </section>

        <section id="repeats" className="card" style={{ gap: 12 }}>
          <h2 className="h2">Treat repeats as one alert</h2>
          <p className="lead">The same problem again only raises the counter. Nobody gets another message.</p>
          <div className="row" style={{ fontSize: 15 }}>Same problem = same
            {(rule.alert_key ?? []).map((k) => (
              <button key={k} type="button" className="chip sm on" disabled={readOnly} aria-label={`Stop grouping by ${fieldLabel(k)}`} onClick={() => set({ alert_key: (rule.alert_key ?? []).filter((x) => x !== k) })}>{fieldLabel(k)} ✕</button>
            ))}
            {!readOnly && (
              <form className="row" style={{ gap: 6 }} onSubmit={(e) => { e.preventDefault(); if (keyDraft.trim()) { set({ alert_key: [...(rule.alert_key ?? []), keyDraft.trim()] }); setKeyDraft(''); } }}>
                <input className="field" list="rule-fields" aria-label="Add a field to group by" placeholder="+ field" value={keyDraft} onChange={(e) => setKeyDraft(e.target.value)} style={{ height: 34, width: 150 }} />
              </form>
            )}
          </div>
          <div className="row" style={{ fontSize: 15 }}>
            <label htmlFor="win">Close it automatically after</label>
            <select id="win" className="field" value={rule.window_s ?? 300} disabled={readOnly} onChange={(e) => set({ window_s: Number(e.target.value) })}>
              {[...new Set([...WINDOWS, rule.window_s ?? 300])].sort((a, b) => a - b).map((w) => <option key={w} value={w}>{windowText(w)}</option>)}
            </select>
            without repeats.
          </div>
        </section>

        <section id="notify" className="card">
          <h2 className="h2">Notify</h2>
          <div className="row">
            <button type="button" className={'chip' + (whoMode === 'oncall' ? ' on' : '')} disabled={readOnly} onClick={() => setWho('oncall')}>{team?.name ?? 'Team'} on-call person</button>
            <button type="button" className={'chip' + (whoMode === 'team' ? ' on' : '')} disabled={readOnly} onClick={() => setWho('team')}>Whole {team?.name ?? 'team'} team</button>
            <button type="button" className={'chip' + (whoMode === 'user' ? ' on' : '')} disabled={readOnly} onClick={() => setWho('user')}>A specific person</button>
            {r0 && 'user' in r0 && (
              <select className="field" aria-label="Person" value={r0.user} disabled={readOnly} onChange={(e) => setWho('user', e.target.value)}>
                {session.users.map((u) => <option key={u.id} value={u.id}>{u.name}</option>)}
              </select>
            )}
          </div>
          <div role="group" aria-label="How" className="row">
            <button type="button" className={'chip' + (!channels ? ' on' : '')} disabled={readOnly} onClick={() => set({ route: { ...rule.route, channels: null } })}>Their own settings</button>
            <button type="button" className={'chip' + (channels ? ' on' : '')} disabled={readOnly} onClick={() => set({ route: { ...rule.route, channels: channels ?? ['ui'] } })}>Always these channels</button>
          </div>
          {channels && (
            <div className="row">
              {CHANNELS.map((c) => (
                <button key={c} type="button" aria-pressed={channels.includes(c)} className={'chip' + (channels.includes(c) ? ' on' : '')} disabled={readOnly} onClick={() => toggleChannel(c)}>{channels.includes(c) ? '✓ ' : ''}{CHANNEL_LABEL[c]}</button>
              ))}
            </div>
          )}
          <p className="small" style={{ fontSize: 14 }}>Personal “do not disturb” hours still apply: a call becomes an SMS, an SMS becomes an email.</p>
        </section>

        <section id="looks" className="card">
          <h2 className="h2">How the alert looks</h2>
          <label htmlFor="title" style={{ fontSize: 14, fontWeight: 700 }}>Title</label>
          <input id="title" className="field" value={rule.present?.title ?? ''} placeholder={rule.name || '{{type}} · {{service}}'} disabled={readOnly} onChange={(e) => set({ present: { ...rule.present, title: e.target.value } })} />
          {!readOnly && (
            <div className="row small" style={{ gap: 6, fontSize: 14 }}>Insert from the event:
              {['type', 'service', 'env', 'workflowName', 'step', 'error.code'].map((k) => (
                <button key={k} type="button" className="chip sm" onClick={() => set({ present: { ...rule.present, title: `${rule.present?.title ?? ''} {{${k}}}`.trim() } })}>{k}</button>
              ))}
            </div>
          )}
          <label htmlFor="body" style={{ fontSize: 14, fontWeight: 700 }}>Text under the title</label>
          <input id="body" className="field" value={rule.present?.body ?? ''} placeholder="e.g. {{error.message}}" disabled={readOnly} onChange={(e) => set({ present: { ...rule.present, body: e.target.value } })} />
          <div className="row"><span style={{ fontSize: 14, fontWeight: 700, marginRight: 4 }}>Colour</span>
            {Object.keys(COLORS).map((c) => (
              <button key={c} type="button" aria-pressed={rule.present?.color === c} className={'chip' + (rule.present?.color === c ? ' on' : '')} disabled={readOnly} onClick={() => set({ present: { ...rule.present, color: c } })}>
                <span aria-hidden className="sdot" style={{ width: 12, height: 12, background: COLORS[c]![0] }} />{cap(c)}
              </button>
            ))}
          </div>
        </section>

        <section id="todo" className="card">
          <h2 className="h2">What to do</h2>
          <div className="row">
            <label htmlFor="rb" style={{ fontSize: 15 }}>Show this runbook on its alerts</label>
            <select id="rb" className="field" style={{ minWidth: 260 }} value={rule.runbook_id ?? ''} disabled={readOnly} onChange={(e) => set({ runbook_id: e.target.value || null })}>
              <option value="">No runbook</option>
              {teamRunbooks.map((r) => <option key={r.id} value={r.id}>{r.title} · {r.canonical?.steps.length ?? 0} steps</option>)}
              {rb && !teamRunbooks.includes(rb) && <option value={rb.id}>{rb.title}</option>}
            </select>
            <Link to="/runbooks" style={{ fontWeight: 700, fontSize: 14, textDecoration: 'none' }}>Open runbooks</Link>
          </div>
          {rb && (
            <>
              <div className="white">
                <div style={{ fontWeight: 800, fontSize: 15 }}>{START[rbRisk][0]}</div>
                <div style={{ fontSize: 14, color: 'var(--soft)', lineHeight: 1.5, marginTop: 4 }}>{START[rbRisk][1]}</div>
              </div>
              {rbRisk !== 'approval' && (
                <div className="row" style={{ gap: 12, flexWrap: 'nowrap' }}>
                  <div style={{ flex: 1 }}>
                    <div style={{ fontWeight: 700, fontSize: 15 }}>Always wait for a person before its actions run</div>
                    <div className="small">Stricter than the runbook’s default, for this rule only. Comes with runbook runs in the API (#73).</div>
                  </div>
                  <Switch on={false} disabled label="Always wait for a person" onChange={() => {}} />
                </div>
              )}
            </>
          )}
        </section>

        {mine && (
          <div className="row" style={{ justifyContent: 'flex-end' }}>
            <Link to={`/rules?team=${encodeURIComponent(rule.team_id)}`} className="btn light" style={{ height: 52, padding: '0 24px' }}>Cancel</Link>
            <button type="button" className="btn dark" style={{ height: 52, padding: '0 28px' }} disabled={saving} onClick={() => void save()}>{saving ? 'Saving…' : isNew ? 'Create rule' : 'Save rule'}</button>
          </div>
        )}
      </section>

      <aside className="panel" aria-label="Rule summary" style={{ alignSelf: 'stretch' }}>
        <div className="stack" style={{ gap: 8 }}>
          <span className="label">In plain words</span>
          <p style={{ margin: 0, fontSize: 18, fontWeight: 600, lineHeight: 1.5, letterSpacing: '-0.01em' }}>{summary}</p>
        </div>
        <div className="stack">
          <span className="label">Preview</span>
          <div className="row" style={{ gap: 12, flexWrap: 'nowrap', borderRadius: 20, padding: '14px 16px', background: color[1] }}>
            <span className="sev-icon" style={{ width: 40, height: 40, color: '#fff', background: color[0] }} aria-hidden>!</span>
            <div style={{ flex: 1, minWidth: 0 }}>
              <div className="wrap" style={{ fontWeight: 800, fontSize: 15 }}>{titlePreview}</div>
              <div className="small" style={{ marginTop: 4 }}>{rule.severity ? cap(rule.severity) : 'Critical (from the event)'} · {fillTemplate(rule.present?.body || 'Counterparty service did not respond in 5000 ms', SAMPLE)}</div>
            </div>
          </div>
          <div className="small">As SMS: “{(rule.severity ?? 'critical').toUpperCase()} {titlePreview} relay.example/a/…”</div>
        </div>
      </aside>
    </Shell>
  );
}
