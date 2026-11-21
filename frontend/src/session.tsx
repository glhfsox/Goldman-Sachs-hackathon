import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { api, errorText, setApiUser } from './api/client';
import type { Team, User } from './api/types';

// Who the UI acts as. No login in the PoC (contracts/api.md): the person is picked on the sign-in
// screen, kept in this browser, and sent as X-User on every request.

const KEY = 'alpin.user';

interface Session {
  user: User | null;
  users: User[];
  teams: Team[];
  loading: boolean;
  error: string | null;
  myTeamIds: string[];
  onCallTeamIds: string[];
  isMine: (teamId: string | null | undefined) => boolean;
  userName: (id: string | null | undefined) => string;
  teamName: (id: string | null | undefined) => string;
  signIn: (id: string) => void;
  signOut: () => void;
  reload: () => Promise<void>;
}

const Ctx = createContext<Session | null>(null);

function readStored(): string | null {
  try {
    return localStorage.getItem(KEY);
  } catch {
    return null;
  }
}

export function SessionProvider({ children }: { children: ReactNode }) {
  const [userId, setUserId] = useState<string | null>(readStored);
  const [users, setUsers] = useState<User[]>([]);
  const [teams, setTeams] = useState<Team[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const reload = useCallback(async () => {
    setLoading(true);
    try {
      const [u, t] = await Promise.all([api.users(), api.teams()]);
      setUsers(u);
      setTeams(t);
      setError(null);
    } catch (e) {
      setError(errorText(e));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void reload();
  }, [reload]);

  const user = users.find((u) => u.id === userId) ?? null;
  setApiUser(user ? user.id : null);

  const value = useMemo<Session>(() => {
    const mine = teams.filter((t) => t.members.some((m) => m.user_id === user?.id));
    return {
      user, users, teams, loading, error,
      myTeamIds: mine.map((t) => t.id),
      onCallTeamIds: mine.filter((t) => t.members.some((m) => m.user_id === user?.id && m.on_call)).map((t) => t.id),
      isMine: (teamId) => !!teamId && mine.some((t) => t.id === teamId),
      userName: (id) => (id ? users.find((u) => u.id === id)?.name ?? id : '—'),
      teamName: (id) => (id ? teams.find((t) => t.id === id)?.name ?? id : '—'),
      signIn: (id) => {
        try { localStorage.setItem(KEY, id); } catch { /* private window: stays for this tab only */ }
        setUserId(id);
      },
      signOut: () => {
        try { localStorage.removeItem(KEY); } catch { /* ignore */ }
        setUserId(null);
      },
      reload,
    };
  }, [user, users, teams, loading, error, reload]);

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useSession(): Session {
  const s = useContext(Ctx);
  if (!s) throw new Error('useSession outside SessionProvider');
  return s;
}
