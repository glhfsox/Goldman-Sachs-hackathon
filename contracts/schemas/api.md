# Database `api` (service: api)

Host `api-db`. Only the api connects. Migrations: `backend/api/src/AlertApi.Infrastructure/Migrations/`.
Shared rules: [../storage.md](../storage.md). The REST shapes built from these tables: [../api.md](../api.md).

## Owned tables: configuration

Every insert, update or soft-delete below is published to Kafka `config.changes`
([../events.md](../events.md) §6) through `outbox`, in the same transaction.

| Table | Columns | Published as kind |
|---|---|---|
| `sources` | `id` text primary key, `kind` text (`http` \| `kafka`), `config` jsonb, `enabled` boolean, `updated_at` timestamptz | `source` |
| `mappings` | `id` text primary key, `status` text (`active` \| `disabled`), `origin` text (`seed` \| `llm` \| `user`), `priority` int, `body` jsonb ([../rules.md](../rules.md) §3), `updated_at` timestamptz | `mapping` |
| `rules` | `id` text primary key, `team_id` text, `enabled` boolean, `body` jsonb ([../rules.md](../rules.md) §4), `updated_at` timestamptz | `rule` |
| `teams` | `id` text primary key, `name` text | `team` |
| `users` | `id` text primary key, `name` text, `email` text, `phone` text | `user` |
| `memberships` | `team_id` text, `user_id` text, `on_call` boolean, primary key (`team_id`, `user_id`) | `membership` |
| `preferences` | `user_id` text primary key, `body` jsonb ([../rules.md](../rules.md) §5) | `preferences` |
| `service_deps` | `service` text, `depends_on` text, primary key on both | `service_dep` |
| `runbooks` | `id` text primary key, `team_id` text, `title` text, `format` text, `source` text, `canonical` jsonb ([../runbooks.md](../runbooks.md)), `status` text (`draft` \| `active`), `version` int, `updated_at` timestamptz | `runbook` |

`teams`, `users`, `memberships` and `service_deps` are seeded; the seed is published like any change.
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
| `data` | jsonb, not null | |

Rows come from three places: `alerts.changes` (alert state), `alerts.timeline` (notifications), and the
api's own actions (action runs, proposals). After storing a row the api publishes a `timeline` message
on Redis `ui.alerts`.

Example row:

```json
{"id": "01JD3KA...", "alert_id": "01JD3K8...", "at": "2026-11-20T10:15:03.412Z", "actor": "notifier",
 "kind": "notification_sent", "data": {"notification": {"id": "9f2c...", "channel": "sms", "user_id": "marek",
 "status": "sent"}, "why": "rule settlements-workflow-failed, on-call"}}
```

### `action_runs`

| Column | Type | Notes |
|---|---|---|
| `id` | text, primary key | |
| `alert_id`, `runbook_id` | text, not null | |
| `step_index` | int, not null | |
| `action` | text, not null | Action plugin name |
| `params` | jsonb, not null | Placeholders already filled |
| `status` | text, not null | `waiting_approval`, `running`, `succeeded`, `failed`, `rejected` |
| `requested_by`, `approved_by` | text | User ids |
| `result` | jsonb | |
| `started_at`, `finished_at` | timestamptz | |

### `proposals`

| Column | Type | Notes |
|---|---|---|
| `id` | text, primary key | |
| `alert_id` | text | |
| `kind` | text, not null | `threshold`, `template`, `runbook` |
| `body` | jsonb, not null | |
| `author` | text, not null | A user id or `ai-worker` |
| `status` | text, not null | `open`, `accepted`, `rejected` |
| `decided_by` | text | |

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
| `alerts` | `id` text primary key, then every field of the [Alert object](../api.md) as a column (`fields` and `sample_event` jsonb) | Kafka `alerts.changes` (insert or replace); `count` and `last_seen` also from Redis `ui.alerts` `count` messages |
| `notifications` | `id` text primary key, `alert_id` text, `user_id` text, `channel` text, `status` text, `reason` text, `send_at` timestamptz, `sent_at` timestamptz, `error` text | Kafka `alerts.timeline`, the `notification` object in `data` |
| `schema_samples` | `fingerprint` text primary key, `source_id` text, `sample` jsonb, `status` text (`pending` \| `mapped` \| `failed`), `created_at` timestamptz | Kafka `schema.samples` (insert as `pending`); `mapped` when a mapping created for that fingerprint is saved |

The api's `alerts` is a copy: the processor's table is the truth. An ack or resolve is published to
`alerts.changes` and takes effect in every copy, this one included, when that record is consumed.
How a failed mapping attempt reaches `schema_samples.status` is not specified yet.
