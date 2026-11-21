import type { Channel, Preferences, Severity } from '../api/types';
import { CHANNEL_LABEL } from './ui';

// "What happens if…" on My notifications, following contracts/rules.md §5.

export const DEFAULT_DOWNGRADE: Partial<Record<Channel, Channel>> = { call: 'sms', sms: 'email' };

export function defaultPreferences(userId: string): Preferences {
  return {
    user_id: userId,
    timezone: Intl.DateTimeFormat().resolvedOptions().timeZone || 'Europe/Warsaw',
    by_severity: { critical: ['sms', 'ui'], error: ['email', 'ui'], warning: ['email', 'ui'], info: ['ui'] },
    quiet_hours: null,
    wake_me: false,
    max_push_per_hour: 10,
  };
}

function minutes(hhmm: string): number {
  const [h, m] = hhmm.split(':').map(Number);
  return (h ?? 0) * 60 + (m ?? 0);
}

/** Inside quiet hours at this time of day? Handles a window across midnight (20:00 → 08:00). */
export function inQuietHours(p: Preferences, hour: number): boolean {
  if (!p.quiet_hours) return false;
  const now = Math.round(hour * 60);
  const start = minutes(p.quiet_hours.start);
  const end = minutes(p.quiet_hours.end);
  return start <= end ? now >= start && now < end : now >= start || now < end;
}

export interface Reach {
  channels: Channel[];
  text: string;
  note: string;
}

/** Which channels an alert of this severity reaches at this hour. A channel is downgraded, never dropped. */
export function reach(p: Preferences, severity: Severity, hour: number): Reach {
  const quiet = inQuietHours(p, hour);
  const bypass = severity === 'critical' && !!p.wake_me;
  let channels = [...(p.by_severity[severity] ?? [])];
  if (quiet && !bypass) {
    const down = p.quiet_hours?.downgrade ?? DEFAULT_DOWNGRADE;
    channels = channels.map((c) => down[c] ?? c);
  }
  channels = channels.filter((c, i) => channels.indexOf(c) === i);
  const note = quiet ? (bypass ? 'Do not disturb is on, but critical alerts always reach you.' : 'Do not disturb: louder channels move to quieter ones.') : '';
  return { channels, text: channels.length ? channels.map((c) => CHANNEL_LABEL[c] ?? c).join(' + ') : 'Nothing', note };
}
