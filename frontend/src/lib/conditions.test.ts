import { describe as group, expect, it } from 'vitest';
import { describe, evaluate, getPath, MISSING, parseValue } from './conditions';

const envelope = {
  source: 'workflows-kafka', type: 'WORKFLOW_FAILED', severity: 'critical', env: 'prod', service: 'order-settlement',
  raw: { workflowName: 'order-settlement', error: { code: 'TIMEOUT' }, payload: { notional: 25_000_000 } },
};

group('getPath (contracts/rules.md §1)', () => {
  it('reads envelope fields first, then falls back to raw', () => {
    expect(getPath(envelope, 'env')).toBe('prod');
    expect(getPath(envelope, 'raw.error.code')).toBe('TIMEOUT');
    expect(getPath(envelope, 'error.code')).toBe('TIMEOUT');
    expect(getPath(envelope, 'payload.notional')).toBe(25_000_000);
    expect(getPath(envelope, 'nope.here')).toBe(MISSING);
  });
});

group('evaluate', () => {
  it('handles every operator of the condition language', () => {
    expect(evaluate({ field: 'type', op: 'eq', value: 'WORKFLOW_FAILED' }, envelope)).toBe(true);
    expect(evaluate({ field: 'env', op: 'ne', value: 'prod' }, envelope)).toBe(false);
    expect(evaluate({ field: 'payload.notional', op: 'gt', value: 10_000_000 }, envelope)).toBe(true);
    expect(evaluate({ field: 'error.code', op: 'in', value: ['TIMEOUT', 'REJECTED'] }, envelope)).toBe(true);
    expect(evaluate({ field: 'service', op: 'glob', value: 'order-*' }, envelope)).toBe(true);
    expect(evaluate({ field: 'severity', op: 'sev_gte', value: 'error' }, envelope)).toBe(true);
    expect(evaluate({ field: 'raw.workflowName', op: 'exists' }, envelope)).toBe(true);
    expect(evaluate({ field: 'raw.missing', op: 'eq', value: 'x' }, envelope)).toBe(false);
  });

  it('combines all and any groups', () => {
    const c = { all: [{ field: 'env', op: 'eq', value: 'prod' }, { any: [{ field: 'severity', op: 'eq', value: 'info' }, { field: 'error.code', op: 'eq', value: 'TIMEOUT' }] }] };
    expect(evaluate(c, envelope)).toBe(true);
  });
});

group('describe and parseValue', () => {
  it('turns a condition into plain words', () => {
    expect(describe({ all: [{ field: 'type', op: 'eq', value: 'WORKFLOW_FAILED' }, { field: 'raw.workflowName', op: 'eq', value: 'order-settlement' }] }))
      .toBe('event type is WORKFLOW_FAILED and workflow name is order-settlement');
  });

  it('parses typed values into JSON types', () => {
    expect(parseValue('in', 'TIMEOUT, REJECTED, 3')).toEqual(['TIMEOUT', 'REJECTED', 3]);
    expect(parseValue('gt', '10000000')).toBe(10_000_000);
    expect(parseValue('eq', 'prod')).toBe('prod');
    expect(parseValue('eq', '42')).toBe(42);
  });
});
