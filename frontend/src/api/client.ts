import type {
  ActionDef, Alert, AlertDetail, AlertStatus, Mapping, Preferences, Proposal, ProposalKind, Rule, Runbook,
  RunbookRun, RunNode, RunPreview, SchemaSample, Source, Team, User,
} from './types';

// Every call goes to /api on our own origin; the dev server (or nginx) forwards it to the API service.
const BASE = '/api';

export interface ProblemItem {
  path: string;
  message: string;
}

/** A failed call, with the API's problem+json fields (contracts/api.md: error, message, details). */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string | null;
  readonly details: ProblemItem[];

  constructor(status: number, data: unknown) {
    const d = (data && typeof data === 'object' ? data : {}) as Record<string, unknown>;
    const message = (d.message as string) || (d.detail as string) || (d.title as string) || `Request failed (${status})`;
    super(message);
    this.status = status;
    this.code = (d.error as string) || null;
    this.details = Array.isArray(d.details) ? (d.details as ProblemItem[]) : [];
  }
}

let currentUser: string | null = null;

/** The person the UI acts as: sent as X-User on every request (contracts/api.md, no auth in the PoC). */
export function setApiUser(userId: string | null): void {
  currentUser = userId;
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (currentUser) headers['X-User'] = currentUser;
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const res = await fetch(BASE + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await res.text();
  let data: unknown = null;
  if (text) {
    try {
      data = JSON.parse(text);
    } catch {
      data = { message: text };
    }
  }
  if (!res.ok) throw new ApiError(res.status, data);
  return data as T;
}

const q = (params: Record<string, string | number | undefined | null>): string => {
  const entries = Object.entries(params).filter(([, v]) => v !== undefined && v !== null && v !== '');
  return entries.length ? '?' + new URLSearchParams(entries.map(([k, v]) => [k, String(v)])).toString() : '';
};

/** null instead of an error for 404 (e.g. a user without saved preferences). */
async function orNull<T>(p: Promise<T>): Promise<T | null> {
  try {
    return await p;
  } catch (e) {
    if (e instanceof ApiError && e.status === 404) return null;
    throw e;
  }
}

export const api = {
  users: () => request<User[]>('GET', '/users'),
  teams: () => request<Team[]>('GET', '/teams'),

  alerts: (p: { team?: string; status?: AlertStatus; limit?: number } = {}) => request<Alert[]>('GET', '/alerts' + q({ limit: 200, ...p })),
  alert: (id: string) => request<AlertDetail>('GET', `/alerts/${encodeURIComponent(id)}`),
  ack: (id: string) => request<Alert>('POST', `/alerts/${encodeURIComponent(id)}/ack`),
  resolve: (id: string) => request<Alert>('POST', `/alerts/${encodeURIComponent(id)}/resolve`),

  rules: (team?: string) => request<Rule[]>('GET', '/rules' + q({ team })),
  rule: (id: string) => request<Rule>('GET', `/rules/${encodeURIComponent(id)}`),
  createRule: (rule: Rule) => request<Rule>('POST', '/rules', rule),
  saveRule: (rule: Rule) => request<Rule>('PUT', `/rules/${encodeURIComponent(rule.id)}`, rule),

  runbooks: (team?: string) => request<Runbook[]>('GET', '/runbooks' + q({ team })),
  runbook: (id: string) => request<Runbook>('GET', `/runbooks/${encodeURIComponent(id)}`),
  createRunbook: (b: { team_id: string; title: string; format: string; source: string }) => request<Runbook>('POST', '/runbooks', b),
  saveRunbook: (id: string, b: { canonical?: unknown; status?: string }) => request<Runbook>('PUT', `/runbooks/${encodeURIComponent(id)}`, b),
  acceptRunbook: (id: string, version: number) => request<Runbook>('POST', `/runbooks/${encodeURIComponent(id)}/accept` + q({ version })),
  actions: () => request<ActionDef[]>('GET', '/actions'),

  runPreview: (alertId: string, runbookId: string) => request<RunPreview>('GET', `/alerts/${encodeURIComponent(alertId)}/runs/preview` + q({ runbook_id: runbookId })),
  startRun: (alertId: string, runbookId: string) => request<RunbookRun>('POST', `/alerts/${encodeURIComponent(alertId)}/runs`, { runbook_id: runbookId }),
  run: (id: string) => request<RunbookRun>('GET', `/runs/${encodeURIComponent(id)}`),
  cancelRun: (id: string) => request<RunbookRun>('POST', `/runs/${encodeURIComponent(id)}/cancel`),
  nodeDone: (runId: string, nodeId: string) => request<RunNode>('POST', `/runs/${encodeURIComponent(runId)}/nodes/${encodeURIComponent(nodeId)}/done`),

  sources: () => request<Source[]>('GET', '/sources'),
  createSource: (b: { id: string; kind: string; config: Record<string, unknown> }) => request<Source>('POST', '/sources', b),
  saveSource: (s: Source) => request<Source>('PUT', `/sources/${encodeURIComponent(s.id)}`, { kind: s.kind, config: s.config, enabled: s.enabled }),
  samples: (status?: SchemaSample['status']) => request<SchemaSample[]>('GET', '/schema-samples' + q({ status })),
  mappings: () => request<Mapping[]>('GET', '/mappings'),
  saveMapping: (m: Mapping, fingerprint?: string) => request<Mapping>('PUT', `/mappings/${encodeURIComponent(m.id)}` + q({ fingerprint }), m),

  preferences: (userId: string) => orNull(request<Preferences>('GET', `/users/${encodeURIComponent(userId)}/preferences`)),
  savePreferences: (p: Preferences) => request<Preferences>('PUT', `/users/${encodeURIComponent(p.user_id)}/preferences`, p),

  proposals: (status?: Proposal['status']) => request<Proposal[]>('GET', '/proposals' + q({ status, limit: 200 })),
  propose: (alertId: string, kind: ProposalKind, body: Record<string, unknown>) => request<Proposal>('POST', `/alerts/${encodeURIComponent(alertId)}/proposals`, { kind, body }),
  acceptProposal: (id: string) => request<Proposal>('POST', `/proposals/${encodeURIComponent(id)}/accept`),
  rejectProposal: (id: string) => request<Proposal>('POST', `/proposals/${encodeURIComponent(id)}/reject`),
};

/** A message for a toast: the API's own message, plus the first field problem when there is one. */
export function errorText(e: unknown): string {
  if (e instanceof ApiError) {
    const first = e.details[0];
    return first ? `${e.message}: ${first.path} ${first.message}` : e.message;
  }
  return e instanceof Error ? e.message : 'Something went wrong';
}
