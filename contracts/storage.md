# Postgres tables

One database, `alerts`. Migrations live in `backend/api/migrations/`; no other service creates or
alters tables. JSON columns hold the objects defined in the other contract files, unchanged.

| Table | Columns (key first) | Written by | Read by |
|---|---|---|---|
| `sources` | `id`, `kind` (`http` \| `kafka`), `config` json, `enabled` | api | ingest |
| `mappings` | `id`, `status`, `origin`, `priority`, `body` json ([rules.md](rules.md) §3), `updated_at` | api, ai-worker | processor, api |
| `schema_samples` | `fingerprint`, `source_id`, `sample` json, `status` (`pending` \| `mapped` \| `failed`), `created_at` | processor (insert), ai-worker (status) | ai-worker, api |
| `rules` | `id`, `team_id`, `enabled`, `body` json ([rules.md](rules.md) §4), `updated_at` | api | processor, notifier |
| `teams` | `id`, `name` | api (seeded) | all |
| `users` | `id`, `name`, `email`, `phone` | api (seeded) | notifier, api |
| `memberships` | `team_id`, `user_id`, `on_call` bool | api | notifier, api |
| `preferences` | `user_id`, `body` json ([rules.md](rules.md) §5) | api | notifier |
| `service_deps` | `service`, `depends_on` | api (seeded) | processor |
| `alerts` | `id`, plus the Alert fields in [api.md](api.md) | processor, api (ack, resolve), notifier (auto-resolve) | all |
| `incidents` | `id`, `root_alert_id`, `status`, `created_at` | processor | api, notifier |
| `alert_events` | `id`, `alert_id`, `at`, `actor`, `kind`, `data` json | every service, append only | api |
| `notifications` | `id`, `alert_id`, `user_id`, `channel`, `reason`, `status`, `send_at`, `attempts`, `rendered` json, `sent_at`, `error` | notifier | api |
| `runbooks` | `id`, `team_id`, `title`, `format`, `source` text, `canonical` json ([runbooks.md](runbooks.md)), `status` (`draft` \| `active`), `version`, `updated_at` | api, ai-worker | api, notifier |
| `action_runs` | `id`, `alert_id`, `runbook_id`, `step_index`, `action`, `params` json, `status`, `requested_by`, `approved_by`, `result` json, `started_at`, `finished_at` | api | api |
| `proposals` | `id`, `alert_id`, `kind` (`threshold` \| `template` \| `runbook`), `body` json, `author`, `status` (`open` \| `accepted` \| `rejected`), `decided_by` | api, ai-worker | api |

## Rules every writer follows

- `rules` and `mappings` are never hard-deleted: set `enabled = false` / `status = 'disabled'` and always
  touch `updated_at`. The processor polls `max(updated_at)` every 2 s to reload.
- `schema_samples.fingerprint` = `source_id` plus the sorted top-level keys of the payload. Insert with
  `ON CONFLICT DO NOTHING`, so a burst of a new schema causes one Claude call.
- `alert_events` is the audit log and the alert timeline: what happened, who did it (`actor` = a user id,
  a service name or `auto`), when, with what result.
  `kind`: `opened`, `severity_raised`, `reopened`, `acked`, `resolved`, `notification_sent`,
  `notification_failed`, `notification_suppressed`, `action_requested`, `action_approved`,
  `action_rejected`, `action_finished`, `proposal_created`, `proposal_decided`.
- `notifications.id` is the idempotency key: a hash of alert id, user id, channel and the triggering change.
  `status`: `pending`, `sent`, `failed` (will retry), `dead` (gave up), `cancelled` (acked first),
  `suppressed` (dedup, limit or quiet hours; `reason` says which). Workers claim rows with
  `FOR UPDATE SKIP LOCKED` where `send_at <= now()`.
- `action_runs.status`: `waiting_approval`, `running`, `succeeded`, `failed`, `rejected`.
- Per-event counter updates go to Redis; the processor flushes `count` and `last_seen` to `alerts`
  about once per second per alert.

Example `alert_events` row:

```json
{"id": "01JD3KA...", "alert_id": "01JD3K8...", "at": "2026-11-20T10:15:03.412Z", "actor": "notifier",
 "kind": "notification_sent", "data": {"notification_id": "9f2c...", "channel": "sms", "user_id": "marek",
 "why": "rule settlements-workflow-failed, on-call"}}
```
