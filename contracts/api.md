# API for the UI: REST and WebSocket

Base URL `http://localhost:8000/api`. JSON in and out. No auth in the PoC: every request carries
`X-User: <user id>` from the UI's user picker; it becomes the `actor` in the audit log. A service
calling the api sends its own name there: the ai-worker sends `X-User: ai-worker`.
`X-User` is required on `ack` and `resolve`: a missing header is `422 invalid_user`, an unknown user id is `404 not_found`.
Errors: `{"error": "<code>", "message": "...", "details": [...]}` with a 4xx status; the api sends them as `application/problem+json`, so `title`, `status` and `detail` are there too. Codes: `forbidden` (403), `not_found` (404), `conflict` (409), `invalid_<thing>` (422).

## Alert object

Also the `alert` in Kafka `alerts.changes` and Redis `ui.alerts` ([events.md](events.md)).

```json
{
  "id": "01JD3K8QK2...",
  "rule_id": "settlements-workflow-failed",
  "team_id": "settlements",
  "incident_id": null,
  "status": "open",
  "severity": "critical",
  "title": "order-settlement failed at step settle",
  "body": "Counterparty service did not respond in 5000 ms",
  "fields": {"step": "settle", "error.code": "TIMEOUT", "error.message": "Counterparty service did not respond in 5000 ms"},
  "color": "red",
  "count": 500,
  "first_seen": "2026-11-20T10:15:01.000Z",
  "last_seen": "2026-11-20T10:16:01.004Z",
  "first_received_at": "2026-11-20T10:15:02.123Z",
  "runbook_id": "settlement-timeout",
  "acked_by": null,
  "resolved_by": null,
  "sample_event": {"...": "envelope of the first event"}
}
```

`status`: `open`, `acked`, `resolved`. `incident_id` links alerts merged into one incident; the
incident's `root_alert_id` is the earliest alert on the most upstream service.

## Endpoints

