import type { Recipient, Rule } from '../api/types';
import { describe, fieldLabel } from './conditions';
import { CHANNEL_LABEL, listText } from './ui';

export function recipientText(r: Recipient, teamName: (id: string) => string, userName: (id: string) => string): string {
  if ('user' in r) return userName(r.user);
  return r.on_call ? `the ${teamName(r.team)} on-call person` : `the whole ${teamName(r.team)} team`;
}

export function channelsText(rule: Pick<Rule, 'route'>): string {
  const ch = rule.route.channels;
  return ch && ch.length ? listText(ch.map((c) => (CHANNEL_LABEL[c] ?? c).toLowerCase())) : 'each person’s own settings';
}

export function whoText(rule: Pick<Rule, 'route'>, teamName: (id: string) => string, userName: (id: string) => string): string {
  return listText(rule.route.recipients.map((r) => recipientText(r, teamName, userName))) || 'nobody yet';
}

/** One sentence for a rule card: "When …, notify … by …". */
export function ruleSentence(rule: Rule, teamName: (id: string) => string, userName: (id: string) => string): { when: string; who: string; channels: string } {
  return { when: describe(rule.when), who: whoText(rule, teamName, userName), channels: channelsText(rule) };
}

export function groupByText(rule: Pick<Rule, 'alert_key' | 'window_s'>): string {
  const keys = rule.alert_key?.length ? listText(rule.alert_key.map(fieldLabel)) : 'type and service';
  return `${keys}, ${windowText(rule.window_s ?? 300)}`;
}

export const WINDOWS: number[] = [300, 900, 1800, 3600, 14400, 43200, 86400, 259200, 604800];

export function windowText(s: number): string {
  if (s % 86400 === 0) return `${s / 86400} day${s === 86400 ? '' : 's'}`;
  if (s % 3600 === 0) return `${s / 3600} hour${s === 3600 ? '' : 's'}`;
  return `${Math.round(s / 60)} minutes`;
}
