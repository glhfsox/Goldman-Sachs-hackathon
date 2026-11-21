# Events: ingest endpoint, Kafka records, live UI channel

Timestamps are ISO 8601 UTC with milliseconds. Ids are opaque strings (ULID or UUID).

## 1. Ingest HTTP endpoint

`POST http://localhost:8001/ingest/{source_id}` with any body (JSON or a plain log line).

| Response | Meaning |
|---|---|
| `202 {"event_id": "..."}` | Kafka has confirmed the event. Never returned earlier. |
| `404` | Unknown or disabled `source_id`, or a `source_id` of a `kafka` source (those are read from their topic only) |
| `413` | Body larger than 64 KiB (65536 bytes) |
| `503` | Kafka did not confirm within about 4 s; the caller retries |

The body is the event: the `Content-Type` header is ignored. Errors are `application/problem+json`.

OpenAPI spec: `http://localhost:8001/openapi/v1.json`; Swagger UI: `http://localhost:8001/swagger` (on with `ENABLE_SWAGGER=true`). A Postman collection for this endpoint is in `backend/ingest/postman/`.

A Kafka source is a `sources` row of kind `kafka` naming a `source.*` topic on the separate sources
cluster (`SOURCE_KAFKA_BROKERS`), e.g. `{"id": "workflows-kafka", "kind": "kafka", "config": {"topic": "source.workflows"}}`.
Ingest consumes it and republishes each message to `events.raw` on the internal cluster in the same raw record. Ingest never parses, filters or transforms the payload.

What the sources send (source ids, `source.*` topics and records, HTTP sender behaviour, payload catalog):
[data/sources.md](data/sources.md).

## 2. Raw event: topic `events.raw`

```json
{
  "event_id": "01JD3K8Q2V7M9X4T6B0N5R1WEC",
  "source_id": "workflows-kafka",
  "received_at": "2026-11-20T10:15:02.123Z",
  "payload": {"type": "WORKFLOW_FAILED", "workflowId": "WF-20261120-0042", "workflowName": "order-settlement",
              "step": "settle", "severity": "critical", "environment": "prod", "timestamp": "2026-11-20T10:15:01Z",
              "error": {"code": "TIMEOUT", "message": "Counterparty service did not respond in 5000 ms"}}
}
```

`event_id` is a UUID string, unique per record, and the idempotency key for consumers. HTTP sources: UUID v7, new for
every request (a sender retry gets a new id).
Kafka sources: UUID v5 of `source_id`, topic, partition and offset (namespace `6f1c2b7e-3a45-4d8e-9b0a-5c7e2d4f8a11`, name `<source_id>/<topic>/<partition>/<offset>`), so a message read again after a crash gets the same `event_id`.
Ingest reads a Kafka source's topic from its beginning on the first start and commits an offset only after Kafka confirmed the republished record.

`payload` is the parsed JSON value when the body is valid JSON, otherwise the body as a string (bytes that are not valid UTF-8 become U+FFFD).
The record key is `event_id`, so the records of one source (even a single busy one) spread over all partitions and every Processor worker gets a share. There is no order between the records of one source; consumers must not rely on it (the Processor does not). Until 21-11 the key was `source_id`.
`received_at` is the start of the latency measurement (ingest → delivery).

## 3. Envelope: built by the processor, never dropped

```json
{
  "event_id": "01JD3K8Q2V7M9X4T6B0N5R1WEC",
  "source": "workflows-kafka",
  "type": "WORKFLOW_FAILED",
  "severity": "critical",
  "env": "prod",
  "service": "order-settlement",
  "ts": "2026-11-20T10:15:01.000Z",
  "dedup_hints": ["order-settlement", "TIMEOUT"],
  "mapped": true,
  "mapping_id": "workflow-failed",
  "received_at": "2026-11-20T10:15:02.123Z",
  "raw": {"...": "the payload, whole"}
}
```

- Severity scale, low to high: `info`, `warning`, `error`, `critical`.
- An event with no matching mapping still gets an envelope: `mapped: false`, `type: "unmapped"`,
  `severity: "warning"`, `env: "unknown"`, `service` = `source`, `ts` = `received_at`, `dedup_hints: []`.
  Rules can still match it through `raw.*`, and the UI shows it as unmapped.
