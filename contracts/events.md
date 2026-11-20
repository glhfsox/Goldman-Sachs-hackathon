# Events: ingest endpoint, Kafka records, live UI channel

Timestamps are ISO 8601 UTC with milliseconds. Ids are opaque strings (ULID or UUID).

## 1. Ingest HTTP endpoint

`POST http://localhost:8001/ingest/{source_id}` with any body (JSON or a plain log line).

| Response | Meaning |
|---|---|
| `202 {"event_id": "..."}` | Kafka has confirmed the event. Never returned earlier. |
| `404` | Unknown or disabled `source_id` |
| `503` | Kafka unavailable; the caller retries |

A Kafka source is a `sources` row of kind `kafka` naming a `source.*` topic; ingest republishes each
message in the same raw record. Ingest never parses, filters or transforms the payload.

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

`payload` is the parsed JSON value when the body is valid JSON, otherwise the body as a string.
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
| `resolved` | notifier (`actor` = `"auto"`) | No recurrence for the rule's window |

Whoever publishes a change writes Postgres first, then Kafka, then Redis `ui.alerts`.
On `acked` the notifier cancels every notification of that alert that is still pending.

## 5. Live UI updates: Redis pub/sub channel `ui.alerts`

The API relays these to browsers over WebSocket (see [api.md](api.md)). Loss is acceptable: the UI refetches.

```json
{"type": "alert", "change": "opened", "alert": {"...": "full Alert object"}}
{"type": "count", "alert_id": "01JD...", "count": 500, "last_seen": "2026-11-20T10:16:01.004Z"}
{"type": "timeline", "alert_id": "01JD..."}
```

- `count`: published by the processor at most once per second per alert.
- `timeline`: published by any service that appends to `alert_events` (notification sent, action run).
