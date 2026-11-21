import { createContext, useCallback, useContext, useRef, useState, type ReactNode } from 'react';

interface ToastState {
  text: string;
  undo: (() => void) | null;
}

const Ctx = createContext<(text: string, undo?: () => void) => void>(() => {});

/** A short confirmation at the bottom right; with an Undo button for 6 s when `undo` is given. */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [toast, setToast] = useState<ToastState | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout>>();
  const say = useCallback((text: string, undo?: () => void) => {
    clearTimeout(timer.current);
    setToast({ text, undo: undo ?? null });
    timer.current = setTimeout(() => setToast(null), undo ? 6000 : 3000);
  }, []);
  return (
    <Ctx.Provider value={say}>
      {children}
      {toast && (
        <div role="status" className="toast">
          <span style={{ flex: 1 }}>{toast.text}</span>
          {toast.undo && (
            <button type="button" onClick={() => { toast.undo?.(); setToast(null); }}>Undo</button>
          )}
        </div>
      )}
    </Ctx.Provider>
  );
}

export const useToast = () => useContext(Ctx);
