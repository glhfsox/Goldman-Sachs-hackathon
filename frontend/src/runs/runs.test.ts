import { describe, expect, it } from 'vitest';
import type { RunNode, RunProgress } from '../api/types';
import { nodeView, paramsLine, progressChip, waitsForPerson } from './runs';

const progress = (status: RunProgress['status'], done = 1, total = 3): RunProgress => ({
  run_id: 'run-1', runbook_id: 'rb', status, steps_total: total, steps_done: done, steps: [],
});

describe('alert card chip', () => {
  it('shows nothing until progress is loaded, and nothing for resolved alerts', () => {
    expect(progressChip(undefined, true, false)).toBeNull();
    expect(progressChip(progress('running'), true, true)).toBeNull();
  });

  it('says a runbook waits for a person when no run was started', () => {
    expect(progressChip(null, true, false)!.text).toBe('Runbook waits for a person');
    expect(progressChip(null, false, false)).toBeNull();
  });

  it('follows the run status', () => {
    expect(progressChip(progress('running', 2, 4), true, false)!.text).toBe('Runbook running · 2 of 4');
    expect(progressChip(progress('failed'), true, false)!.text).toBe('Runbook failed');
    expect(progressChip(progress('done'), true, false)!.text).toBe('Runbook done');
  });

  it('puts only never-started runbooks in "Waiting for a person"', () => {
    expect(waitsForPerson(null, true, false)).toBe(true);
    expect(waitsForPerson(progress('running'), true, false)).toBe(false);
    expect(waitsForPerson(null, true, true)).toBe(false);
  });
});

describe('steps', () => {
  const step = (over: Partial<RunNode>): RunNode => ({ id: 'step-0', text: 't', action: 'http_get', risk: 'read_only', params: { url: 'http://x/health' }, ...over });

  it('lets a person tick off a pending text-only step in a live run', () => {
    expect(nodeView(step({ action: null, status: 'pending' }), 3, true).canMarkDone).toBe(true);
    expect(nodeView(step({ action: null, status: 'pending' }), 3, false).canMarkDone).toBe(false);
    expect(nodeView(step({ status: 'pending' }), 0, true).canMarkDone).toBe(false);
  });

  it('shows results and failures', () => {
    expect(nodeView(step({ status: 'succeeded', result: { ok: true, output: '503' } }), 0, true).note).toBe('Done: 503');
    expect(nodeView(step({ status: 'failed', result: { ok: false, error: 'timeout' } }), 0, true).note).toContain('Failed: timeout');
  });

  it('warns in the preview when the alert lacks a placeholder value', () => {
    expect(nodeView(step({ missing: ['raw.workflowId'] }), 0, false).note).toContain('raw.workflowId');
  });

  it('prints filled parameters on one line', () => {
    expect(paramsLine(step({}))).toBe('http_get · url http://x/health');
    expect(paramsLine(step({ action: null, params: null }))).toContain('Text only');
  });
});
