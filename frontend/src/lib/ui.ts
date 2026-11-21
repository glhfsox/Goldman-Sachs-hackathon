import type { AlertStatus, Risk, Severity } from '../api/types';

/** Severity colours of the v3 design: [label, text colour, background, icon glyph]. */
export const SEVERITY: Record<Severity, { label: string; fg: string; bg: string; glyph: string }> = {
  critical: { label: 'Critical', fg: '#B42318', bg: '#FEE4E2', glyph: '!' },
  error: { label: 'Error', fg: '#B54708', bg: '#FFEDD5', glyph: '!' },
  warning: { label: 'Warning', fg: '#854D0E', bg: '#FEF9C3', glyph: '~' },
  info: { label: 'Info', fg: '#1A56DB', bg: '#E8F1FF', glyph: 'i' },
};

export const STATUS: Record<AlertStatus, { label: string; fg: string; bg: string }> = {
  open: { label: 'Open', fg: '#B42318', bg: '#FEF2F2' },
  acked: { label: 'Acknowledged', fg: '#92400E', bg: '#FFFBEB' },
  resolved: { label: 'Resolved', fg: '#15803D', bg: '#F0FDF4' },
};

export const RISK: Record<Risk | 'manual', { label: string; fg: string; bg: string; meaning: string }> = {
  read_only: { label: 'Read-only', fg: '#1A56DB', bg: '#E8F1FF', meaning: 'Runs at once. It only reads.' },
  auto: { label: 'Automatic', fg: '#15803D', bg: '#DCFCE7', meaning: 'Starts by itself after a 30 s cancel window, if the risk check clears it.' },
  approval: { label: 'Needs a person', fg: '#92400E', bg: '#FEF3C7', meaning: 'A person reviews the run and starts it.' },
  manual: { label: 'Manual', fg: '#3F3F46', bg: '#E4E4E7', meaning: 'A person does it and marks it done.' },
};

export const RISK_RANK: Record<Risk, number> = { read_only: 0, auto: 1, approval: 2 };

/** Rule `present.color` → [accent, soft background]. */
export const COLORS: Record<string, [string, string]> = {
  red: ['#DC2626', '#FEE4E2'],
  orange: ['#EA580C', '#FFEDD5'],
  yellow: ['#CA8A04', '#FEF9C3'],
  blue: ['#1A56DB', '#E8F1FF'],
  grey: ['#71717A', '#F4F4F5'],
};

export const CHANNEL_LABEL: Record<string, string> = { call: 'Phone call', sms: 'SMS', email: 'Email', ui: 'In the app' };

export function initials(name: string): string {
  return name.split(/\s+/).filter(Boolean).slice(0, 2).map((p) => p[0]!.toUpperCase()).join('');
}

export function clock(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? '—' : d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

export function ago(iso: string | null | undefined, now: number = Date.now()): string {
  if (!iso) return '—';
  const s = Math.max(0, Math.round((now - new Date(iso).getTime()) / 1000));
  if (s < 10) return 'just now';
  if (s < 60) return `${s} s ago`;
  if (s < 3600) return `${Math.round(s / 60)} min ago`;
  if (s < 86400) return `${Math.round(s / 3600)} h ago`;
  return `${Math.round(s / 86400)} d ago`;
}

export function cap(s: string): string {
  return s ? s[0]!.toUpperCase() + s.slice(1) : s;
}

/** "a, b and c". */
export function listText(items: string[]): string {
  if (items.length <= 1) return items.join('');
  return items.slice(0, -1).join(', ') + ' and ' + items[items.length - 1];
}
