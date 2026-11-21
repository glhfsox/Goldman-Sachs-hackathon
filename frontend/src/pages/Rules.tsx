import { Link, useSearchParams } from 'react-router-dom';
import { api, errorText } from '../api/client';
import type { Rule } from '../api/types';
import { Shell } from '../components/Shell';
import { Pill, Switch } from '../components/Switch';
import { useToast } from '../components/Toast';
import { groupByText, ruleSentence } from '../lib/ruleText';
import { useLoad } from '../lib/useLoad';
import { COLORS, initials, SEVERITY } from '../lib/ui';
import { useSession } from '../session';

export const TEMPLATES: { id: string; title: string; text: string }[] = [
  { id: 'workflow', title: 'Failed workflow in prod', text: 'Call and SMS the on-call person' },
  { id: 'logs', title: 'Service error logs', text: 'Show in the app, one alert per 5 minutes' },
  { id: 'amount', title: 'Amount over a limit', text: 'Email the team for large business events' },
];

export function RulesPage() {
  const session = useSession();
  const say = useToast();
  const [params, setParams] = useSearchParams();
  const rules = useLoad(() => api.rules(), []);
  const runbooks = useLoad(() => api.runbooks(), []);
  const myTeams = session.teams.filter((t) => session.isMine(t.id));
  const otherTeams = session.teams.filter((t) => !session.isMine(t.id));
  const teamId = params.get('team') || myTeams[0]?.id || session.teams[0]?.id || '';
  const team = session.teams.find((t) => t.id === teamId);
  const mine = session.isMine(teamId);
  const list = (rules.data ?? []).filter((r) => r.team_id === teamId);
  const runbookTitle = (id?: string | null) => (id ? (runbooks.data ?? []).find((r) => r.id === id)?.title ?? id : 'none');

  const toggle = async (rule: Rule) => {
    const next = { ...rule, enabled: !rule.enabled };
    rules.setData((prev) => (prev ?? []).map((r) => (r.id === rule.id ? next : r)));
    try {
      await api.saveRule(next);
      say(next.enabled ? 'Rule switched on.' : 'Rule switched off. It no longer opens alerts.');
    } catch (e) {
      rules.setData((prev) => (prev ?? []).map((r) => (r.id === rule.id ? rule : r)));
      say(errorText(e));
    }
  };

  const teamButton = (id: string, name: string) => (
    <button key={id} type="button" className={'item' + (id === teamId ? ' on' : '')} onClick={() => setParams({ team: id })}>
      <span aria-hidden>#</span><span className="grow">{name}</span>
      <span style={{ fontSize: 12, opacity: 0.8 }}>{(rules.data ?? []).filter((r) => r.team_id === id).length}</span>
    </button>
  );

  return (
    <Shell side={
      <>
        <h2>Rules</h2>
        {myTeams.length > 0 && <div><h3>Your teams</h3>{myTeams.map((t) => teamButton(t.id, t.name))}</div>}
        {otherTeams.length > 0 && <div><h3>Other teams · read only</h3>{otherTeams.map((t) => teamButton(t.id, t.name))}</div>}
      </>
    }>
      <section className="content">
        <div className="row" style={{ alignItems: 'flex-end', gap: 12 }}>
          <div style={{ flex: 1, minWidth: 240 }}>
            <h1 className="h1"># {team?.name ?? 'Rules'}</h1>
            <p className="lead" style={{ marginTop: 6 }}>Rules decide which events become alerts for this team, who hears about them, and how.</p>
            {!mine && team && <p className="pill" style={{ marginTop: 10, background: 'var(--grey)', color: 'var(--soft)', fontSize: 14, fontWeight: 500, padding: '6px 14px' }}>You are not in {team.name}, so you can read its rules but not change them.</p>}
          </div>
          {mine && <Link to={`/rules/new?team=${encodeURIComponent(teamId)}`} className="btn dark">+ New rule</Link>}
        </div>
        {rules.error && <div className="banner" style={{ background: 'var(--red-soft)', color: '#7F1D1D' }}>{rules.error}</div>}

        {list.length > 0 && (
          <ul className="plain" style={{ gap: 12 }}>
            {list.map((r) => {
              const s = ruleSentence(r, session.teamName, session.userName);
              const dot = r.enabled ? (COLORS[r.present?.color ?? 'grey'] ?? COLORS.grey!)[0] : '#A1A1AA';
              const sev = r.severity ? SEVERITY[r.severity] : null;
              return (
                <li key={r.id} className="card" style={{ flexDirection: 'row', flexWrap: 'wrap', alignItems: 'flex-start', gap: 16, padding: '20px 22px', borderRadius: 20 }}>
                  <div className="stack" style={{ flex: 1, minWidth: 260, gap: 8 }}>
                    <div className="row" style={{ gap: 8 }}>
                      <span className="sdot" style={{ width: 10, height: 10, background: dot }} />
                      <Link to={`/rules/${encodeURIComponent(r.id)}`} style={{ fontWeight: 800, fontSize: 17, color: 'var(--ink)', textDecoration: 'none' }}>{r.name || r.id}</Link>
                    </div>
                    <p className="wrap" style={{ margin: 0, fontSize: 15, color: '#27272A', lineHeight: 1.55 }}>
                      <strong>When</strong> {s.when}, <strong>notify</strong> {s.who} <strong>by</strong> {s.channels}.
                    </p>
                    <div className="row" style={{ gap: 6 }}>
                      <Pill fg={sev ? sev.fg : '#111'} bg={sev ? sev.bg : '#fff'}>{sev ? `Alerts: always ${r.severity}` : 'Severity from the event'}</Pill>
                      <Pill fg="#111" bg="#fff">Repeats grouped by {groupByText(r)}</Pill>
                      <Pill fg="#111" bg="#fff">Runbook: {runbookTitle(r.runbook_id)}</Pill>
                    </div>
                  </div>
                  {mine && <Switch on={r.enabled} label={`Rule ${r.name || r.id} on or off`} onChange={() => void toggle(r)} />}
                </li>
              );
            })}
          </ul>
        )}
        {!rules.loading && list.length === 0 && (
          <section className="card" style={{ padding: 32 }}>
            <h2 style={{ margin: 0, fontSize: 24, fontWeight: 800, letterSpacing: '-0.02em' }}>No rules yet{mine ? '. Start from a template.' : '.'}</h2>
            {mine && (
              <>
                <p className="lead">Change what you need and save. The team gets its first alerts right away.</p>
                <div className="grid3">
                  {TEMPLATES.map((t) => (
                    <Link key={t.id} to={`/rules/new?team=${encodeURIComponent(teamId)}&template=${t.id}`} className="white" style={{ borderRadius: 18, padding: 18, textDecoration: 'none', color: 'var(--ink)' }}>
                      <strong style={{ display: 'block', fontSize: 16, marginBottom: 6 }}>{t.title}</strong>
                      <span className="small">{t.text}</span>
                    </Link>
                  ))}
                </div>
              </>
            )}
          </section>
        )}
      </section>

      <aside className="panel" aria-label="Team members">
        <h2 className="h3" style={{ fontSize: 18 }}>People in {team?.name ?? 'this team'}</h2>
        {team?.members.map((m) => (
          <div key={m.user_id} className="row" style={{ gap: 12, flexWrap: 'nowrap' }}>
            <span className="avatar" style={{ width: 40, height: 40, fontSize: 14 }}>{initials(m.name)}</span>
            <div style={{ flex: 1 }}><div style={{ fontWeight: 700, fontSize: 15 }}>{m.name}{m.user_id === session.user?.id ? ' (you)' : ''}</div></div>
            {m.on_call && <Pill fg="#15803D" bg="#DCFCE7">On call</Pill>}
          </div>
        ))}
        <div className="banner" style={{ background: 'var(--accent-soft)', color: '#1E3A8A', fontSize: 14, lineHeight: 1.5, display: 'block' }}>
          <strong>On call</strong> is the one person who answers this team’s urgent alerts right now. Rules can call or text only this person instead of waking the whole team.
        </div>
        {myTeams.length > 1 && (
          <div className="tile" style={{ fontSize: 14, lineHeight: 1.5 }}>You are in <strong>{myTeams.length} teams</strong>. If rules from several fire for the same incident, you still get <strong>one</strong> message.</div>
        )}
      </aside>
    </Shell>
  );
}
