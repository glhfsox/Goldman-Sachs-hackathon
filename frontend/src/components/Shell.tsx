import { useEffect, useState, type ReactNode } from 'react';
import { NavLink } from 'react-router-dom';
import { api } from '../api/client';
import { onProposalsChanged } from '../lib/proposalsSignal';
import { initials } from '../lib/ui';
import { useSession } from '../session';
import { Bell, Book, Bulb, Logo, Plug, Sliders } from './Icons';

/** The v3 layout: dark icon rail, a sidebar, and the white main area (content + optional right panel). */
export function Shell({ side, children }: { side: ReactNode; children: ReactNode }) {
  const { user } = useSession();
  const [openProposals, setOpenProposals] = useState(0);

  useEffect(() => {
    let alive = true;
    let last = 0;
    let pending: ReturnType<typeof setTimeout> | undefined;
    const load = () => {
      last = Date.now();
      api.proposals('open').then((p) => alive && setOpenProposals(p.length)).catch(() => {});
    };
    // At most one fetch every 2 s, however many changes arrive; the last change is always fetched.
    const soon = () => {
      if (pending) return;
      pending = setTimeout(() => { pending = undefined; load(); }, Math.max(0, 2_000 - (Date.now() - last)));
    };
    const onVisible = () => { if (document.visibilityState === 'visible') soon(); };
    load();
    const t = setInterval(load, 30_000);
    const off = onProposalsChanged(soon);
    document.addEventListener('visibilitychange', onVisible);
    return () => {
      alive = false;
      clearInterval(t);
      clearTimeout(pending);
      off();
      document.removeEventListener('visibilitychange', onVisible);
    };
  }, []);

  const link = (to: string, label: string, icon: ReactNode, badge?: number) => (
    <NavLink to={to} className={({ isActive }) => (isActive ? 'active' : '')}>
      {icon}
      {label}
      {!!badge && <span className="dot" aria-label={`${badge} open`}>{badge}</span>}
    </NavLink>
  );

  return (
    <div className="app">
      <nav className="rail" aria-label="Main">
        <NavLink to="/preferences" className="logo" aria-label="Alpin: go to my notifications" title="My notifications"><Logo /></NavLink>
        {link('/alerts', 'Alerts', <Bell />)}
        {link('/rules', 'Rules', <Sliders />)}
        {link('/runbooks', 'Runbooks', <Book />)}
        {link('/proposals', 'Proposals', <Bulb />, openProposals)}
        {link('/sources', 'Sources', <Plug />)}
        <div style={{ flex: 1 }} />
        <NavLink to="/preferences" aria-label="My notifications" title={user?.name}><span className="avatar">{user ? initials(user.name) : '?'}</span></NavLink>
      </nav>
      <aside className="side">{side}</aside>
      <main className="main">{children}</main>
    </div>
  );
}
