export function Switch({ on, onChange, label, disabled }: { on: boolean; onChange: (v: boolean) => void; label: string; disabled?: boolean }) {
  return (
    <button type="button" role="switch" aria-checked={on} aria-label={label} disabled={disabled}
      className={'switch' + (on ? ' on' : '')} onClick={() => onChange(!on)}>
      <span className="knob" />
    </button>
  );
}

export function Pill({ fg, bg, children, style }: { fg: string; bg: string; children: React.ReactNode; style?: React.CSSProperties }) {
  return <span className="pill" style={{ color: fg, background: bg, ...style }}>{children}</span>;
}
