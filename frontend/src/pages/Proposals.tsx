import { useState } from 'react';
import { Link } from 'react-router-dom';
import { api, errorText } from '../api/client';
import type { Proposal, Rule } from '../api/types';
import { Shell } from '../components/Shell';
import { Pill } from '../components/Switch';
import { useToast } from '../components/Toast';
import { useLoad } from '../lib/useLoad';
import { ago, cap, RISK } from '../lib/ui';
import { useSession } from '../session';

const KIND_LABEL: Record<Proposal['kind'], string> = {
  rule_severity: 'Rule severity', runbook: 'New runbook', runbook_start: 'How runs start', threshold: 'When it fires', template: 'How it looks',
};

interface Card {
  title: string;
  target: string;
  now: string | null;
  next: string | null;
  reasons: string[];
  confidence: number | null;
  steps: { text: string; risk: keyof typeof RISK }[];
  effect: string;
  blocked: boolean;
  teamId: string | null;
}

/** What a proposal says, per kind (contracts/api.md: the body shape depends on the kind). */
function cardOf(p: Proposal, rules: Map<string, Rule>, risks: Map<string, keyof typeof RISK>): Card {
  const b = p.body ?? {};
  const ruleName = (id: string | undefined) => (id ? rules.get(id)?.name || id : 'its rule');
  const reasons: string[] = Array.isArray(b.reasons) ? b.reasons.map(String) : [];
  const base = { reasons, confidence: typeof b.confidence === 'number' ? b.confidence : null, steps: [], blocked: false, teamId: (b.team_id as string) ?? null };
  switch (p.kind) {
    case 'rule_severity':
      return { ...base, title: `Make “${ruleName(b.rule_id)}” always ${b.severity}`, target: `Rule “${ruleName(b.rule_id)}”`,
        now: b.current_severity ? `Always ${b.current_severity}` : 'Severity from the event', next: `Always ${b.severity}`,
        effect: 'Accept changes the rule right away. Only new alerts get the new severity.' };
    case 'runbook': {
      const steps = (b.canonical?.steps ?? []).map((s: { text: string; action?: string }) => ({ text: s.text, risk: s.action ? risks.get(s.action) ?? 'approval' : 'manual' }));
      return { ...base, title: `Save “${b.title}” as a runbook`, target: 'Learned from how people fixed this alert',
        now: 'No runbook for this kind of alert', next: `A runbook with the ${steps.length} step${steps.length === 1 ? '' : 's'} people ran`, steps,
        effect: 'Accept saves it as an active runbook, accepted by you. It then shows on alerts of this kind.' };
    }
    case 'runbook_start':
      return { ...base, title: `Let runs of “${b.runbook_id}” ${b.start === 'auto' ? 'start by themselves' : 'wait for a person'}`,
        target: `Runbook “${b.runbook_id}” on rule “${ruleName(b.rule_id)}”`,
        now: b.current_start === 'auto' ? 'Starts by itself' : 'Waits for a person', next: b.start === 'auto' ? 'Starts by itself after the cancel window' : 'Waits for a person',
        effect: 'Not available yet: the API accepts this once runbook runs are built (#73).', blocked: true };
    default:
      return { ...base, title: String(b.text ?? 'A suggested change'), target: `Rule “${ruleName(b.rule_id)}”`, now: null, next: null,
        effect: 'Accept marks it accepted. Someone on the team then changes the rule.' };
  }
}

