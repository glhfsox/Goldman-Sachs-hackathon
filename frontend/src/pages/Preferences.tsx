import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { api, ApiError, errorText } from '../api/client';
import { CHANNELS, SEVERITIES, type Channel, type Preferences } from '../api/types';
import { Shell } from '../components/Shell';
import { Pill, Switch } from '../components/Switch';
import { useToast } from '../components/Toast';
import { DEFAULT_DOWNGRADE, defaultPreferences, reach } from '../lib/prefs';
import { CHANNEL_LABEL, SEVERITY } from '../lib/ui';
import { useSession } from '../session';

const TIMES = Array.from({ length: 24 }, (_, h) => `${String(h).padStart(2, '0')}:00`);
const ZONES = ['Europe/Warsaw', 'Europe/London', 'Europe/Kyiv', 'Europe/Berlin', 'America/New_York', 'Asia/Singapore', 'UTC'];

export function PreferencesPage() {
  const session = useSession();
  const say = useToast();
  const navigate = useNavigate();
  const user = session.user!;
  const [prefs, setPrefs] = useState<Preferences | null>(null);
  const [saving, setSaving] = useState(false);
  const [problems, setProblems] = useState<{ path: string; message: string }[]>([]);

  useEffect(() => {
    api.preferences(user.id).then((p) => setPrefs(p ?? defaultPreferences(user.id))).catch((e) => say(errorText(e)));
  }, [user.id, say]);

  if (!prefs) return <Shell side={<h2>{user.name}</h2>}><section className="content"><p className="lead">Loading your settings…</p></section></Shell>;

  const set = (patch: Partial<Preferences>) => setPrefs({ ...prefs, ...patch });
  const toggleCell = (sev: (typeof SEVERITIES)[number], c: Channel) => {
    const cur = prefs.by_severity[sev] ?? [];
    set({ by_severity: { ...prefs.by_severity, [sev]: cur.includes(c) ? cur.filter((x) => x !== c) : [...cur, c] } });
  };
  const quiet = prefs.quiet_hours ?? null;
  const setQuiet = (q: Partial<NonNullable<Preferences['quiet_hours']>>) => set({ quiet_hours: { start: '20:00', end: '08:00', downgrade: DEFAULT_DOWNGRADE, ...(quiet ?? {}), ...q } });
  const downgrade = quiet?.downgrade ?? DEFAULT_DOWNGRADE;

  const save = async () => {
    setSaving(true);
    setProblems([]);
    try {
      // The API wants quiet_hours left out (not null) when do-not-disturb is off.
      const { quiet_hours, ...rest } = prefs;
      setPrefs(await api.savePreferences(quiet_hours ? prefs : rest));
      say('Saved. Your next alert uses these settings.');
    } catch (e) {
      if (e instanceof ApiError) setProblems(e.details);
      say(errorText(e));
    } finally {
      setSaving(false);
    }
  };

  const examples = [
    ['A critical alert at 14:00', reach(prefs, 'critical', 14)],
    ['A critical alert at 22:30', reach(prefs, 'critical', 22.5)],
    ['A warning at 21:00', reach(prefs, 'warning', 21)],
    ['An info alert, any time', reach(prefs, 'info', 12)],
  ] as const;
  const myTeams = session.teams.filter((t) => session.isMine(t.id));

  return (
    <Shell side={
      <>
        <div><h2>{user.name}</h2><div className="sub">{user.email}</div></div>
        <div>
          <h3>Settings</h3>
          <a className="item on" href="#channels">My notifications</a>
          <a className="item" href="#dnd">Do not disturb</a>
          <a className="item" href="#teams">My teams</a>
          <a className="item" href="#contact">Phone and email</a>
        </div>
        <button type="button" className="item" style={{ marginTop: 'auto' }} onClick={() => { session.signOut(); navigate('/sign-in'); }}>Sign out</button>
      </>
    }>
      <section className="content" style={{ gap: 16 }}>
        <h1 className="h1">My notifications</h1>
        <p className="lead">How alerts reach you when a rule leaves it to you. Rules that force a channel (like phone calls for on-call) still apply.</p>
        {problems.length > 0 && (
          <div className="banner" style={{ background: 'var(--red-soft)', color: '#7F1D1D' }}>
            <strong>The API did not accept these settings:</strong>
            <ul style={{ margin: 0, paddingLeft: 18 }}>{problems.map((p, i) => <li key={i}><code>{p.path}</code> {p.message}</li>)}</ul>
          </div>
        )}

        <section id="channels" className="card" style={{ gap: 12 }}>
          <h2 className="h2">How should each kind of alert reach you?</h2>
          <div style={{ overflowX: 'auto' }}>
            <table style={{ borderCollapse: 'collapse', width: '100%', minWidth: 520 }}>
              <thead>
                <tr>
                  <th scope="col" style={{ textAlign: 'left', fontSize: 13, color: 'var(--muted)', padding: 8 }}>Severity</th>
                  {CHANNELS.map((c) => <th key={c} scope="col" style={{ fontSize: 13, color: 'var(--muted)', padding: 8 }}>{CHANNEL_LABEL[c]}</th>)}
                </tr>
              </thead>
              <tbody>
                {SEVERITIES.map((sev) => (
                  <tr key={sev}>
                    <td style={{ padding: '6px 8px' }}><Pill fg={SEVERITY[sev].fg} bg={SEVERITY[sev].bg} style={{ fontSize: 13, padding: '4px 12px' }}>{SEVERITY[sev].label}</Pill></td>
                    {CHANNELS.map((c) => {
                      const on = (prefs.by_severity[sev] ?? []).includes(c);
                      return (
                        <td key={c} style={{ padding: '6px 8px', textAlign: 'center' }}>
                          <button type="button" aria-pressed={on} aria-label={`${SEVERITY[sev].label} by ${CHANNEL_LABEL[c]}`} onClick={() => toggleCell(sev, c)}
                            style={{ width: 44, height: 44, borderRadius: 14, border: 0, fontWeight: 800, fontSize: 16, color: '#fff', background: on ? '#111' : '#fff', transition: 'background-color .15s' }}>{on ? '✓' : ''}</button>
                        </td>
                      );
                    })}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <p className="small" style={{ fontSize: 14 }}>Leave a row on “In the app” only if you don't want to be disturbed for that severity.</p>
        </section>

        <section id="dnd" className="card">
          <div className="row" style={{ gap: 12, flexWrap: 'nowrap' }}>
            <h2 className="h2" style={{ flex: 1 }}>Do not disturb</h2>
            <Switch on={!!quiet} label="Do not disturb" onChange={(v) => (v ? setQuiet({}) : set({ quiet_hours: null }))} />
          </div>
          {quiet && (
            <>
              <div className="row" style={{ fontSize: 15 }}>
                <label htmlFor="from">From</label>
                <select id="from" className="field" value={quiet.start} onChange={(e) => setQuiet({ start: e.target.value })}>{TIMES.map((t) => <option key={t}>{t}</option>)}</select>
                <label htmlFor="to">to</label>
                <select id="to" className="field" value={quiet.end} onChange={(e) => setQuiet({ end: e.target.value })}>{TIMES.map((t) => <option key={t}>{t}</option>)}</select>
                <label htmlFor="tz">in</label>
                <select id="tz" className="field" value={prefs.timezone ?? 'UTC'} onChange={(e) => set({ timezone: e.target.value })}>{[...new Set([prefs.timezone ?? 'UTC', ...ZONES])].map((z) => <option key={z}>{z}</option>)}</select>
              </div>
              <div className="white stack">
                <div style={{ fontWeight: 700, fontSize: 15 }}>During these hours, use instead</div>
                {(['call', 'sms'] as const).map((from) => (
                  <div key={from} className="row" style={{ fontSize: 15 }}>
                    {CHANNEL_LABEL[from]} →
                    <select className="field grey" aria-label={`Instead of ${CHANNEL_LABEL[from]}`} value={downgrade[from] ?? from}
                      onChange={(e) => setQuiet({ downgrade: { ...downgrade, [from]: e.target.value as Channel } })}>
                      {CHANNELS.filter((c) => c !== from).map((c) => <option key={c} value={c}>{CHANNEL_LABEL[c]}</option>)}
                    </select>
                  </div>
                ))}
                <div className="small">Alerts are never dropped, only moved to a quieter channel.</div>
              </div>
            </>
          )}
          <div className="row" style={{ gap: 12, flexWrap: 'nowrap' }}>
            <div style={{ flex: 1 }}><div style={{ fontWeight: 700, fontSize: 15 }}>Critical alerts always reach me</div><div className="small">Ignore do-not-disturb for critical alerts.</div></div>
            <Switch on={!!prefs.wake_me} label="Critical alerts always reach me" onChange={(v) => set({ wake_me: v })} />
          </div>
          <div className="row" style={{ fontSize: 15 }}>
            <label htmlFor="lim">At most</label>
            <select id="lim" className="field" value={prefs.max_push_per_hour ?? 10} onChange={(e) => set({ max_push_per_hour: Number(e.target.value) })}>
              {[...new Set([5, 10, 20, 50, prefs.max_push_per_hour ?? 10])].sort((a, b) => a - b).map((n) => <option key={n} value={n}>{n}</option>)}
            </select>
            SMS and calls per hour. The rest arrive as one “and N more” message.
          </div>
        </section>

        <section id="teams" className="card" style={{ gap: 10 }}>
          <h2 className="h2">My teams</h2>
          {myTeams.length === 0 && <div className="small">You are not in a team yet.</div>}
          {myTeams.map((t) => {
            const me = t.members.find((m) => m.user_id === user.id);
            const onCall = t.members.find((m) => m.on_call);
            return (
              <div key={t.id} className="white row" style={{ gap: 12 }}>
                <strong style={{ flex: 1 }}># {t.name}</strong>
                {me?.on_call ? <Pill fg="#15803D" bg="#DCFCE7">You are on call</Pill> : <span className="small">On call: {onCall?.name ?? 'nobody'}</span>}
              </div>
            );
          })}
        </section>

        <section id="contact" className="card" style={{ gap: 12 }}>
          <h2 className="h2">Phone and email</h2>
          <div className="grid2">
            <div className="white"><div className="small">Calls and SMS go to</div><div style={{ fontWeight: 700, fontSize: 16, marginTop: 4 }}>{user.phone}</div></div>
            <div className="white"><div className="small">Emails go to</div><div style={{ fontWeight: 700, fontSize: 16, marginTop: 4 }}>{user.email}</div></div>
          </div>
          <div className="small">From your company profile.</div>
        </section>
      </section>

      <aside className="panel" aria-label="What happens if">
        <h2 className="h3" style={{ fontSize: 18 }}>What happens if…</h2>
        <p className="small" style={{ margin: 0 }}>Updates as you change settings.</p>
        {examples.map(([q, r]) => (
          <div key={q} className="tile stack" style={{ gap: 6, borderRadius: 18 }}>
            <div className="small" style={{ fontSize: 14 }}>{q}</div>
            <div style={{ fontWeight: 800, fontSize: 17 }}>→ {r.text}</div>
            {r.note && <div className="small">{r.note}</div>}
          </div>
        ))}
        <button type="button" className="btn dark" style={{ height: 52, marginTop: 8 }} disabled={saving} onClick={() => void save()}>{saving ? 'Saving…' : 'Save'}</button>
      </aside>
    </Shell>
  );
}
