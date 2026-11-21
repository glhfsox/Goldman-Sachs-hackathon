import type { Condition, Leaf, Rule } from '../api/types';
import { isLeaf, parseValue } from './conditions';

// Helpers for the rule editor: the form edits a Rule as it is in contracts/rules.md §4.

export type Group = { all: Condition[] } | { any: Condition[] };

export function modeOf(c: Condition): 'all' | 'any' {
  return 'any' in c ? 'any' : 'all';
}

export function childrenOf(c: Condition): Condition[] {
  if ('all' in c) return c.all;
  if ('any' in c) return c.any;
  return [c];
}

export function makeGroup(mode: 'all' | 'any', children: Condition[]): Group {
  return mode === 'all' ? { all: children } : { any: children };
}

export function valueText(v: unknown): string {
  if (v === undefined || v === null) return '';
  return Array.isArray(v) ? v.join(', ') : String(v);
}

/** Leaves keep what was typed while editing; this turns every value into its JSON type before saving. */
export function normalize(c: Condition): Condition {
  if (isLeaf(c)) {
    const leaf: Leaf = { field: c.field.trim(), op: c.op };
    if (c.op !== 'exists') leaf.value = typeof c.value === 'string' ? parseValue(c.op, c.value) : c.value;
    return leaf;
  }
  return makeGroup(modeOf(c), childrenOf(c).map(normalize));
}

export function slug(text: string): string {
  return text.toLowerCase().normalize('NFKD').replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 60) || 'rule';
}

export function blankRule(teamId: string): Rule {
  return {
    id: '', team_id: teamId, name: '', enabled: true,
    when: { all: [{ field: 'type', op: 'eq', value: '' }] },
    severity: null, alert_key: ['type', 'service'], window_s: 300,
    route: { recipients: [{ team: teamId, on_call: true }], channels: null },
    present: { title: '', body: '', color: 'red' }, runbook_id: null,
  };
}

/** The empty-state templates of the Rules screen. */
export function templateRule(teamId: string, template: string | null): Rule {
  const r = blankRule(teamId);
  switch (template) {
    case 'workflow':
      return { ...r, name: 'Failed workflow in prod', severity: 'critical',
        when: { all: [{ field: 'type', op: 'eq', value: 'WORKFLOW_FAILED' }, { field: 'env', op: 'eq', value: 'prod' }] },
        alert_key: ['raw.workflowName', 'raw.error.code'], route: { recipients: [{ team: teamId, on_call: true }], channels: ['call', 'sms', 'ui'] },
        present: { title: '{{workflowName}} failed at step {{step}}', body: '{{error.message}}', color: 'red' } };
    case 'logs':
      return { ...r, name: 'Service error logs',
        when: { all: [{ field: 'service', op: 'glob', value: 'my-service*' }, { field: 'severity', op: 'sev_gte', value: 'error' }] },
        alert_key: ['service'], route: { recipients: [{ team: teamId }], channels: ['ui'] }, present: { title: 'Errors in {{service}}', color: 'orange' } };
    case 'amount':
      return { ...r, name: 'Amount over a limit',
        when: { all: [{ field: 'type', op: 'glob', value: 'trade.rejected*' }, { field: 'payload.notional', op: 'gt', value: 10000000 }] },
        alert_key: ['payload.tradeId'], route: { recipients: [{ team: teamId }], channels: ['email', 'ui'] },
        present: { title: 'Trade {{payload.tradeId}}: {{payload.reason}}', color: 'orange' } };
    default:
      return r;
  }
}

/** Fills {{path}} placeholders for the preview. */
export function fillTemplate(template: string, sample: Record<string, string>): string {
  return template.replace(/\{\{\s*([\w.]+)\s*\}\}/g, (_, k: string) => sample[k] ?? `‹${k}›`);
}