export function ProposalsPage() {
  const session = useSession();
  const say = useToast();
  const [tab, setTab] = useState<'open' | 'decided'>('open');
  const [from, setFrom] = useState<'all' | 'ai' | 'people'>('all');
  const proposals = useLoad(() => api.proposals(), []);
  const rules = useLoad(() => api.rules(), []);
  const actions = useLoad(() => api.actions(), []);
  const ruleMap = new Map((rules.data ?? []).map((r) => [r.id, r]));
  const riskMap = new Map((actions.data ?? []).map((a) => [a.name, a.risk] as const));

  const all = proposals.data ?? [];
  const byTab = (p: Proposal) => (tab === 'open' ? p.status === 'open' : p.status !== 'open');
  const byFrom = (p: Proposal) => from === 'all' || (from === 'ai' ? p.author === 'ai-worker' : p.author !== 'ai-worker');
  const visible = all.filter(byTab).filter(byFrom);
  const openN = all.filter((p) => p.status === 'open').length;

  const decide = async (p: Proposal, how: 'accept' | 'reject') => {
    try {
      const updated = how === 'accept' ? await api.acceptProposal(p.id) : await api.rejectProposal(p.id);
      proposals.setData((prev) => (prev ?? []).map((x) => (x.id === p.id ? updated : x)));
      say(how === 'accept' ? 'Accepted.' : 'Rejected.');
    } catch (e) {
      say(errorText(e));
    }
  };

  return (
    <Shell side={
      <>
        <h2>Proposals</h2>
        <div>
          {(['open', 'decided'] as const).map((t) => (
            <button key={t} type="button" className={'item' + (tab === t ? ' on' : '')} onClick={() => setTab(t)}>
              <span className="grow">{cap(t)}</span>
              {t === 'open' && openN > 0 && <span className="count warn">{openN}</span>}
            </button>
          ))}
        </div>
        <div>
          <h3>From</h3>
          {([['all', 'Everyone'], ['ai', 'The AI'], ['people', 'People']] as const).map(([k, l]) => (
            <button key={k} type="button" className={'item' + (from === k ? ' on' : '')} onClick={() => setFrom(k)}><span className="grow">{l}</span></button>
          ))}
        </div>
      </>
    }>
      <section className="content">
        <h1 className="h1">{tab === 'open' ? 'Open proposals' : 'Decided'}</h1>
        <p className="lead">{tab === 'open' ? 'Suggested changes to rules and runbooks. Nothing changes until someone on the team accepts.' : 'Every decision is also on the alert’s timeline.'}</p>
        {proposals.error && <div className="banner" style={{ background: 'var(--red-soft)', color: '#7F1D1D' }}>{proposals.error}</div>}
        {visible.map((p) => {
          const c = cardOf(p, ruleMap, riskMap);
          const ai = p.author === 'ai-worker';
          const canDecide = !c.teamId || session.isMine(c.teamId);
          return (
            <article key={p.id} className="card">
              <div className="row">
                <Pill fg="#111" bg="#fff">{KIND_LABEL[p.kind]}</Pill>
                <Pill fg={ai ? '#5B21B6' : '#1A56DB'} bg={ai ? '#F3E8FF' : '#E8F1FF'}>{ai ? 'Suggested by the AI' : `Suggested by ${session.userName(p.author)}`}</Pill>
                <span className="small">{c.teamId ? `Team ${session.teamName(c.teamId)} · ` : ''}{ago(p.created_at)}</span>
              </div>
              <div>
                <h2 className="h2 wrap" style={{ lineHeight: 1.3 }}>{c.title}</h2>
                <div className="small" style={{ marginTop: 4, color: 'var(--soft)' }}>
                  {c.target} · filed on <Link to={`/alerts?alert=${encodeURIComponent(p.alert_id)}&view=attention`} style={{ fontWeight: 700, textDecoration: 'none' }}>an alert</Link>
                </div>
              </div>
              {c.now !== null && (
                <div className="grid2">
                  <div className="white"><div className="label">Now</div><div className="wrap" style={{ fontSize: 15, fontWeight: 700, marginTop: 6 }}>{c.now}</div></div>
                  <div className="white" style={{ boxShadow: 'inset 0 0 0 2px #111' }}><div className="label">Proposed</div><div className="wrap" style={{ fontSize: 15, fontWeight: 700, marginTop: 6 }}>{c.next}</div></div>
                </div>
              )}
              {c.steps.length > 0 && (
                <ol className="plain" style={{ gap: 6 }}>
                  {c.steps.map((s, i) => (
                    <li key={i} className="white row" style={{ padding: '10px 12px', borderRadius: 14, gap: 10, flexWrap: 'nowrap' }}>
                      <span className="mark" style={{ width: 24, height: 24, background: '#111', color: '#fff', fontSize: 12 }}>{i + 1}</span>
                      <span style={{ flex: 1, fontSize: 14 }}>{s.text}</span>
                      <Pill fg={RISK[s.risk].fg} bg={RISK[s.risk].bg}>{RISK[s.risk].label}</Pill>
                    </li>
                  ))}
                </ol>
              )}
              {c.reasons.length > 0 && (
                <div className="stack" style={{ gap: 6 }}>
                  <span className="label">Why</span>
                  <ul style={{ margin: 0, paddingLeft: 18, fontSize: 14, lineHeight: 1.6 }}>{c.reasons.map((r, i) => <li key={i} className="wrap">{r}</li>)}</ul>
                </div>
              )}
              {c.confidence !== null && (
                <div className="row" style={{ gap: 10, fontSize: 13, color: 'var(--soft)' }}>
                  <span>AI confidence</span>
                  <span aria-hidden style={{ flex: '0 0 160px', height: 8, borderRadius: 999, background: '#E4E4E7', overflow: 'hidden' }}>
                    <span style={{ display: 'block', height: '100%', width: `${c.confidence * 100}%`, background: '#111', borderRadius: 999 }} />
                  </span>
                  <strong>{Math.round(c.confidence * 100)}%</strong>
                </div>
              )}
              {p.status === 'open' ? (
                <>
                  <div className="small">{c.effect}</div>
                  {canDecide ? (
                    <div className="row">
                      <button type="button" className="btn dark" aria-disabled={c.blocked} onClick={() => (c.blocked ? say('This one can be accepted once runbook runs are built (#73).') : void decide(p, 'accept'))}>Accept</button>
                      <button type="button" className="btn on-grey" onClick={() => void decide(p, 'reject')}>Reject</button>
                    </div>
                  ) : (
                    <div className="small">Only {session.teamName(c.teamId)} can decide on this.</div>
                  )}
                </>
              ) : (
                <div className="white" style={{ fontSize: 14, fontWeight: 600, color: p.status === 'accepted' ? '#15803D' : 'var(--soft)' }}>
                  {cap(p.status)} by {session.userName(p.decided_by)} {ago(p.decided_at)}.
                </div>
              )}
            </article>
          );
        })}
        {!proposals.loading && visible.length === 0 && <div className="empty">Nothing here.</div>}
      </section>
      <aside className="panel" aria-label="About proposals">
        <h2 className="h3" style={{ fontSize: 18 }}>How proposals work</h2>
        {[
          ['From people', 'Anyone on the team can suggest a change from an alert with “Suggest a fix”: when it fires or how it looks.'],
          ['From the AI', 'The AI looks at how people reacted to past alerts and runs, then suggests a severity, a runbook it learned, or how a runbook should start.'],
          ['Only people decide', 'The AI can suggest but never accept. Accepting a severity or a runbook applies it right away; other changes are then made in the rule.'],
        ].map(([t, x]) => (
          <div key={t} className="tile"><div style={{ fontWeight: 800, fontSize: 15 }}>{t}</div><div style={{ fontSize: 14, color: 'var(--soft)', marginTop: 4, lineHeight: 1.5 }}>{x}</div></div>
        ))}
      </aside>
    </Shell>
  );
}