| Method and path | Body | Returns |
|---|---|---|
| `GET /alerts?team=&status=&limit=` | | `[Alert]`, newest `first_seen` first; `limit` defaults to 50, at most 200 |
| `GET /alerts/{id}` | | `{alert, incident, timeline: [alert_events], notifications: [...], runbook}` |
| `POST /alerts/{id}/ack` | | `Alert` as published, with `status: acked` and `acked_by`. `409 conflict` unless the alert is `open` |
| `POST /alerts/{id}/resolve` | | `Alert` as published, with `status: resolved` and `resolved_by`. `409 conflict` when it is already `resolved` |
| `GET /rules?team=` | | `[Rule]` ([rules.md](rules.md)) |
| `GET /rules/{id}` | | `Rule` |
| `POST /rules`, `PUT /rules/{id}` | `Rule` | `Rule`, or `422 invalid_rule`. `409 conflict` for a taken id on `POST`; on `PUT` the body `id` must equal the URL id |
| `GET /mappings`, `PUT /mappings/{id}[?fingerprint=<fingerprint>]` | `Mapping` | `Mapping`, or `422 invalid_mapping`. `PUT` creates or replaces; `GET` lists highest `priority` first. With `fingerprint`, that schema sample becomes `mapped` in the same save; an unknown fingerprint is `404` and nothing is saved |
| `GET /schema-samples?status=` | | `[{fingerprint, source_id, sample, status, error, created_at}]`, oldest first; `status` is `pending`, `mapped` or `failed` |
| `PUT /schema-samples/{fingerprint}` | `{status: "failed", error}` | the sample; the ai-worker calls it when it could not produce a valid mapping, so a person writes one in the UI. `422 invalid_schema_sample` for any other status, `404` for an unknown fingerprint, `409` when the sample is already `mapped` |
| `GET /sources`, `GET /sources/{id}`, `POST /sources` | `{id, kind, config}` | `Source`. `id`: lowercase letters, digits, `-`; a `kafka` source needs `config.topic`; `422 invalid_source` otherwise, `409 conflict` for a taken id |
| `PUT /sources/{id}` | `{kind, config, enabled}` | `Source` (also how a source is disabled) |
| `GET /teams` | | `[{id, name, members: [{user_id, name, on_call}]}]`, for pickers; read-only (seeded) |
| `GET /users` | | `[{id, name, email, phone}]`, for the user picker; read-only (seeded) |
| `GET /users/{id}/preferences`, `PUT /users/{id}/preferences` | `Preferences` ([rules.md](rules.md) §5) | `Preferences`. `PUT` creates or replaces; `422 invalid_preferences` (one `details` entry per wrong path, `user_id` must equal the URL id), `404 not_found` for an unknown user, or for `GET` a user without preferences |
| `GET /runbooks?team=`, `POST /runbooks` | `{team_id, title, format, source}` | `Runbook` (status `draft` and `version` 1 until the ai-worker has converted it, [runbooks.md](runbooks.md)). The id is made from `title` (lowercase, `-`, a `-2` suffix when taken) |
| `GET /runbooks/{id}` | | `Runbook` |
| `PUT /runbooks/{id}` | `{canonical?, status?}` | `Runbook`. A new `canonical` bumps `version` and clears `accepted_by`; `canonical.id` must equal the URL id; `active` needs a canonical. `422 invalid_runbook` with one `details` entry per wrong path |
| `POST /runbooks/{id}/accept?version=` | none, `X-User` of a person; `version` = the version the person reviewed (required, `422` without it) | `Runbook` with `accepted_by` and `accepted_at`. `403` for a service actor or an unknown user, `409` when the runbook is not `active` or `version` is not the current one |
| `POST /alerts/{id}/actions` | `{runbook_id, step_index}` | `ActionRun`: `running`, or `waiting_approval` when the step needs approval |
| `POST /actions/{id}/approve`, `POST /actions/{id}/reject` | | `ActionRun` |
| `POST /alerts/{id}/proposals` | `{kind, body}` with `X-User` (a person or `ai-worker`) | `Proposal`, `201`. An identical open proposal (same alert, kind and body) is returned with `200` instead of a second one. `422 invalid_proposal` (one `details` entry per wrong path, each starting with `body.` or `kind`), `404` for an unknown alert or an `X-User` that is neither a user nor `ai-worker` |
| `GET /proposals?status=&limit=` | | `[Proposal]`, newest first; `status` is `open`, `accepted` or `rejected`; `limit` defaults to 50, at most 200 |
| `GET /proposals/{id}` | | `Proposal`, `404` when unknown |
| `POST /proposals/{id}/accept`, `POST /proposals/{id}/reject` | | `Proposal`. Only a person: `403` for `ai-worker` or an unknown `X-User`. `409` when the proposal is not `open` |

A `Proposal` is `{id, alert_id, kind, body, author, status, decided_by, created_at, decided_at}`; `kind` is `threshold`, `template`, `runbook`, `rule_severity` or `runbook_start`. Accepting applies the proposal: `rule_severity` sets the rule's `severity` (published on `config.changes`; `409` when the rule no longer exists); `runbook` saves `body.canonical` as a new runbook that is `active` and accepted by that person, with an id made from `body.title` (the id inside `canonical` is replaced); `threshold` and `template` only change the status, a person applies them; `runbook_start` answers `409` until the run model exists and a rule can store the start mode. Creating and deciding add the timeline rows `proposal_created` and `proposal_decided`.

A `Runbook` is `{id, team_id, title, format, source, canonical, status, version, accepted_by, accepted_at, updated_at}`;
`canonical` is `null` on a draft.

`incident` and `runbook` in `GET /alerts/{id}` are `null` until incidents and runbooks are built. `runbook`, once built, is the canonical runbook ([runbooks.md](runbooks.md)) with every
`{{placeholder}}` already filled from the alert's `sample_event`, plus the latest `ActionRun` per step.

Each item of `notifications` carries what the UI needs for "why you got this / why not":
`{id, user_id, channel, status, reason, send_at, sent_at, error}`.

