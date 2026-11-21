import type { Mapping, MappingField, Severity } from '../api/types';

// The mapping form of the Sources screen: one line per envelope field (contracts/rules.md §3).
// A line is a path into the sample, several paths joined with "+", or a fixed value in quotes.

export const TARGETS: { key: string; label: string }[] = [
  { key: 'type', label: 'Type' },
  { key: 'severity', label: 'Severity' },
  { key: 'service', label: 'Service' },
  { key: 'env', label: 'Environment' },
  { key: 'ts', label: 'Time' },
  { key: 'dedup', label: 'Same problem' },
];

const SCALE: Severity[] = ['info', 'warning', 'error', 'critical'];
const SEVERITY_WORDS: Record<string, Severity> = {
  info: 'info', normal: 'info', notice: 'info', debug: 'info',
  warning: 'warning', warn: 'warning',
  error: 'error', err: 'error',
  critical: 'critical', fatal: 'critical', p1: 'critical', crit: 'critical',
};

export type Form = Record<string, string>;

function lookup(sample: unknown, path: string): unknown {
  let cur: unknown = sample;
  for (const part of path.split('.')) {
    if (cur && typeof cur === 'object' && part in (cur as Record<string, unknown>)) cur = (cur as Record<string, unknown>)[part];
    else return undefined;
  }
  return cur;
}

function resolvePart(sample: unknown, part: string): unknown {
  return /^".*"$/.test(part) ? part.slice(1, -1) : lookup(sample, part);
}

export function severityOf(value: unknown, map?: Record<string, string> | null): Severity | null {
  const s = String(value).toLowerCase();
  const mapped = map ? Object.entries(map).find(([k]) => k.toLowerCase() === s)?.[1] : undefined;
  if (mapped && SCALE.includes(mapped as Severity)) return mapped as Severity;
  return SEVERITY_WORDS[s] ?? null;
}

export interface LineCheck {
  key: string;
  label: string;
  value: string;
  ok: boolean;
  note: string;
  values: unknown[];
}

/** Checks every line against the sample, like the API does before a mapping goes live. */
export function checkForm(form: Form, sample: unknown): LineCheck[] {
  return TARGETS.map(({ key, label }) => {
    const value = form[key] ?? '';
    const parts = value.split('+').map((p) => p.trim()).filter(Boolean);
    const values = parts.map((p) => resolvePart(sample, p));
    let problem = '';
    if (!parts.length) problem = key === 'severity' ? 'Nothing in the sample looks like a severity. Type a fixed one, like "error".' : 'Empty';
    else if (values.some((v) => v === undefined || (v !== null && typeof v === 'object'))) problem = 'Not found in the sample';
    else if (key === 'severity' && !severityOf(values[0])) problem = `“${String(values[0])}” is not on our severity scale`;
    else if (key === 'ts' && !tsFormat(values[0])) problem = 'Not a time we can read';
    const shown = values.map(String).join(' + ');
    const note = problem || (key === 'severity' ? `sample: ${shown} → ${severityOf(values[0])}` : `sample: ${shown.length > 44 ? shown.slice(0, 43) + '…' : shown}`);
    return { key, label, value, ok: !problem, note, values };
  });
}

function tsFormat(v: unknown): 'iso' | 'epoch_ms' | 'epoch_s' | null {
  if (typeof v === 'number') return v > 1e12 ? 'epoch_ms' : 'epoch_s';
  if (typeof v === 'string' && !Number.isNaN(Date.parse(v))) return 'iso';
  return null;
}

function fieldText(f: MappingField | undefined): string {
  if (!f) return '';
  if (f.path !== undefined) return f.path;
  return f.const !== undefined ? `"${String(f.const)}"` : '';
}

export function formFromMapping(m: Mapping): Form {
  return {
    type: fieldText(m.fields.type),
    severity: fieldText(m.fields.severity),
    service: fieldText(m.fields.service),
    env: fieldText(m.fields.env),
    ts: fieldText(m.fields.ts),
    dedup: (m.dedup_fields ?? []).join(' + '),
  };
}

function toField(text: string): MappingField {
  const t = text.trim();
  return /^".*"$/.test(t) ? { const: t.slice(1, -1) } : { path: t };
}

/** The form → a Mapping body for PUT /mappings/{id}. `base` keeps id, match, priority and origin. */
export function mappingFromForm(form: Form, sample: unknown, base: Pick<Mapping, 'id' | 'match' | 'priority' | 'origin'>): Mapping {
  const sev = (form.severity ?? '').trim();
  const sevValue = resolvePart(sample, sev);
  const severityMap: Record<string, string> = {};
  const word = severityOf(sevValue);
  if (word && String(sevValue) !== word) severityMap[String(sevValue)] = word;
  const ts = toField(form.ts ?? '');
  return {
    id: base.id,
    status: 'active',
    origin: base.origin,
    priority: base.priority,
    match: base.match,
    fields: {
      type: toField(form.type ?? ''),
      severity: toField(sev),
      service: toField(form.service ?? ''),
      env: toField(form.env ?? ''),
      ts: { ...ts, format: tsFormat(resolvePart(sample, (form.ts ?? '').trim())) ?? 'iso' },
    },
    severity_map: Object.keys(severityMap).length ? severityMap : null,
    dedup_fields: (form.dedup ?? '').split('+').map((p) => p.trim()).filter(Boolean),
  };
}

/** A match for a new mapping: the source plus the sample's top-level keys (like the schema fingerprint). */
export function matchForSample(sourceId: string, sample: unknown): Mapping['match'] {
  const keys = sample && typeof sample === 'object' && !Array.isArray(sample) ? Object.keys(sample as object) : [];
  return { all: [{ field: 'source', op: 'eq', value: sourceId }, ...keys.slice(0, 6).map((k) => ({ field: `raw.${k}`, op: 'exists' }))] };
}
