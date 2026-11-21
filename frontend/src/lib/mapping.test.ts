import { describe, expect, it } from 'vitest';
import { checkForm, formFromMapping, mappingFromForm, matchForSample } from './mapping';

const k8s = {
  apiVersion: 'v1', kind: 'Event', involvedObject: { kind: 'Pod', name: 'pricing-engine-3-7c9f4' },
  reason: 'BackOff', message: 'Back-off restarting failed container', type: 'Warning', lastTimestamp: '2026-11-20T10:23:41Z',
};
const good = { type: 'reason', severity: 'type', service: 'involvedObject.name', env: '"prod"', ts: 'lastTimestamp', dedup: 'involvedObject.name + reason' };

describe('checkForm', () => {
  it('accepts a mapping whose every line resolves on the sample', () => {
    const checks = checkForm(good, k8s);
    expect(checks.every((c) => c.ok)).toBe(true);
    expect(checks.find((c) => c.key === 'severity')!.note).toContain('warning');
  });

  it('marks missing paths, off-scale severities and unreadable times', () => {
    const checks = checkForm({ ...good, service: 'involvedObject.nope', severity: 'kind', ts: 'reason' }, k8s);
    expect(checks.find((c) => c.key === 'service')!.ok).toBe(false);
    expect(checks.find((c) => c.key === 'severity')!.note).toContain('not on our severity scale');
    expect(checks.find((c) => c.key === 'ts')!.note).toBe('Not a time we can read');
  });

  it('asks for a severity when the line is empty', () => {
    expect(checkForm({ ...good, severity: '' }, k8s).find((c) => c.key === 'severity')!.ok).toBe(false);
  });
});

describe('mappingFromForm / formFromMapping', () => {
  it('round-trips and fills format, severity_map and dedup_fields', () => {
    const m = mappingFromForm(good, k8s, { id: 'k8s-event', match: matchForSample('alertmanager-http', k8s), priority: 50, origin: 'human' });
    expect(m.fields.env).toEqual({ const: 'prod' });
    expect(m.fields.ts).toEqual({ path: 'lastTimestamp', format: 'iso' });
    expect(m.severity_map).toEqual({ Warning: 'warning' });
    expect(m.dedup_fields).toEqual(['involvedObject.name', 'reason']);
    expect(formFromMapping(m)).toEqual(good);
  });

  it('detects epoch timestamps', () => {
    const m = mappingFromForm({ ...good, ts: 'meta.ts' }, { ...k8s, meta: { ts: 1795169702000 } }, { id: 'x', match: { all: [] }, priority: 1, origin: 'human' });
    expect(m.fields.ts!.format).toBe('epoch_ms');
  });

  it('matches new samples on their source and top-level keys', () => {
    const match = matchForSample('alertmanager-http', k8s) as { all: { field: string }[] };
    expect(match.all[0]).toEqual({ field: 'source', op: 'eq', value: 'alertmanager-http' });
    expect(match.all.map((l) => l.field)).toContain('raw.reason');
  });
});
