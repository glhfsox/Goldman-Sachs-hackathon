# Sources: what the simulated (and real) sources send to ingest

Owner: data zone. Consumer: ingest (`backend/ingest/`); the payload catalog in §4 is also what the processor's
seed mappings and the example rules ([rules.md](../rules.md) §4) read.

The data zone simulates third-party systems that push events to us. A simulated source behaves exactly like a
real one: same transport, same rules, no simulator-only fields. So ingest is built and tested against this
file, and a real system replaces a simulated one by changing a URL or a broker address, never ingest's code.

The story the simulated data tells (the fictional bank, its systems and the demo timeline) is in
[data/README.md](../../data/README.md).

Ingest's side of the HTTP endpoint (responses, `events.raw` record) is in [events.md](../events.md) §1–§2;
this file does not repeat it.

## 1. Source registry

Ingest learns sources from Kafka `config.changes` (kind `source`, [events.md](../events.md) §6) into its
`sources` table ([schemas/ingest.md](../schemas/ingest.md)). Until the api publishes them, seed these rows:

| `id` | `kind` | `config` | Transport | Payload (§4) |
|---|---|---|---|---|
| `workflows-kafka` | `kafka` | `{"topic": "source.workflows"}` | Kafka, sources cluster | Workflow event |
| `trades-kafka` | `kafka` | `{"topic": "source.trades"}` | Kafka, sources cluster | Business event, `{meta, payload}` envelope |
| `alertmanager-http` | `http` | `{}` | HTTP webhook | Metric alert, Alertmanager-like |
| `pricing-logs-http` | `http` | `{}` | HTTP webhook | Log record (JSON) or raw log line (text) |

```json
[
  {"id": "workflows-kafka",   "kind": "kafka", "config": {"topic": "source.workflows"}, "enabled": true},
  {"id": "trades-kafka",      "kind": "kafka", "config": {"topic": "source.trades"},    "enabled": true},
  {"id": "alertmanager-http", "kind": "http",  "config": {},                            "enabled": true},
  {"id": "pricing-logs-http", "kind": "http",  "config": {},                            "enabled": true}
]
```

- `id`: lowercase `[a-z0-9-]`, ends in `-kafka` or `-http` by convention only; ingest must not parse it.
- A source of kind `http` accepts requests only on its own id; a `kafka` source is consumed only from its own topic.
- Adding a source (real or simulated) = one new row (later: `POST /sources` on the api). No code change.

## 2. HTTP sources (webhook push)

```
POST {INGEST_URL}/ingest/{source_id}
Content-Type: application/json            (JSON payloads)
Content-Type: text/plain; charset=utf-8   (raw log lines)
```

| Rule | Value |
|---|---|
| Events per request | Exactly one. No arrays, no NDJSON. The body is the payload. |
| Body | UTF-8. JSON object, or one line of text without the trailing newline |
| Body size | ≤ 64 KiB (typical 0.2–2 KiB) |
| Headers | Only `Content-Type` is meaningful. Any other header may be present and is ignored |
| Auth | None in the PoC |
| Connection | HTTP/1.1 keep-alive, many requests in parallel |

Sender behaviour (what ingest can expect):

| Ingest answers | Sender does |
|---|---|
| `202` | Done |
| `404` | Gives up on this event and logs it (no retry) |
| `503`, timeout (5 s), connection error | Retries with exponential backoff, 0.2 s → 5 s, up to 6 attempts, then drops and logs |
| any other `4xx` / `5xx` | Gives up and logs |

A retry after a lost `202` sends the same body again, so ingest may see duplicates. Ingest does not
deduplicate; the processor's alert keys absorb them.

## 3. Kafka sources (sources cluster)

