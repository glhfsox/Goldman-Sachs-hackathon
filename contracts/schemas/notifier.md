# Database `notifier` (service: notifier)

Host `notifier-db`. Only the notifier connects. Migrations: `backend/notifier/migrations/`.
Shared rules: [../storage.md](../storage.md).

## Owned tables

### `notifications`: delivery outbox

| Column | Type | Notes |
|---|---|---|
| `id` | text, primary key | Idempotency key: hash of alert id, user id, channel and the triggering change |
| `alert_id` | text, not null | |
| `user_id` | text, not null | |
| `channel` | text, not null | `email`, `sms`, `call`, `ui` |
| `reason` | text | Why it was sent, or why it was suppressed |
| `status` | text, not null | `pending`, `sent`, `failed` (will retry), `dead` (gave up), `cancelled` (acked first), `suppressed` (dedup, limit or quiet hours) |
| `send_at` | timestamptz, not null | Now, the next escalation step, or the next digest |
| `attempts` | int, not null | |
| `rendered` | jsonb | The message as rendered for the channel, runbook included |
| `sent_at` | timestamptz | |
| `error` | text | Last delivery error |

- Index on (`status`, `send_at`). Workers claim rows with `FOR UPDATE SKIP LOCKED` where `send_at <= now()`.
- Every status change is published to Kafka `alerts.timeline` ([../events.md](../events.md) §7), which is
  how the api learns about deliveries.

## Copies

### `alerts`: from Kafka `alerts.changes`

| Column | Type | Notes |
|---|---|---|
| `id` | text, primary key | |
| `status` | text, not null | `acked` cancels pending notifications; `resolved` stops escalation |
| `body` | jsonb, not null | The latest [Alert object](../api.md) received |
| `updated_at` | timestamptz, not null | `at` of the latest change applied |

`count` and `last_seen` here are only as fresh as the last change record. The notifier does not decide
auto-resolve: the processor, which owns alert state, publishes it.

### From Kafka `config.changes`

| Table | Columns | From kind |
|---|---|---|
| `rules` | `id` text primary key, `team_id` text, `enabled` boolean, `body` jsonb (routing and presentation, [../rules.md](../rules.md) §4), `updated_at` timestamptz | `rule` |
| `teams` | `id` text primary key, `name` text | `team` |
| `users` | `id` text primary key, `name` text, `email` text, `phone` text | `user` |
| `memberships` | `team_id` text, `user_id` text, `on_call` boolean, primary key (`team_id`, `user_id`) | `membership` |
| `preferences` | `user_id` text primary key, `body` jsonb ([../rules.md](../rules.md) §5) | `preferences` |
| `runbooks` | `id` text primary key, `team_id` text, `title` text, `canonical` jsonb ([../runbooks.md](../runbooks.md)), `status` text, `version` int | `runbook` (only `active` ones are attached to messages) |

Recipients are resolved from these copies at the moment a change arrives, so a preference or on-call
change applies to the next notification.
