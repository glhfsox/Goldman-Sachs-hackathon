const base = { width: 22, height: 22, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', strokeWidth: 2, strokeLinecap: 'round' as const, strokeLinejoin: 'round' as const, 'aria-hidden': true };

export const Bell = () => (<svg {...base}><path d="M6 8a6 6 0 1 1 12 0c0 7 3 8 3 8H3s3-1 3-8" /><path d="M10 20a2 2 0 0 0 4 0" /></svg>);
export const Sliders = () => (<svg {...base}><path d="M4 6h10M18 6h2M4 12h4M12 12h8M4 18h12" /><circle cx="16" cy="6" r="2" /><circle cx="10" cy="12" r="2" /><circle cx="18" cy="18" r="2" /></svg>);
export const Book = () => (<svg {...base}><path d="M4 5a2 2 0 0 1 2-2h12v16H6a2 2 0 0 0-2 2z" /><path d="M4 21V5M9 8h5M9 12h5" /></svg>);
export const Bulb = () => (<svg {...base}><path d="M9 18h6M10 21h4" /><path d="M12 3a6 6 0 0 0-3.5 10.9c.6.5 1 1.2 1 2V16h5v-.1c0-.8.4-1.5 1-2A6 6 0 0 0 12 3z" /></svg>);
export const Plug = () => (<svg {...base}><path d="M9 3v5M15 3v5M7 8h10v4a5 5 0 0 1-10 0zM12 17v4" /></svg>);
export const Search = () => (<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} aria-hidden><circle cx="11" cy="11" r="7" /><path d="m20 20-3.5-3.5" /></svg>);
export const Plus = () => (<svg width="16" height="16" viewBox="0 0 16 16" aria-hidden style={{ display: 'block' }}><path d="M8 3v10M3 8h10" stroke="currentColor" strokeWidth={2.2} strokeLinecap="round" /></svg>);

export const Logo = ({ size = 36, light = true }: { size?: number; light?: boolean }) => (
  <svg width={size} height={size} viewBox="0 0 32 32" aria-hidden style={{ display: 'block' }}>
    <rect width="32" height="32" rx="10" fill={light ? '#fff' : '#111'} />
    <circle cx="12" cy="20" r="3" fill={light ? '#111' : '#fff'} />
    <path d="M12 12a8 8 0 0 1 8 8" stroke={light ? '#111' : '#fff'} strokeWidth="2.5" fill="none" strokeLinecap="round" />
    <path d="M12 6a14 14 0 0 1 14 14" stroke={light ? '#111' : '#fff'} strokeWidth="2.5" fill="none" strokeLinecap="round" opacity="0.55" />
  </svg>
);
