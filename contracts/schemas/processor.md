# Database `processor` (service: processor)

Host `processor-db`. Only the processor connects. Migrations: `backend/processor/migrations/`.
Shared rules: [../storage.md](../storage.md).

## Owned tables

### `alerts`: source of truth for alert state

The [Alert object](../api.md) plus six internal columns that never leave the service.

| Column | Type | Notes |
|---|---|---|
| `id` | text, primary key | |
| `alert_key` | text, not null | Internal. Rule id plus a hash of the rule's `alert_key` values |
| `rule_id` | text, not null | |
| `team_id` | text, not null | |
| `incident_id` | text | References `incidents.id` |
| `status` | text, not null | `open`, `acked`, `resolved` |
| `severity` | text, not null | `info`, `warning`, `error`, `critical`; only ever raised |
| `title`, `body` | text, not null | Rendered from the rule's templates on the first event; capped at 300 and 2,000 characters |
| `fields` | jsonb, not null | Path → value, from the rule's `present.fields`; each value capped at 500 characters |
| `color` | text | |
| `count` | bigint, not null | Flushed from Redis about once per second; never goes down |
| `first_seen`, `last_seen` | timestamptz, not null | Event time (`ts`) of the first and the latest event |
| `first_received_at` | timestamptz, not null | Ingest time of the first event: start of the latency measurement |
| `last_received_at` | timestamptz, not null | Internal. Ingest time of the latest event; window arithmetic uses it |
| `window_s` | int, not null | Internal. The rule's `window_s` when the alert was opened or last reopened by an event; auto-resolve uses it |
| `runbook_id` | text | |
| `acked_by`, `resolved_by` | text | User id, or `auto` |
| `sample_event` | jsonb, not null | Envelope of the first event; a `raw` over 16,384 characters of JSON is replaced by a truncated preview |
| `opened_by_event` | text, not null | Internal. `event_id` that last opened or reopened the alert, so a redelivered event is not counted twice |
| `opened_partition`, `opened_offset` | int, bigint, not null | Internal. Kafka position of that event; every replica that caches the alert in Redis starts from it |

- Unique index on `alert_key` where `status <> 'resolved'`: at most one live alert per key. This index,
  not Redis, decides whether an alert exists.
- Index on (`alert_key`, `last_received_at` desc) for the reopen lookup.
- `status`, `acked_by` and `resolved_by` also change when the processor applies an `acked` or `resolved`
  record that the api published to `alerts.changes`.
- Auto-resolve: every few seconds the processor sets `status = 'resolved'`, `resolved_by = 'auto'` on
  live alerts whose `last_received_at` is older than `window_s` plus 5 s and whose counters in Redis have
  expired, and writes the `resolved` record to `outbox` in the same transaction.

### `incidents`

| Column | Type | Notes |
|---|---|---|
| `id` | text, primary key | |
| `root_alert_id` | text, not null | Earliest alert on the most upstream service |
| `status` | text, not null | `open`, `resolved` |
| `created_at` | timestamptz, not null | |

### `outbox`

Kafka records waiting to be published. Written in the same transaction as the change they describe.

| Column | Type | Notes |
|---|---|---|
| `id` | bigserial, primary key | The relay publishes in `id` order and deletes the row afterwards |
| `topic` | text, not null | `alerts.changes` or `schema.samples` |
| `key` | text, not null | Kafka key: alert id, or fingerprint |
| `payload` | jsonb, not null | The record, exactly as in [../events.md](../events.md) |

### `outbox_dead`

Outbox rows Kafka rejected for good (too large, invalid). They are moved here in the same transaction
that removes them from `outbox`, so one bad record never blocks the records behind it.

| Column | Type | Notes |
|---|---|---|
| `id` | bigint, primary key | The `outbox.id` the row had |
| `topic`, `key` | text, not null | |
| `payload` | jsonb, not null | |
| `error` | text, not null | What Kafka answered |
| `failed_at` | timestamptz, not null, default `now()` | |

### `schema_samples`: schemas already reported

| Column | Type | Notes |
|---|---|---|
| `fingerprint` | text, primary key | Hash of source id, key paths to depth 3 and the values of type-like fields |
| `source_id` | text, not null | |
| `created_at` | timestamptz, not null | |

Insert with `ON CONFLICT DO NOTHING`; only a successful insert puts a `schema.samples` record into
`outbox`, so a burst of a new schema causes one Claude call.

## Copies (fed by Kafka `config.changes`)

| Table | Columns | From kind |
|---|---|---|
| `mappings` | `id` text primary key, `status` text, `priority` int, `body` jsonb ([../rules.md](../rules.md) §3), `updated_at` timestamptz | `mapping` |
| `rules` | `id` text primary key, `team_id` text, `enabled` boolean, `body` jsonb ([../rules.md](../rules.md) §4), `updated_at` timestamptz | `rule` |
| `service_deps` | `service` text, `depends_on` text, primary key on both | `service_dep` |

The processor compiles `mappings` and `rules` into memory and recompiles within 5 s of a change.

## Not here

The timeline (`alert_events`) lives in the api's database: the api derives the processor's entries
(`opened`, `severity_raised`, `reopened`, auto `resolved`) from `alerts.changes`.
Per-event counters and the offsets already counted live in Redis, not in Postgres.
