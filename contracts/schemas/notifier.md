# Database `notifier` (service: notifier)

Host `notifier-db`. Only the notifier connects. Migrations: EF Core, in
`backend/notifier/src/Notifier.Infrastructure/Migrations/` (applied by the worker on start).
Shared rules: [../storage.md](../storage.md).

## Owned tables

### `notifications`: delivery outbox

| Column | Type | Notes |
|---|---|---|
| `id` | text, primary key | Idempotency key: hash of alert id, user id, channel and the triggering change |
| `alert_id` | text, not null | |
| `incident_id` | text | From the alert, when it belongs to an incident |
| `group_key` | text, not null | Incident id, else the triggering event id, else the alert id: "one message per person per incident" (AC-5.3) |
| `user_id` | text, not null | |
| `channel` | text, not null | `email`, `sms`, `call`, `ui`; changes when quiet hours downgrade it |
| `change_id` | text, not null | The `alerts.changes` record that planned it |
| `escalation_step` | int, not null | 0 = at once, 1 = after the push delay without ack, 2 = call to the on-call person |
| `reason` | text, not null | Why it was sent, or why it was suppressed or cancelled |
| `status` | text, not null | `pending`, `sent`, `failed` (will retry), `dead` (gave up), `cancelled` (acked first), `suppressed` (dedup, limit or quiet hours) |
| `send_at` | timestamptz, not null | Now, or the next escalation step; moved forward while a worker holds the row |
| `attempts` | int, not null | |
| `recipient` | jsonb, not null | Contact and preference snapshot taken when the row was planned |
| `content` | jsonb, not null | Title, body, fields, severity and runbook steps (placeholders filled) |
| `rendered` | text | The message as sent on its channel |
| `provider_id` | text | Id returned by the channel provider |
| `sent_at` | timestamptz | |
| `error` | text | Last delivery error |
| `created_at`, `updated_at` | timestamptz, not null | |

- Indexes: partial on (`send_at`) where `status IN ('pending','failed')` (the claim), (`alert_id`), (`user_id`, `group_key`). Workers claim rows with
  `FOR UPDATE SKIP LOCKED` where `send_at <= now()`.
- Every status change is published to Kafka `alerts.timeline` ([../events.md](../events.md) §7), which is
  how the api learns about deliveries.

### `outbox`: records waiting to be published

| Column | Type | Notes |
|---|---|---|
| `id` | uuid, primary key | |
| `sequence` | bigint identity | Publish order |
| `topic` | text, not null | `alerts.timeline` |
| `key` | text, not null | Alert id |
| `payload` | jsonb, not null | The record, as in [../events.md](../events.md) |
| `created_at`, `processed_at`, `locked_until` | timestamptz | Partial index on `sequence` where `processed_at IS NULL` |
| `attempts` | int, not null | |
| `error` | text | |

### `processed_changes`

`change_id` text primary key, `processed_at` timestamptz. A redelivered `alerts.changes` record is skipped.

### `rate_counters`

`user_id` text primary key, `window_start` timestamptz, `push_count` int, `folded_count` int: SMS and calls
sent this hour, and how many were folded into the next "and N more" (`max_push_per_hour`).

## Copies

### `alerts`: from Kafka `alerts.changes`

| Column | Type | Notes |
|---|---|---|
| `id` | text, primary key | |
| `rule_id`, `incident_id` | text | From the Alert object |
| `status` | text, not null | `acked` cancels pending notifications; `resolved` stops escalation |
| `severity` | text, not null | |
| `body` | jsonb, not null | The latest [Alert object](../api.md) received |
| `updated_at` | timestamptz, not null | `at` of the latest change applied; an older change is ignored |

`count` and `last_seen` here are only as fresh as the last change record. The notifier does not decide
auto-resolve: the processor, which owns alert state, publishes it.

### From Kafka `config.changes`

Every copy also has `updated_at` timestamptz; a record older than the row is ignored.

| Table | Columns | From kind |
|---|---|---|
| `rules` | `id` text primary key, `team_id` text, `enabled` boolean, `body` jsonb (routing and presentation, [../rules.md](../rules.md) §4) | `rule` |
| `teams` | `id` text primary key, `name` text | `team` |
| `users` | `id` text primary key, `name` text, `email` text, `phone` text | `user` |
| `memberships` | `team_id` text, `user_id` text, `on_call` boolean, primary key (`team_id`, `user_id`), index on `user_id` | `membership` (`deleted: true` removes the row) |
| `preferences` | `user_id` text primary key, `body` jsonb ([../rules.md](../rules.md) §5) | `preferences` |
| `runbooks` | `id` text primary key, `team_id` text, `title` text, `canonical` jsonb ([../runbooks.md](../runbooks.md)), `status` text, `version` int | `runbook` (only `active` ones are attached to messages) |

Recipients are resolved from these copies at the moment a change arrives, so a preference or on-call
change applies to the next notification.
