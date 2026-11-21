import { describe, expect, it } from 'vitest';
import { fillTemplate, normalize, slug, templateRule } from './ruleForm';

describe('normalize', () => {
  it('turns what was typed into JSON values before saving', () => {
    const typed = { all: [
      { field: ' payload.notional ', op: 'gt', value: '10000000' },
      { any: [{ field: 'raw.error.code', op: 'in', value: 'TIMEOUT, REJECTED' }, { field: 'raw.x', op: 'exists', value: 'ignored' }] },
    ] };
    expect(normalize(typed)).toEqual({ all: [
      { field: 'payload.notional', op: 'gt', value: 10_000_000 },
      { any: [{ field: 'raw.error.code', op: 'in', value: ['TIMEOUT', 'REJECTED'] }, { field: 'raw.x', op: 'exists' }] },
    ] });
  });
});

describe('helpers', () => {
  it('makes ids the API accepts', () => {
    expect(slug('Settlements Order settlement failed in prod!')).toBe('settlements-order-settlement-failed-in-prod');
  });

  it('fills templates for the preview', () => {
    expect(fillTemplate('{{workflowName}} failed at {{step}} ({{nope}})', { workflowName: 'order-settlement', step: 'settle' }))
      .toBe('order-settlement failed at settle (‹nope›)');
  });

  it('builds the template rules of the empty state for the right team', () => {
    const r = templateRule('settlements', 'workflow');
    expect(r.team_id).toBe('settlements');
    expect(r.route.recipients).toEqual([{ team: 'settlements', on_call: true }]);
    expect(r.severity).toBe('critical');
  });
});
