import { useNavigate, useSearchParams } from 'react-router-dom';
import { Logo } from '../components/Icons';
import { initials } from '../lib/ui';
import { useSession } from '../session';

/** v3 sign-in screen. No login in the PoC: you pick who you are (X-User), as agreed in OVERVIEW. */
export function SignInPage() {
  const { users, teams, loading, error, signIn, reload } = useSession();
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const next = params.get('next') || '/alerts';

  const teamsOf = (userId: string) =>
    teams.filter((t) => t.members.some((m) => m.user_id === userId))
      .map((t) => t.name + (t.members.find((m) => m.user_id === userId)?.on_call ? ' (on call)' : ''));

  return (
    <div className="signin">
      <section className="hero">
        <div className="row" style={{ gap: 10 }}>
          <Logo size={34} light={false} />
          <span style={{ fontSize: 22, fontWeight: 800, letterSpacing: '-0.02em' }}>Relay</span>
        </div>
        <div className="stack" style={{ gap: 20, maxWidth: 560 }}>
          <h1 style={{ margin: 0, fontSize: 56, lineHeight: 1.02, fontWeight: 800, letterSpacing: '-0.035em' }}>
            The right alert.<br />The right person.<br />The right channel.
          </h1>
          <p className="lead" style={{ fontSize: 18, color: 'var(--soft)' }}>One place for every failure, threshold and business event, with the steps to fix it.</p>
        </div>
        <div aria-hidden className="stack" style={{ maxWidth: 460 }}>
          <div className="white row" style={{ borderRadius: 20, gap: 14, boxShadow: '0 8px 24px rgba(17,17,17,.06)' }}>
            <span className="sev-icon" style={{ width: 40, height: 40, background: '#FEE4E2', color: '#B42318' }}>!</span>
            <div style={{ flex: 1 }}><div style={{ fontWeight: 700 }}>order-settlement failed</div><div className="small">Called the on-call person · just now</div></div>
            <span style={{ fontWeight: 800, fontSize: 18 }}>×512</span>
          </div>
          <div className="white row" style={{ borderRadius: 20, gap: 14, marginLeft: 28, boxShadow: '0 8px 24px rgba(17,17,17,.05)' }}>
            <span className="sev-icon" style={{ width: 40, height: 40, background: '#DCFCE7', color: '#15803D' }}>✓</span>
            <div style={{ flex: 1 }}><div style={{ fontWeight: 700 }}>Runbook done</div><div className="small">Settlement timeout · 2 steps run, 1 skipped</div></div>
          </div>
        </div>
      </section>

      <section className="form">
        <div className="stack" style={{ width: '100%', maxWidth: 420, gap: 18 }}>
          <div className="stack" style={{ gap: 8 }}>
            <h2 style={{ margin: 0, fontSize: 32, fontWeight: 800, letterSpacing: '-0.02em' }}>Sign in</h2>
            <p className="lead">Choose who you are. Everything you do is recorded under this name.</p>
          </div>
          {error && (
            <div className="banner" style={{ background: 'var(--red-soft)', color: '#7F1D1D' }}>
              <strong>Cannot reach the API.</strong> {error}
              <div><button type="button" className="btn sm dark" onClick={() => void reload()}>Try again</button></div>
            </div>
          )}
          {loading && !users.length && <p className="lead">Loading people…</p>}
          <ul className="plain" aria-label="People">
            {users.map((u) => (
              <li key={u.id}>
                <button type="button" className="person" onClick={() => { signIn(u.id); navigate(next, { replace: true }); }}>
                  <span className="avatar" style={{ width: 44, height: 44, fontSize: 15 }}>{initials(u.name)}</span>
                  <span style={{ flex: 1, minWidth: 0 }}>
                    <span style={{ display: 'block', fontWeight: 700, fontSize: 16 }}>{u.name}</span>
                    <span className="small" style={{ display: 'block' }}>{teamsOf(u.id).join(' · ') || 'No team'} · {u.email}</span>
                  </span>
                  <span aria-hidden style={{ fontWeight: 800 }}>→</span>
                </button>
              </li>
            ))}
          </ul>
          <p className="small">Opened from an SMS or email link? You'll go straight to that alert after choosing.</p>
        </div>
      </section>
    </div>
  );
}
