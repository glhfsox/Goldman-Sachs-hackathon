# API for the UI: REST and WebSocket

Base URL `http://localhost:8000/api`. JSON in and out. No auth in the PoC: every request carries
`X-User: <user id>` from the UI's user picker; it becomes the `actor` in the audit log.
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
| `POST /rules`, `PUT /rules/{id}` | `Rule` | `Rule`, or `422 invalid_rule` |
| `GET /mappings`, `PUT /mappings/{id}` | `Mapping` | `Mapping`, or `422 invalid_mapping` |
| `GET /schema-samples?status=` | | unmapped schemas waiting for a mapping |
| `GET /sources`, `GET /sources/{id}`, `POST /sources` | `{id, kind, config}` | `Source`. `id`: lowercase letters, digits, `-`; a `kafka` source needs `config.topic`; `422 invalid_source` otherwise, `409 conflict` for a taken id |
| `PUT /sources/{id}` | `{kind, config, enabled}` | `Source` (also how a source is disabled) |
| `GET /teams`, `GET /users` | | lists for pickers; a team includes `members` and who is on call |
| `GET /users/{id}/preferences`, `PUT /users/{id}/preferences` | `Preferences` | `Preferences` |
| `GET /runbooks?team=`, `POST /runbooks` | `{team_id, title, format, source}` | `Runbook` (status `draft` until converted and activated) |
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
