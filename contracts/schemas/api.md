# Database `api` (service: api)

Host `api-db`. Only the api connects. Migrations: `backend/api/src/AlertApi.Infrastructure/Migrations/`.
Shared rules: [../storage.md](../storage.md). The REST shapes built from these tables: [../api.md](../api.md).

## Owned tables: configuration

Every insert, update or soft-delete below is published to Kafka `config.changes`
([../events.md](../events.md) §6) through `outbox`, in the same transaction.

| Table | Columns | Published as kind |
|---|---|---|
| `sources` | `id` text primary key, `kind` text (`http` \| `kafka`), `config` jsonb, `enabled` boolean, `updated_at` timestamptz | `source` |
| `mappings` | `id` text primary key, `status` text (`active` \| `disabled`), `origin` text (`seed` \| `human` \| `llm`), `priority` int, `body` jsonb ([../rules.md](../rules.md) §3), `updated_at` timestamptz | `mapping` |
| `rules` | `id` text primary key, `team_id` text, `enabled` boolean, `body` jsonb ([../rules.md](../rules.md) §4), `updated_at` timestamptz | `rule` |
| `teams` | `id` text primary key, `name` text | `team` |
| `users` | `id` text primary key, `name` text, `email` text, `phone` text | `user` |
| `memberships` | `team_id` text, `user_id` text, `on_call` boolean, primary key (`team_id`, `user_id`) | `membership` |
| `preferences` | `user_id` text primary key, `body` jsonb ([../rules.md](../rules.md) §5) | `preferences` |
| `service_deps` | `service` text, `depends_on` text, primary key on both | `service_dep` |
| `runbooks` | `id` text primary key, `team_id` text, `title` text, `format` text, `source` text, `canonical` jsonb ([../runbooks.md](../runbooks.md)), `status` text (`draft` \| `active`), `version` int, `accepted_by` text null (a person accepted this version), `accepted_at` timestamptz null, `updated_at` timestamptz | `runbook` |

