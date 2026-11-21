import { useEffect, useState, type ReactNode } from 'react';
import { NavLink } from 'react-router-dom';
import { api } from '../api/client';
import { initials } from '../lib/ui';
import { useSession } from '../session';
import { Bell, Book, Bulb, Logo, Plug, Sliders } from './Icons';

/** The v3 layout: dark icon rail, a sidebar, and the white main area (content + optional right panel). */
export function Shell({ side, children }: { side: ReactNode; children: ReactNode }) {
  const { user } = useSession();
  const [openProposals, setOpenProposals] = useState(0);

  useEffect(() => {
    let alive = true;
    const load = () => api.proposals('open').then((p) => alive && setOpenProposals(p.length)).catch(() => {});
    load();
    const t = setInterval(load, 30_000);
    return () => {
      alive = false;
      clearInterval(t);
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
        <NavLink to="/preferences" className="logo" aria-label="Relay: go to my notifications" title="My notifications"><Logo /></NavLink>
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