| Rule | Value |
|---|---|
| Cluster | `kafka-sources`: `localhost:9094` from the host, `kafka-sources:9092` in compose (`SOURCE_KAFKA_BROKERS`) |
| Topics | `source.workflows` (3 partitions), `source.trades` (3 partitions). Created by the data zone's broker setup; ingest never creates topics |
| Value | UTF-8 bytes of one JSON object = one event (rare invalid values on purpose, §4.5). Never null (no tombstones) |
| Key | Set by the producer, for ordering only: `workflowId` on `source.workflows`, `payload.tradeId` on `source.trades`. Ingest must not rely on it |
| Headers | None required; any present are ignored |
| Compression | Producer may use `lz4`; the client library decompresses transparently |
| Delivery | At-least-once (`acks=all`, retries): duplicates possible, ordering only per key |
| Consumer | Group `ingest`. Commit an offset only after `events.raw` has confirmed the republished event |

A value that is not valid JSON is handled like an HTTP text body: it becomes a string `payload`.

## 4. Payload catalog

Payloads deliberately differ in envelope, severity scale and timestamp format: that is what mappings
normalise (AC-2.2). Ingest treats all of them as opaque. Fields marked *opt* can be missing.

### 4.1 Workflow event (`workflows-kafka`)

| Field | Type | Values |
|---|---|---|
| `type` | string | `WORKFLOW_FAILED` (most), `WORKFLOW_STEP_SLOW`, `WORKFLOW_RECOVERED` |
| `workflowId` | string | `WF-YYYYMMDD-NNNN` |
| `workflowName` | string | `order-settlement`, `trade-booking`, `payment-release`, `eod-reconciliation` |
| `step` | string | e.g. `validate`, `enrich`, `settle`, `confirm` |
| `severity` | string | `critical`, `error`, `warning`, `info` |
| `environment` | string | `prod`, `staging`, `dev` |
| `timestamp` | string | ISO 8601 UTC, seconds |
| `error` *opt* | object | `{code, message}`; absent on `WORKFLOW_RECOVERED`. `code`: `TIMEOUT`, `REJECTED`, `VALIDATION_ERROR`, `DOWNSTREAM_5XX` |

```json
{"type": "WORKFLOW_FAILED", "workflowId": "WF-20261120-0042", "workflowName": "order-settlement",
 "step": "settle", "severity": "critical", "environment": "prod", "timestamp": "2026-11-20T10:15:01Z",
 "error": {"code": "TIMEOUT", "message": "Counterparty service did not respond in 5000 ms"}}
```

### 4.2 Business event (`trades-kafka`)

| Field | Type | Values |
|---|---|---|
| `meta.schema` | string | `trade.rejected.v1` (most), `trade.amended.v1`, `trade.booked.v1` |
| `meta.producer` | string | `trade-gateway`, `booking-service` |
| `meta.ts` | integer | epoch milliseconds |
| `payload.tradeId` | string | `T-NNNNN` |
| `payload.desk` | string | `rates`, `fx`, `credit`, `equities` |
| `payload.notional` | integer | 10 000 – 500 000 000; about 1 in 5 rates trades above 10 000 000 |
| `payload.currency` | string | ISO 4217: `EUR`, `USD`, `GBP`, `JPY`, `CHF` |
| `payload.reason` *opt* | string | only on `trade.rejected.*`: `LIMIT_BREACH`, `SANCTIONS`, `BAD_CPTY`, `STALE_PRICE` |

No severity field: the mapping sets one.

```json
{"meta": {"schema": "trade.rejected.v1", "producer": "trade-gateway", "ts": 1795169702000},
 "payload": {"tradeId": "T-88121", "desk": "rates", "notional": 25000000, "currency": "EUR", "reason": "LIMIT_BREACH"}}
```

### 4.3 Metric alert (`alertmanager-http`)

One alert per request, in the flat shape of the brief (not Alertmanager's grouped `{"alerts": [...]}` body,
which would be one event holding many alerts).

