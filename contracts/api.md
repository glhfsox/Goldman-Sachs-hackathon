# API for the UI: REST and WebSocket

Base URL `http://localhost:8000/api`. JSON in and out. No auth in the PoC: every request carries
`X-User: <user id>` from the UI's user picker; it becomes the `actor` in the audit log. A service
calling the api sends its own name there: the ai-worker sends `X-User: ai-worker`.
Errors: `{"error": "<code>", "message": "...", "details": [...]}` with a 4xx status; the api sends them as `application/problem+json`, so `title`, `status` and `detail` are there too. Codes: `not_found` (404), `conflict` (409), `invalid_<thing>` (422).

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
| `GET /alerts?team=&status=&limit=` | | `[Alert]`, newest first |
| `GET /alerts/{id}` | | `{alert, incident, timeline: [alert_events], notifications: [...], runbook}` |
| `POST /alerts/{id}/ack` | | `Alert` |
| `POST /alerts/{id}/resolve` | | `Alert` |
| `GET /rules?team=` | | `[Rule]` ([rules.md](rules.md)) |
| `GET /rules/{id}` | | `Rule` |
| `POST /rules`, `PUT /rules/{id}` | `Rule` | `Rule`, or `422 invalid_rule`. `409 conflict` for a taken id on `POST`; on `PUT` the body `id` must equal the URL id |
| `GET /mappings`, `PUT /mappings/{id}[?fingerprint=<fingerprint>]` | `Mapping` | `Mapping`, or `422 invalid_mapping`. `PUT` creates or replaces; `GET` lists highest `priority` first. With `fingerprint`, that schema sample becomes `mapped` |
| `GET /schema-samples?status=` | | unmapped schemas waiting for a mapping |
| `PUT /schema-samples/{fingerprint}` | `{status: "failed", error}` | the sample; the ai-worker calls it when it could not produce a valid mapping, so a person writes one in the UI |
| `GET /sources`, `GET /sources/{id}`, `POST /sources` | `{id, kind, config}` | `Source`. `id`: lowercase letters, digits, `-`; a `kafka` source needs `config.topic`; `422 invalid_source` otherwise, `409 conflict` for a taken id |
| `PUT /sources/{id}` | `{kind, config, enabled}` | `Source` (also how a source is disabled) |
| `GET /teams` | | `[{id, name, members: [{user_id, name, on_call}]}]`, for pickers; read-only (seeded) |
| `GET /users` | | `[{id, name, email, phone}]`, for the user picker; read-only (seeded) |
| `GET /users/{id}/preferences`, `PUT /users/{id}/preferences` | `Preferences` ([rules.md](rules.md) §5) | `Preferences`. `PUT` creates or replaces; `422 invalid_preferences` (one `details` entry per wrong path, `user_id` must equal the URL id), `404 not_found` for an unknown user, or for `GET` a user without preferences |
| `GET /runbooks?team=`, `POST /runbooks` | `{team_id, title, format, source}` | `Runbook` (status `draft` until the ai-worker has converted it, [runbooks.md](runbooks.md)) |
| `PUT /runbooks/{id}` | `{canonical?, status?}` | `Runbook` |
| `POST /alerts/{id}/actions` | `{runbook_id, step_index}` | `ActionRun`: `running`, or `waiting_approval` when the step needs approval |
| `POST /actions/{id}/approve`, `POST /actions/{id}/reject` | | `ActionRun` |
| `POST /alerts/{id}/proposals` | `{kind, body}` | `Proposal` |
| `GET /proposals?status=open` | | `[Proposal]` |
| `POST /proposals/{id}/accept`, `POST /proposals/{id}/reject` | | `Proposal` |

`runbook` in `GET /alerts/{id}` is the canonical runbook ([runbooks.md](runbooks.md)) with every
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
if that alert is open on screen). On reconnect the UI refetches `GET /alerts`.

## Short link

`{PUBLIC_BASE_URL}/a/{alert_id}` opens that alert in the UI. It is the link placed in SMS and email.
