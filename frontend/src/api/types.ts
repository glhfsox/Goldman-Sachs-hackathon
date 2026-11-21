// Shapes of contracts/api.md (REST) and contracts/events.md §5 (live messages). JSON is snake_case.

export type Severity = 'info' | 'warning' | 'error' | 'critical';
export const SEVERITIES: Severity[] = ['critical', 'error', 'warning', 'info'];
export type AlertStatus = 'open' | 'acked' | 'resolved';
export type Channel = 'ui' | 'email' | 'sms' | 'call';
export const CHANNELS: Channel[] = ['call', 'sms', 'email', 'ui'];
export type Risk = 'read_only' | 'auto' | 'approval';

export interface Alert {
  id: string;
  rule_id: string;
  team_id: string;
  incident_id: string | null;
  status: AlertStatus;
  severity: Severity;
  title: string;
  body: string | null;
  fields: Record<string, unknown> | null;
  color: string | null;
  count: number;
  first_seen: string;
  last_seen: string;
  first_received_at?: string | null;
  runbook_id: string | null;
  acked_by: string | null;
  resolved_by: string | null;
  sample_event?: Record<string, unknown> | null;
}

export interface AlertEvent {
  id: string;
  alert_id: string;
  at: string;
  actor: string;
  kind: string;
  data: Record<string, unknown> | null;
}

export interface Notification {
  id: string;
  user_id: string;
  channel: string;
  status: string;
  reason: string | null;
  send_at: string | null;
  sent_at: string | null;
  error: string | null;
}

export interface Incident {
  id: string;
  root_alert_id: string;
  status: string;
  created_at: string;
}

export interface AlertDetail {
  alert: Alert;
  incident: Incident | null;
  timeline: AlertEvent[];
  notifications: Notification[];
  runbook: RunProgress | null;
}

export interface User {
  id: string;
  name: string;
  email: string;
  phone: string;
}

export interface Team {
  id: string;
  name: string;
  members: { user_id: string; name: string; on_call: boolean }[];
}

export interface Leaf {
  field: string;
  op: string;
  value?: unknown;
}
export type Condition = { all: Condition[] } | { any: Condition[] } | Leaf;

export type Recipient = { team: string; on_call?: boolean } | { user: string };

export interface Rule {
  id: string;
  team_id: string;
  name?: string | null;
  enabled: boolean;
  when: Condition;
  severity?: Severity | null;
  alert_key?: string[] | null;
  window_s?: number | null;
  route: { recipients: Recipient[]; channels?: Channel[] | null };
  present?: { title?: string | null; body?: string | null; fields?: string[] | null; color?: string | null } | null;
  runbook_id?: string | null;
}

export interface Preferences {
  user_id: string;
  timezone?: string | null;
  by_severity: Partial<Record<Severity, Channel[]>>;
  quiet_hours?: { start: string; end: string; downgrade?: Partial<Record<Channel, Channel>> } | null;
  wake_me?: boolean;
  max_push_per_hour?: number;
}

export interface Source {
  id: string;
  kind: 'http' | 'kafka';
  config: Record<string, unknown>;
  enabled: boolean;
  updated_at?: string;
}

export interface SchemaSample {
  fingerprint: string;
  source_id: string;
  sample: unknown;
  status: 'pending' | 'mapped' | 'failed';
  error: string | null;
  created_at: string;
}

export interface MappingField {
  path?: string;
  const?: unknown;
  format?: string;
}

export interface Mapping {
  id: string;
  status: 'active' | 'disabled';
  origin: string;
  priority: number;
  match: Condition;
  fields: Record<string, MappingField>;
  severity_map?: Record<string, string> | null;
  dedup_fields?: string[] | null;
  updated_at?: string;
}

export interface RunbookStep {
  text: string;
  action?: string;
  params?: Record<string, unknown>;
  approval?: string;
  when?: string;
  link?: string;
  id?: string;
  needs?: string[];
  if?: { step: string; status: string };
}

export interface Canonical {
  id: string;
  title: string;
  matches?: Condition;
  steps: RunbookStep[];
}

export interface Runbook {
  id: string;
  team_id: string;
  title: string;
  format: string;
  source: string;
  canonical: Canonical | null;
  status: 'draft' | 'active';
  version: number;
  accepted_by: string | null;
  accepted_at: string | null;
  updated_at: string;
}

export interface ActionDef {
  name: string;
  risk: Risk;
  default_target?: string | null;
  url_method?: string | null;
  timeout_s?: number | null;
}

export type ProposalKind = 'threshold' | 'template' | 'runbook' | 'rule_severity' | 'runbook_start';

export interface Proposal {
  id: string;
  alert_id: string;
  kind: ProposalKind;
  // The body shape depends on the kind (contracts/api.md); people's suggestions are free-form.
  body: Record<string, any>;
  author: string;
  status: 'open' | 'accepted' | 'rejected';
  decided_by: string | null;
  created_at: string;
  decided_at: string | null;
}

export type LiveMessage =
  | { type: 'alert'; change: string; alert: Alert }
  | { type: 'count'; alert_id: string; count: number; last_seen: string }
  | { type: 'timeline'; alert_id: string };

// Runbook runs (contracts/api.md, issue #82). Automatic start is not served yet: every run is started by a person.
export type RunStatus = 'scheduled' | 'running' | 'done' | 'failed' | 'cancelled';
export type NodeStatus = 'pending' | 'ready' | 'running' | 'succeeded' | 'failed' | 'skipped' | 'blocked' | 'cancelled';

export interface RunNode {
  id: string;
  step_index?: number;
  text: string;
  action: string | null;
  risk: Risk;
  params: Record<string, unknown> | null;
  needs?: string[] | null;
  status?: NodeStatus;
  attempt?: number;
  result?: { ok?: boolean; error?: string | null; output?: string | null } | null;
  missing?: string[];
  started_at?: string | null;
  finished_at?: string | null;
}

export interface RunPreview {
  runbook_id: string;
  runbook_version: number;
  risk: Risk;
  nodes: RunNode[];
}

export interface RunbookRun {
  id: string;
  alert_id: string;
  runbook_id: string;
  runbook_version: number;
  risk: Risk;
  trigger: 'auto' | 'manual';
  status: RunStatus;
  start_at: string | null;
  approved_by: string | null;
  started_at: string | null;
  finished_at: string | null;
  cancelled_by: string | null;
  nodes: RunNode[];
}

/** `runbook` in GET /alerts/{id}: how far the alert's latest run has got, or null when none was started. */
export interface RunProgress {
  run_id: string;
  runbook_id: string;
  status: RunStatus;
  steps_total: number;
  steps_done: number;
  steps: { node: string; status: NodeStatus }[];
}
