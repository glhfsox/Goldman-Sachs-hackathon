import type { Condition, Leaf } from '../api/types';

// The shared condition language of contracts/rules.md §1–§2.

const ENVELOPE = ['event_id', 'source', 'type', 'severity', 'env', 'service', 'ts', 'dedup_hints', 'mapped', 'mapping_id', 'received_at', 'raw'];
const SCALE = ['info', 'warning', 'error', 'critical'];

export const MISSING = Symbol('missing');

/** A dot path; a first segment that is not an envelope field is read from `raw` (rules.md §1). */
export function getPath(envelope: Record<string, unknown>, path: string): unknown {
  const parts = path.split('.');
  let cur: unknown = ENVELOPE.includes(parts[0]!) ? envelope : envelope.raw;
  for (const part of parts) {
    if (cur && typeof cur === 'object' && part in (cur as Record<string, unknown>)) cur = (cur as Record<string, unknown>)[part];
    else return MISSING;
  }
  return cur;
}

export function isLeaf(c: Condition): c is Leaf {
  return 'field' in c;
}

function globMatch(value: string, pattern: string): boolean {
  const re = new RegExp('^' + pattern.split('*').map((s) => s.replace(/[.+?^${}()|[\]\\]/g, '\\$&')).join('.*') + '$');
  return re.test(value);
}

export function evaluate(c: Condition, envelope: Record<string, unknown>): boolean {
  if ('all' in c) return c.all.every((x) => evaluate(x, envelope));
  if ('any' in c) return c.any.some((x) => evaluate(x, envelope));
  const v = getPath(envelope, c.field);
  if (c.op === 'exists') return v !== MISSING;
  if (v === MISSING) return false;
  switch (c.op) {
    case 'eq': return v === c.value;
    case 'ne': return v !== c.value;
    case 'gt': return Number(v) > Number(c.value);
    case 'gte': return Number(v) >= Number(c.value);
    case 'lt': return Number(v) < Number(c.value);
    case 'lte': return Number(v) <= Number(c.value);
    case 'in': return Array.isArray(c.value) && c.value.includes(v);
    case 'glob': return typeof v === 'string' && globMatch(v, String(c.value));
    case 'sev_gte': return SCALE.indexOf(String(v)) >= SCALE.indexOf(String(c.value)) && SCALE.includes(String(v));
    default: return false;
  }
}

export const OPS: { op: string; label: string }[] = [
  { op: 'eq', label: 'is' },
  { op: 'ne', label: 'is not' },
  { op: 'in', label: 'is one of' },
  { op: 'glob', label: 'matches' },
  { op: 'gt', label: 'is greater than' },
  { op: 'gte', label: 'is at least' },
  { op: 'lt', label: 'is less than' },
  { op: 'lte', label: 'is at most' },
  { op: 'sev_gte', label: 'is at least (severity)' },
  { op: 'exists', label: 'is present' },
];

export const FIELD_LABELS: Record<string, string> = {
  type: 'event type', severity: 'severity', env: 'environment', service: 'service', source: 'source',
  'raw.workflowName': 'workflow name', 'raw.error.code': 'error code', 'raw.step': 'step',
};

export function fieldLabel(path: string): string {
  return FIELD_LABELS[path] ?? path.replace(/^raw\./, '');
}

function valueText(v: unknown): string {
  if (Array.isArray(v)) return v.map(String).join(', ');
  return String(v);
}

/** Plain words for the rule cards and the editor's summary. */
export function describe(c: Condition): string {
  if ('all' in c) return c.all.map((x) => (isLeaf(x) ? describe(x) : `(${describe(x)})`)).join(' and ');
  if ('any' in c) return c.any.map((x) => (isLeaf(x) ? describe(x) : `(${describe(x)})`)).join(' or ');
  const op = OPS.find((o) => o.op === c.op)?.label ?? c.op;
  return c.op === 'exists' ? `${fieldLabel(c.field)} ${op}` : `${fieldLabel(c.field)} ${op.replace(' (severity)', '')} ${valueText(c.value)}`;
}

/** Text in a value box → the JSON value the API expects for that operator. */
export function parseValue(op: string, text: string): unknown {
  const t = text.trim();
  if (op === 'in') return t.split(',').map((s) => s.trim()).filter(Boolean).map((s) => (s !== '' && !Number.isNaN(Number(s)) ? Number(s) : s));
  if (['gt', 'gte', 'lt', 'lte'].includes(op)) return Number(t);
  if (t === 'true' || t === 'false') return t === 'true';
  return t !== '' && /^-?\d+(\.\d+)?$/.test(t) ? Number(t) : t;
}