`teams`, `users`, `memberships`, `service_deps` and two `preferences` rows (`anna`, `boris`) are seeded by migration `AddReferenceData` (the brief's four teams, five people, `payments-api` and `order-settlement` depend on `counterparty-service`); the seed is queued on `config.changes` in the same migration like any change. There is no endpoint to change teams, users, memberships or dependencies yet.
`sources` is seeded with the four sources of [../data/sources.md](../data/sources.md) §1 by the `AddSources` migration, which also queues their `config.changes` records in `outbox`.

## Owned tables: activity

### `alert_events`: the audit log and the alert timeline

| Column | Type | Notes |
|---|---|---|
| `id` | text, primary key | `change_id` or `entry_id` of the Kafka record it came from, so applying twice is harmless |
| `alert_id` | text, not null | Indexed with `at` |
| `at` | timestamptz, not null | |
| `actor` | text, not null | A user id, a service name, or `auto` |
| `kind` | text, not null | `opened`, `severity_raised`, `reopened`, `acked`, `resolved`, `notification_sent`, `notification_failed`, `notification_suppressed`, `action_requested`, `action_approved`, `action_rejected`, `action_finished`, `proposal_created`, `proposal_decided` |
| `data` | jsonb, not null | For the `action_*` kinds: `{"action_run": {id, runbook_id, step_index, action, params, status, result}}` |

For rows that come from `alerts.changes`, `data` is `{"severity": ..., "count": ...}` of the alert at that change. `count_threshold` changes update the `alerts` copy only and add no row. A record whose id is already a row is skipped.

Rows come from three places: `alerts.changes` (alert state), `alerts.timeline` (notifications), and the
api's own actions (action runs, proposals). After storing a row the api publishes a `timeline` message
on Redis `ui.alerts`.

Example row:

```json
{"id": "01JD3KA...", "alert_id": "01JD3K8...", "at": "2026-11-20T10:15:03.412Z", "actor": "notifier",
 "kind": "notification_sent", "data": {"notification": {"id": "9f2c...", "channel": "sms", "user_id": "marek",
 "status": "sent"}, "why": "rule settlements-workflow-failed, on-call"}}
```

### `action_targets`: where runbook actions go

Owned by the api and read only by it, so it is not published on `config.changes`. Seeded by migration
`AddActionTargets` with the demo targets of [../runbooks.md](../runbooks.md) §3.

| Column | Type | Notes |
|---|---|---|
| `id` | text, primary key | Lowercase letters, digits, `-` |
| `base_url` | text, not null | Every call of this target goes here |
| `origins` | jsonb, not null | `["http://counterparty-service"]`: origins a runbook `url` may name |
| `operations` | jsonb, not null | `{name: {method, path, risk?}}` |
| `secret_ref` | text | A secret's name, never the secret |
| `version` | int, not null | Starts at 1, + 1 on every change; concurrency token |
| `updated_at` | timestamptz, not null | |

`runbooks.version` is a concurrency token too: a save that read an older version fails with `409`.

### `runbook_runs`

One run of a runbook on an alert (migration `AddRunbookRuns`). Indexed on `(status, finished_at)` and `(alert_id, created_at)`.

| Column | Type | Notes |
|---|---|---|
| `id` | text, primary key | `run-` and 12 hex characters |
| `alert_id`, `rule_id`, `team_id`, `runbook_id` | text, not null | `rule_id` and `team_id` are copied from the alert |
| `runbook_version` | int, not null | The accepted version that was run |
| `risk` | text, not null | `read_only`, `auto` or `approval`: the highest risk of any step, including steps an `if` may skip |
| `trigger` | text, not null | `manual` (automatic start comes with step 4) |
| `status` | text, not null | `scheduled`, `running`, `done`, `failed`, `cancelled`. Unique index `UQ_runbook_runs_live_alert` on `alert_id` where status is `scheduled` or `running`: one live run per alert |
| `start_at` | timestamptz, not null | When the run starts; now for a manual run |
| `approved_by`, `approved_at` | text, timestamptz | The person who started it by hand |
| `created_at` | timestamptz, not null | |
| `started_at`, `finished_at` | timestamptz | |
| `cancelled_by` | text | |

### `action_runs`

One row per step (node) of a run. The ai-worker reads the finished ones through `GET /runs`.

| Column | Type | Notes |
|---|---|---|
| `id` | text, primary key | `<run_id>:<node_id>` |
| `run_id`, `alert_id`, `runbook_id` | text, not null | Unique `(run_id, node_id)` |
| `node_id` | text, not null | The step's `id`, or `step-<index>` |
| `step_index` | int, not null | Order in the runbook, for the UI |
| `text` | text, not null | The step's text with its placeholders filled |
| `action` | text | Action plugin name; null for a text-only step, which a person ticks |
| `params` | jsonb | Placeholders already filled, once, at run creation |
| `risk` | text, not null | The step's risk (`read_only` for a text step) |
| `target_id`, `target_version` | text, int | The action target the request was resolved against |
| `request` | jsonb | `{method, url, body}` frozen at run creation; null for a text step or a blocked step |
| `needs` | jsonb, not null | Ids of the steps it waits for (a text step that was only waited for by default is looked through) |
| `if_step`, `if_status` | text | The step's `if` |
| `retries`, `timeout_s` | int | |
| `status` | text, not null | `pending`, `ready`, `running`, `succeeded`, `failed`, `skipped`, `blocked`, `cancelled` |
| `attempt` | int, not null | Calls made so far; a step may run again while `attempt < 1 + retries` |
| `not_before`, `lease_until` | timestamptz | When a ready step may be claimed; when a running claim expires (executor, step 3c) |
| `idempotency_key` | text, not null, unique | `<run_id>:<node_id>`; sent with every call |
| `result` | jsonb | Cut to 4 KB |
| `started_at`, `finished_at` | timestamptz | |

### `proposals`

| Column | Type | Notes |
|---|---|---|
| `id` | text, primary key | A UUID |
| `alert_id` | text, not null | The alert it was filed on; indexed with `kind` |
| `kind` | text, not null | `threshold`, `template`, `runbook`, `rule_severity`, `runbook_start` |
| `body` | jsonb, not null | The shape per kind is in [../api.md](../api.md) |
| `author` | text, not null | A user id or `ai-worker` |
| `status` | text, not null | `open`, `accepted`, `rejected`; indexed with `created_at` |
| `idempotency_key` | text | The `Idempotency-Key` header of the request that filed it; unique (`UQ_proposals_idempotency_key`), null when none was sent |
| `decided_by` | text | The person who accepted or rejected it |
| `created_at` | timestamptz, not null | |
| `decided_at` | timestamptz | |

### `outbox`

| Column | Type | Notes |
|---|---|---|
| `id` | bigint identity, primary key | Published in `id` order, then deleted |
| `topic` | text, not null | `config.changes` or `alerts.changes` (ack, resolve) |
| `key` | text, not null | |
| `payload` | jsonb, not null | The record, exactly as in [../events.md](../events.md) |
| `created_at` | timestamptz, not null | When the row was written |

## Copies (read models)

| Table | Columns | Fed by |
|---|---|---|
| `alerts` | `id` text primary key, then every field of the [Alert object](../api.md) as a column (`fields` and `sample_event` jsonb) | Kafka `alerts.changes` (insert or replace). `count` and `last_seen` also from Redis `ui.alerts` `count` messages (they only move up) |
| `notifications` | `id` text primary key, `alert_id` text, `user_id` text, `channel` text, `status` text, `reason` text, `send_at` timestamptz, `sent_at` timestamptz, `error` text | Kafka `alerts.timeline`, the `notification` object in `data` |
| `schema_samples` | `fingerprint` text primary key, `source_id` text, `sample` jsonb, `status` text (`pending` \| `mapped` \| `failed`), `error` text (why it failed), `created_at` timestamptz | Kafka `schema.samples` (insert as `pending`); a repeated record for a known fingerprint is ignored; `mapped` when a mapping is saved with `PUT /mappings/{id}?fingerprint=`; `failed` through `PUT /schema-samples/{fingerprint}` ([../api.md](../api.md)) |

The api's `alerts` is a copy: the processor's table is the truth. An ack or resolve is published to
`alerts.changes` and takes effect in every copy, this one included, when that record is consumed.
