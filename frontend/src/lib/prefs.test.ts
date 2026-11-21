import { describe, expect, it } from 'vitest';
import type { Preferences } from '../api/types';
import { inQuietHours, reach } from './prefs';

// Anna's seeded preferences (contracts/rules.md §5).
const anna: Preferences = {
  user_id: 'anna', timezone: 'Europe/Warsaw',
  by_severity: { critical: ['sms'], error: ['email'], warning: ['email'], info: ['ui'] },
  quiet_hours: { start: '20:00', end: '08:00', downgrade: { call: 'sms', sms: 'email' } },
  wake_me: false, max_push_per_hour: 10,
};

describe('quiet hours', () => {
  it('handles a window across midnight', () => {
    expect(inQuietHours(anna, 22.5)).toBe(true);
    expect(inQuietHours(anna, 7)).toBe(true);
    expect(inQuietHours(anna, 9)).toBe(false);
    expect(inQuietHours({ ...anna, quiet_hours: null }, 22)).toBe(false);
  });
});

describe('reach (AC-5.5: Anna, critical, 22:00 → email; 09:00 → SMS)', () => {
  it('downgrades inside quiet hours and keeps channels outside', () => {
    expect(reach(anna, 'critical', 22).channels).toEqual(['email']);
    expect(reach(anna, 'critical', 9).channels).toEqual(['sms']);
  });

  it('lets critical through when wake_me is on', () => {
    expect(reach({ ...anna, wake_me: true }, 'critical', 22).channels).toEqual(['sms']);
    expect(reach({ ...anna, wake_me: true }, 'error', 22).channels).toEqual(['email']);
  });

  it('never repeats a channel after downgrading', () => {
    const p: Preferences = { ...anna, by_severity: { critical: ['call', 'sms'] } };
    expect(reach(p, 'critical', 23).channels).toEqual(['sms', 'email']);
  });
});