Each item of `timeline` is an `alert_events` row `{id, alert_id, at, actor, kind, data}`
([schemas/api.md](schemas/api.md)). For the `action_*` kinds, `data` is
`{"action_run": {id, runbook_id, step_index, action, params, status, result}}`.

A proposal of kind `runbook` has the body `{team_id, title, canonical}`, where `canonical` is a
canonical runbook ([runbooks.md](runbooks.md)). The ai-worker files one when an alert is resolved after
people ran actions on it; accepting it saves that runbook as `active`. Example:

```json
{"kind": "runbook", "body": {"team_id": "settlements", "title": "WORKFLOW_FAILED in prod",
 "canonical": {"id": "workflow-failed-prod", "title": "WORKFLOW_FAILED in prod",
  "matches": {"all": [{"field": "type", "op": "eq", "value": "WORKFLOW_FAILED"}, {"field": "env", "op": "eq", "value": "prod"}]},
  "steps": [{"text": "Check counterparty-service health", "action": "http_get",
             "params": {"url": "http://counterparty-service/health"}, "approval": "none"}]}}}
```

A proposal of kind `rule_severity` suggests the severity a rule should fix for its alerts (the rule's
`severity`, [rules.md](rules.md) §4). The ai-worker files it on one of the rule's alerts: on the rule's
first alert (`mode: "first"`, judged from the rule and that alert alone, `stats: null`), and each time the
rule's resolved alerts reach a multiple of 20 (`mode: "history"`, judged from how people reacted to its
most recent resolved alerts: at most 200, resolved in the last 30 days, and only when at least 20 are left).
It is filed only when the suggestion differs from the rule's current `severity` with a confidence of at
least 0.5. `current_severity` is null when the rule fixes none. `reasons` are plain sentences for the UI.
Accepting it sets the rule's `severity` to `body.severity` (a `PUT /rules/{rule_id}`, published on
`config.changes` like any rule change). Example:

```json
{"kind": "rule_severity", "body": {"rule_id": "settlements-workflow-failed", "team_id": "settlements",
 "severity": "critical", "current_severity": "warning", "confidence": 0.82,
 "probabilities": {"info": 0.02, "warning": 0.06, "error": 0.1, "critical": 0.82}, "mode": "history",
 "stats": {"window": {"max_alerts": 200, "max_age_days": 30}, "resolved_alerts": 20, "acked_share": 0.7,
           "median_minutes_to_ack": 3.0, "auto_resolved_unacked_share": 0.25, "reopened_share": 0.05,
           "median_count": 4.0, "severities": {"critical": 20}},
 "reasons": ["it comes from production", "people usually react within minutes",
             "14 of 20 alerts were acknowledged by a person, median 3 min",
             "5 of 20 resolved on their own without anyone acknowledging them", "1 of 20 were reopened",
             "median 4 events per alert"]}}
```

### Runbook runs: needed by the ai-worker, not served by the api yet (issue #75)

On top of the run model of issue #73 (run risk `read_only` / `auto` / `approval`; an `auto` run is created
`scheduled` with `start_at` = now + cancel window). `backend/ai-worker/scripts/stub_api.py` serves these as the
reference until the api does.

| Method and path | Body | Returns |
|---|---|---|
| `GET /runs?risk_check=pending` | | `[RunbookRun]`: status `scheduled`, risk `auto`, `risk_check: null`. The ai-worker polls it every 2 s |
| `POST /runs/{id}/risk-check` | `{verdict, confidence, reasons}`, `X-User: ai-worker` | `RunbookRun`. While the run is `scheduled` a `hold` always applies (the run goes to `waiting_approval`), even over an earlier `clear`; a second `clear` changes nothing. `409` when the run is no longer `scheduled` (so a `clear` after a `hold` changes nothing); the same verdict twice changes nothing |
| `POST /runs/{id}/approve` | none, `X-User` of a person | `RunbookRun`, now `running`. `403` for a service actor |
| `GET /runs?finished_since=<ISO time>&limit=` | | `[RunbookRun]` with status `done`, `failed` or `cancelled`, oldest first. The ai-worker polls it every 30 s from 5 minutes before the newest run it holds |

