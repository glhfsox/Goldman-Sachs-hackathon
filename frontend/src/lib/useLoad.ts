import { useCallback, useEffect, useRef, useState } from 'react';
import { errorText } from '../api/client';

export interface Loaded<T> {
  data: T | null;
  error: string | null;
  loading: boolean;
  reload: () => Promise<void>;
  setData: (update: T | ((prev: T | null) => T)) => void;
}

/** Loads data once (and again on `reload` or when `deps` change); keeps the last good data on errors. */
export function useLoad<T>(fn: () => Promise<T>, deps: unknown[] = []): Loaded<T> {
  const [data, setDataState] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const fnRef = useRef(fn);
  fnRef.current = fn;

  const reload = useCallback(async () => {
    setLoading(true);
    try {
      setDataState(await fnRef.current());
      setError(null);
    } catch (e) {
      setError(errorText(e));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void reload();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);

  const setData = useCallback((update: T | ((prev: T | null) => T)) => {
    setDataState((prev) => (typeof update === 'function' ? (update as (p: T | null) => T)(prev) : update));
  }, []);

  return { data, error, loading, reload, setData };
}