- The envelope is not a Kafka record. It is stored on the alert as `sample_event` and is what rule
  conditions, templates and runbook placeholders read (path rules in [rules.md](rules.md)).
- Rules and templates always see the whole payload. The copy stored on the alert is capped so that no
  change record gets too large for Kafka: a `raw` whose JSON is longer than 16,384 characters is stored as
  `{"_truncated": true, "_chars": <length>, "_preview": "<first 2,000 characters>…"}`.
- Input the processor repairs instead of dropping: NUL characters and lone surrogates in payload strings
  become U+FFFD, and `NaN` / `Infinity` become `null`. It skips (and logs) only what cannot be stored:
  an `event_id` or `source_id` with such characters, a `received_at` outside years 1 to 9999, or a payload
  nested deeper than 64 levels.

## 4. Alert change: topic `alerts.changes`

Only state changes, never one record per event.

```json
{
  "change_id": "01JD3K8Q9A...",
  "change": "opened",
  "at": "2026-11-20T10:15:02.180Z",
  "actor": "processor",
  "alert": {"...": "full Alert object, see api.md"}
}
```

| `change` | Published by | When |
|---|---|---|
| `opened` | processor | First event for an alert key |
| `severity_raised` | processor | A later event has a higher severity |
| `count_threshold` | processor | `count` crosses 10, 100, 1000 |
| `reopened` | processor | An event arrives for a resolved alert inside its reopen window |
| `acked`, `resolved` | api (`actor` = user id) | A person acts in the UI |
| `resolved` | processor (`actor` = `"auto"`) | No recurrence for the rule's window |

Whoever publishes a change writes its own database first, with the record in an `outbox` table in the
same transaction ([storage.md](storage.md)), then Kafka, then Redis `ui.alerts`.

Consumers: the notifier (policy and delivery; on `acked` it cancels every notification of that alert
that is still pending), the api (its read copy of alerts and the timeline) and the processor, which owns
alert state and applies the `acked` and `resolved` records the api published. Every consumer ignores
changes it published itself and applies the rest idempotently by `change_id`.

### What the notifier gets from the processor

Every record the processor publishes carries these fields, always present and never null unless noted.
The notifier builds its messages from them and needs nothing else from the processor.