- `verdict`: `clear` (the run starts at `start_at`) or `hold` (it goes to `waiting_approval` with the reasons and
  starts when a person approves). `confidence`: 0 to 1. `reasons`: at most 6 plain sentences for the UI. The
  ai-worker sends `clear` only when Jev chose it with confidence of at least 0.6.
- No verdict by `start_at`: the api moves the run to `waiting_approval` (reason "risk check did not answer"); a run
  never starts unchecked. The ai-worker never starts or approves a run.
- A `RunbookRun` here has `id`, `alert_id`, `rule_id`, `team_id`, `runbook_id`, `risk`, `trigger` (`auto` or
  `manual`), `status`, `start_at`, `risk_check` (`{verdict, confidence, reasons}` or null) and
  `nodes: [{id, action, risk, params, text}]` with params filled; a finished one also `approved_by`, `approved_at`,
  `started_at`, `finished_at`, `cancelled_by` and each node's `status`.

```json
POST /api/runs/run-7f3a/risk-check
{"verdict": "hold", "confidence": 0.94,
 "reasons": ["it runs in production", "customers or money are affected right now",
             "the action cannot be undone easily"]}
```

A proposal of kind `runbook_start` says how runs of `runbook_id` on `rule_id` should start: `auto` or
`approval`. The ai-worker files it on the alert of the pair's latest finished run each time the pair's finished
runs reach a multiple of 10, judged from its most recent finished runs (at most 50, finished in the last 30 days,
at least 10 left), when Jev's choice differs from `current_start` (`auto` when at least half of the window's runs had `trigger: auto`)
with confidence of at least 0.6. Accepting it changes the rule so that runs of `runbook_id` on `rule_id` start as
`body.start` (the shape is the api's, issue #75 question 3). Example:

```json
{"kind": "runbook_start", "body": {"rule_id": "settlements-workflow-failed", "team_id": "settlements",
 "runbook_id": "workflow-retry", "start": "auto", "current_start": "approval", "confidence": 0.66,
 "probabilities": {"auto": 0.83, "approval": 0.17},
 "stats": {"window": {"max_runs": 50, "max_age_days": 30}, "runs": 10, "auto_started_share": 0.0,
           "approved_share": 1.0, "median_seconds_from_alert_to_approve": 8, "cancelled_before_start_share": 0.0,
           "cancelled_after_start_share": 0.0, "held_share": 0.0, "failed_share": 0.0,
           "rule_alerts": 12, "alerts_without_run": 2, "alerts_without_run_share": 0.167},
 "reasons": ["10 of 10 runs were approved by a person, median 8 s after the alert", "no run was cancelled",
             "no run failed", "2 of 12 alerts of this rule got no run"]}}
```

`median_seconds_from_alert_to_approve` is measured from the alert's `opened_at` (null when the ai-worker never saw
the alert). `rule_alerts` counts the rule's alerts opened in the same 30 days and `alerts_without_run` those that got
no run; all three alert fields are null when the ai-worker holds no alert of the rule.

The api accepts exactly what the processor reads: unknown fields, wrong types and unknown ops are `422`, and `details` has one entry per wrong path.

Example invalid rule:

```json
{"error": "invalid_rule", "message": "1 problem",
 "details": [{"path": "when.all[1].op", "message": "unknown op 'gtt'"}]}
```

## WebSocket

`ws://localhost:8000/ws?team=<team id>` (omit `team` for all teams). Server to client only; the three
message shapes are exactly the Redis `ui.alerts` messages in [events.md](events.md):
`alert` (insert or replace the card), `count` (update the counter), `timeline` (refetch `GET /alerts/{id}`
if that alert is open on screen). On reconnect the UI refetches `GET /alerts`. With `team`, a message is sent only when its alert is that team's; a message for an alert the api has not stored yet is not sent to such a client. A person's ack or resolve is sent as an `alert` message by the api itself, since the processor does not publish it.

## Short link

`{PUBLIC_BASE_URL}/a/{alert_id}` opens that alert in the UI. It is the link placed in SMS and email.