| Field | Type | Values |
|---|---|---|
| `alertname` | string | `HighErrorRate`, `HighLatency`, `PodCrashLooping`, `DiskFillingUp` |
| `labels.service` | string | `payments-api`, `pricing-engine`, `counterparty-service`, `trade-gateway` |
| `labels.region` | string | `eu-west`, `us-east`, `ap-south` |
| `labels.severity` | string | `critical`, `warning`, `info` |
| `labels.*` *opt* | string | extra labels may appear, e.g. `instance`, `pod` |
| `annotations.summary` | string | human-readable, e.g. `Error rate 12% over the last 5 minutes` |
| `startsAt` | string | ISO 8601 UTC, milliseconds |
| `endsAt` *opt* | string | ISO 8601 UTC; set when `status` = `resolved` |
| `status` | string | `firing`, later `resolved` for the same `alertname` + `labels` |

```json
{"alertname": "HighErrorRate",
 "labels": {"service": "payments-api", "region": "eu-west", "severity": "critical"},
 "annotations": {"summary": "Error rate 12% over the last 5 minutes"},
 "startsAt": "2026-11-20T10:14:30.000Z", "status": "firing"}
```

### 4.4 Log record (`pricing-logs-http`)

A log shipper's parsed record, JSON (`Content-Type: application/json`):

| Field | Type | Values |
|---|---|---|
| `ts` | string | ISO 8601 UTC, milliseconds |
| `level` | string | `DEBUG`, `INFO`, `WARN`, `ERROR`, `FATAL` |
| `service` | string | `pricing-engine-1` … `pricing-engine-4` |
| `instrument` | string | e.g. `EURUSD`, `USDJPY`, `UST10Y`, `BUND10Y` |
| `latency_ms` *opt* | integer | present on latency messages |
| `msg` | string | free text |

```json
{"ts": "2026-11-20T10:15:01.204Z", "level": "ERROR", "service": "pricing-engine-3", "instrument": "EURUSD",
 "latency_ms": 1840, "msg": "Price calculation exceeded 1000 ms: cache miss"}
```

The same source also sends a small share of raw lines (`Content-Type: text/plain`). They reach the
processor as a string payload and stay unmapped: this is the deliberate "unknown shape" case.

```
2026-11-20T10:15:01.204Z ERROR pricing-engine-3 instrument=EURUSD latency_ms=1840 cache miss
```

### 4.5 Deliberate edge cases

Sent on purpose in demo and test runs, in small numbers; ingest must not crash on any of them.

| Case | Where | Expected from ingest |
|---|---|---|
| Unknown `source_id` | HTTP | `404` |
| Body is invalid JSON with `Content-Type: application/json` | HTTP, Kafka | String `payload`, `202` |
| New, never-seen schema on an existing source | any | Passed through like any other |
| Non-ASCII text (`ü`, `€`, `日本`) | any | Passed through unchanged |
| Same event repeated (burst of 500 per minute) | any | Every copy accepted |

## 5. Volumes

| Profile | Rate | Shape |
|---|---|---|
| `demo` | ~5–20 msg/s total | Scripted story following the brief's example rules |
| `steady` | ~50 msg/s total | Poisson arrivals, mixed severities |
| `burst` | +500 events/min on one key | Same error repeated (dedup test, AC-4) |
| `load` | ≥ 1000 msg/s, mostly Kafka | For AC-9.2 throughput and latency |

## 6. Running against it

| Var | Host | Inside compose |
|---|---|---|
| `INGEST_URL` (used by HTTP senders) | `http://localhost:8001` | `http://ingest:8001` |
| `SOURCE_KAFKA_BROKERS` | `localhost:9094` | `kafka-sources:9092` |

The `kafka-sources` broker and its topics come from the data zone (`data/`), so ingest does not run its own.
Until the simulator exists, a quick manual test:

```bash
curl -i -X POST localhost:8001/ingest/alertmanager-http -H 'Content-Type: application/json' \
  -d '{"alertname":"HighErrorRate","labels":{"service":"payments-api","region":"eu-west","severity":"critical"},"annotations":{"summary":"Error rate 12%"},"startsAt":"2026-11-20T10:14:30.000Z","status":"firing"}'
curl -i -X POST localhost:8001/ingest/pricing-logs-http -H 'Content-Type: text/plain' \
  -d '2026-11-20T10:15:01.204Z ERROR pricing-engine-3 instrument=EURUSD latency_ms=1840 cache miss'
```
