import type { NodeStatus, Risk, RunNode, RunProgress, RunStatus } from '../api/types';

// Runbook runs come from the API (contracts/api.md, issue #82): preview, start, cancel, mark a text step done,
// and `runbook` on GET /alerts/{id} for progress. These helpers only decide how to show them.

/** Automatic start (issue #82, step 4) is not served yet: until it is, every run is started by a person. */
export const AUTO_START_AVAILABLE = false;

export const LIVE: RunStatus[] = ['scheduled', 'running'];

export interface Chip {
  text: string;
  fg: string;
  bg: string;
}

/**
 * The chip on an alert card. `hasRunbook`: the alert names an active runbook.
 * undefined progress = not loaded yet (no chip); null = no run was ever started.
 */
export function progressChip(progress: RunProgress | null | undefined, hasRunbook: boolean, resolved: boolean): Chip | null {
  if (progress === undefined || resolved) return null;
  if (progress === null) return hasRunbook ? { text: 'Runbook waits for a person', fg: '#92400E', bg: '#FEF3C7' } : null;
  switch (progress.status) {
    case 'scheduled': return { text: 'Runbook starts soon', fg: '#1A56DB', bg: '#E8F1FF' };
    case 'running': return { text: `Runbook running · ${progress.steps_done} of ${progress.steps_total}`, fg: '#1A56DB', bg: '#E8F1FF' };
    case 'done': return { text: 'Runbook done', fg: '#15803D', bg: '#DCFCE7' };
    case 'failed': return { text: 'Runbook failed', fg: '#B42318', bg: '#FEE4E2' };
    case 'cancelled': return { text: 'Runbook cancelled', fg: '#3F3F46', bg: '#E4E4E7' };
  }
}

/** "Waiting for a person": an open alert with an active runbook that nobody has started yet. */
export function waitsForPerson(progress: RunProgress | null | undefined, hasRunbook: boolean, resolved: boolean): boolean {
  return hasRunbook && !resolved && progress === null;
}

/** Text-only steps have no action; a person does them and ticks them off. */
export function isManual(n: RunNode): boolean {
  return !n.action;
}

export interface NodeView {
  mark: string;
  markFg: string;
  markBg: string;
  note: string;
  noteColor: string;
  canMarkDone: boolean;
}

/** How one step of a run looks. `live`: the run is scheduled or running. */
export function nodeView(n: RunNode, index: number, live: boolean): NodeView {
  const base: NodeView = { mark: String(index + 1), markFg: '#111', markBg: '#F4F4F6', note: '', noteColor: '#52525B', canMarkDone: false };
  const s: NodeStatus | undefined = n.status;
  const output = n.result?.error || n.result?.output || '';
  switch (s) {
    case 'succeeded':
      return { ...base, mark: '✓', markFg: '#15803D', markBg: '#DCFCE7', note: isManual(n) ? 'Marked done.' : output ? `Done: ${output}` : 'Done.', noteColor: '#15803D' };
    case 'failed':
      return { ...base, mark: '!', markFg: '#B42318', markBg: '#FEE4E2', note: `Failed: ${output || 'no details'}. The run stops here; a person takes over.`, noteColor: '#B42318' };
    case 'running':
      return { ...base, mark: '…', markFg: '#1A56DB', markBg: '#E8F1FF', note: 'Running…', noteColor: '#1A56DB' };
    case 'skipped':
      return { ...base, mark: '–', note: 'Skipped: its condition was not met.' };
    case 'blocked':
      return { ...base, mark: '–', note: 'Blocked: the alert has no value for one of its placeholders.', noteColor: '#92400E' };
    case 'cancelled':
      return { ...base, mark: '–', note: 'Not run: cancelled.' };
    case 'pending':
    case 'ready':
      if (live && isManual(n)) return { ...base, note: 'Waiting for a person to do it.', noteColor: '#92400E', canMarkDone: true };
      return { ...base, note: live ? 'Waits for the steps before it.' : '' };
    default: {
      const missing = n.missing?.length ? `The alert has no value for ${n.missing.join(', ')}, so this step would be blocked.` : '';
      return { ...base, note: missing, noteColor: missing ? '#92400E' : base.noteColor };
    }
  }
}

/** Filled parameters as one line: "http_get · url http://…". */
export function paramsLine(n: RunNode): string {
  const parts = Object.entries(n.params ?? {}).map(([k, v]) => `${k} ${typeof v === 'string' ? v : JSON.stringify(v)}`);
  return [n.action, ...parts].filter(Boolean).join(' · ') || 'Text only: a person does it and ticks it off';
}

export function riskWord(r: Risk): string {
  return r === 'approval' ? 'needs a person' : r === 'auto' ? 'automatic' : 'read-only';
}