| What | Field | Notes |
|---|---|---|
| Title | `alert.title` | Text, at most 300 characters, never empty: the rule's title template, else the rule's name, else `type · service` |
| Message | `alert.body` | Text, at most 2,000 characters; empty string when the rule has no body template |
| Severity | `alert.severity` | `info`, `warning`, `error` or `critical`: the highest seen on this alert so far |
| Time of this change | `at` | When it happened, ingest clock (for an auto `resolved`: the processor's clock). Use it for ordering and for "sent N seconds after" |
| Time of the problem | `alert.first_seen`, `alert.last_seen` | Event time of the first and of the latest event, as the source reported it |
| Which alert | `alert.id`, `change_id`, `change` | `alert.id` for the short link and for cancelling on ack; `change_id` to ignore a record delivered twice |
| Who it is for | `alert.rule_id`, `alert.team_id` | The notifier resolves recipients and channels from its own copy of that rule |
| Extras for the message | `alert.count`, `alert.fields`, `alert.color`, `alert.runbook_id` | `fields` is path → value, each value at most 500 characters; `color` and `runbook_id` may be null |

All times are ISO 8601 UTC with milliseconds. Example of what the notifier reads:

```json
{"change_id": "7c1e...", "change": "opened", "at": "2026-11-20T10:15:02.123Z", "actor": "processor",
 "alert": {"id": "5b0f...", "rule_id": "settlements-workflow-failed", "team_id": "settlements",
           "title": "order-settlement failed at step settle",
           "body": "Counterparty service did not respond in 5000 ms",
           "severity": "critical", "first_seen": "2026-11-20T10:15:01.000Z", "last_seen": "2026-11-20T10:15:01.000Z",
           "count": 1, "fields": {"step": "settle", "error.code": "TIMEOUT"}, "color": "red",
           "runbook_id": "settlement-timeout", "...": "the other Alert fields, see api.md"}}
```

## 5. Live UI updates: Redis pub/sub channel `ui.alerts`

The API relays these to browsers over WebSocket (see [api.md](api.md)). Loss is acceptable: the UI refetches.

```json
{"type": "alert", "change": "opened", "alert": {"...": "full Alert object"}}
{"type": "count", "alert_id": "01JD...", "count": 500, "last_seen": "2026-11-20T10:16:01.004Z"}
{"type": "timeline", "alert_id": "01JD..."}
```

- `count`: published by the processor at most once per second per alert.
- `timeline`: published by the api after it stores a timeline row (from `alerts.changes`,
  `alerts.timeline` or its own action runs).

## 6. Configuration: topic `config.changes`

The api owns all configuration and publishes every insert, update and soft-delete here. The topic is
compacted and keyed by `<kind>:<id>`, so a service that starts with an empty database reads it from the
beginning and ends up with the current state. Consumers upsert by id and ignore a record older than
the row they hold.

```json
{
  "kind": "rule",
  "id": "settlements-workflow-failed",
  "updated_at": "2026-11-20T09:30:00.000Z",
  "deleted": false,
  "body": {"id": "settlements-workflow-failed", "team_id": "settlements", "enabled": true, "...": "the Rule, see rules.md"}
}
```

| `kind` | `id` | `body` | Copied by |
|---|---|---|---|
| `source` | source id | `{id, kind, config, enabled}` | ingest |
| `mapping` | mapping id | Mapping ([rules.md](rules.md) §3) | processor |
| `rule` | rule id | Rule ([rules.md](rules.md) §4) | processor, notifier |
| `service_dep` | `<service>:<depends_on>` | `{service, depends_on}` | processor |
| `team` | team id | `{id, name}` | notifier |
| `user` | user id | `{id, name, email, phone}` | notifier |
| `membership` | `<team_id>:<user_id>` | `{team_id, user_id, on_call}` | notifier |
| `preferences` | user id | Preferences ([rules.md](rules.md) §5) | notifier |
| `runbook` | runbook id | `{id, team_id, title, format, source, canonical, status, version}` | notifier (`active`), ai-worker (`draft`, to convert) |

`deleted: true` (with `body: null`) is used only for rows that really disappear (`membership`,
`service_dep`); everything else is disabled through its `enabled` or `status` field.

## 7. Timeline entries from other services: topic `alerts.timeline`

What the notifier did for an alert. The api stores each record as an `alert_events` row (`entry_id`
becomes the row id) and keeps the `notification` object as its copy of that notification.

```json
{
  "entry_id": "01JD3KA...",
  "alert_id": "01JD3K8...",
  "at": "2026-11-20T10:15:03.412Z",
  "actor": "notifier",
  "kind": "notification_sent",
  "data": {"notification": {"id": "9f2c...", "user_id": "marek", "channel": "sms", "status": "sent",
                            "reason": "rule settlements-workflow-failed, on-call",
                            "send_at": "2026-11-20T10:15:02.200Z", "sent_at": "2026-11-20T10:15:03.410Z", "error": null}}
}
```

`kind`: `notification_sent`, `notification_failed`, `notification_suppressed`. Alert state changes are
not repeated here: the api derives those timeline rows from `alerts.changes`.

## 8. New schemas: topic `schema.samples`

Published by the processor once per fingerprint, when an event matches no mapping.

```json
{
  "fingerprint": "9f2c41d0...",
  "source_id": "trades-kafka",
  "seen_at": "2026-11-20T10:15:02.123Z",
  "sample": {"meta": {"schema": "trade.rejected.v1", "producer": "trade-gateway", "ts": 1795169702000},
             "payload": {"tradeId": "T-88121", "desk": "rates", "notional": 25000000, "currency": "EUR", "reason": "LIMIT_BREACH"}}
}
```

The ai-worker proposes a mapping and saves it through the api (`PUT /mappings/{id}`, [api.md](api.md));
the api lists the samples under `GET /schema-samples`. `fingerprint` is a hash of the source id, the
payload's key paths to depth 3 and the values of type-like fields (`type`, `schema`, `event`, `eventType`,
`kind`, `alertname`), so two business events in the same `{meta, payload}` envelope are different schemas.
