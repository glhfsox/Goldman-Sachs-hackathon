import type { ReactNode } from 'react';
import { Navigate, Route, Routes, useLocation, useParams } from 'react-router-dom';
import { AlertsPage } from './pages/Alerts';
import { PreferencesPage } from './pages/Preferences';
import { ProposalsPage } from './pages/Proposals';
import { RuleEditorPage } from './pages/RuleEditor';
import { RulesPage } from './pages/Rules';
import { RunbooksPage } from './pages/Runbooks';
import { SignInPage } from './pages/SignIn';
import { SourcesPage } from './pages/Sources';
import { useSession } from './session';

/** Pages need a person (X-User); without one, go to sign-in and come back afterwards. */
function NeedsUser({ children }: { children: ReactNode }) {
  const { user, loading } = useSession();
  const location = useLocation();
  if (loading && !user) return <div className="signin" style={{ alignItems: 'center', justifyContent: 'center' }}><p className="lead">Loading…</p></div>;
  if (!user) return <Navigate to={`/sign-in?next=${encodeURIComponent(location.pathname + location.search)}`} replace />;
  return <>{children}</>;
}

/** The short link of SMS and email: {PUBLIC_BASE_URL}/a/{alert_id} (contracts/api.md). */
function ShortLink() {
  const { id } = useParams();
  return <Navigate to={`/alerts?alert=${encodeURIComponent(id ?? '')}`} replace />;
}

export function App() {
  return (
    <Routes>
      <Route path="/sign-in" element={<SignInPage />} />
      <Route path="/a/:id" element={<ShortLink />} />
      <Route path="/alerts" element={<NeedsUser><AlertsPage /></NeedsUser>} />
      <Route path="/proposals" element={<NeedsUser><ProposalsPage /></NeedsUser>} />
      <Route path="/rules" element={<NeedsUser><RulesPage /></NeedsUser>} />
      <Route path="/rules/new" element={<NeedsUser><RuleEditorPage /></NeedsUser>} />
      <Route path="/rules/:id" element={<NeedsUser><RuleEditorPage /></NeedsUser>} />
      <Route path="/runbooks" element={<NeedsUser><RunbooksPage /></NeedsUser>} />
      <Route path="/sources" element={<NeedsUser><SourcesPage /></NeedsUser>} />
      <Route path="/preferences" element={<NeedsUser><PreferencesPage /></NeedsUser>} />
      <Route path="*" element={<Navigate to="/alerts" replace />} />
    </Routes>
  );
}
