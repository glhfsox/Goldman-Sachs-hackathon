# Services, ports, topics, env vars

Shared by every zone. The design itself is in [OVERVIEW.md](../OVERVIEW.md), section "Architecture".
Each service's code lives in `backend/<service>/` with its own dependencies and is stateless:
all state is in Kafka, Redis or Postgres, so every service can run in two or more replicas.
The stack of each service is its owners' choice; the interfaces here are JSON shapes.

## Services

| Service | Code | Port | Consumes | Produces |
|---|---|---|---|---|
| ingest | `backend/ingest/` | 8001 (HTTP) | HTTP webhooks, Kafka `source.*`; Postgres `sources` | Kafka `events.raw` |
| processor | `backend/processor/` | none | Kafka `events.raw`; Postgres `mappings`, `rules`, `service_deps` | Postgres `alerts`, `incidents`, `alert_events`, `schema_samples`; Kafka `alerts.changes`; Redis `ui.alerts` |
| notifier | `backend/notifier/` | none | Kafka `alerts.changes`; Postgres `rules`, `memberships`, `preferences` | Postgres `notifications`, `alert_events`, `alerts` (auto-resolve); channel adapters |
| api | `backend/api/` | 8000 (HTTP + WebSocket) | Postgres (all tables); Redis `ui.alerts` | Postgres (config, ack/resolve, action runs, proposals); Kafka `alerts.changes` |
| ai-worker | `backend/ai-worker/` | none | Postgres `schema_samples`, `runbooks` (drafts); Claude API | Postgres `mappings`, `runbooks` |
| UI | `frontend/` | 3000 | API on 8000 | none |

Interface details: [events.md](events.md), [rules.md](rules.md), [storage.md](storage.md),
[api.md](api.md), [runbooks.md](runbooks.md).

## Infrastructure and mocks (docker compose service names)

| Name | Port | Purpose |
|---|---|---|
| `kafka` | 9092 | One cluster for everything, including the `source.*` topics |
| `redis` | 6379 | Dedup windows and counters (processor only), pub/sub channel `ui.alerts` |
| `postgres` | 5432 | All durable state, see [storage.md](storage.md) |
| `mailhog` | 1025 SMTP, 8025 web | Mock email |
| `mock-gateway` | 8002 | Mock SMS and phone call provider: logs each delivery with a timestamp |
| `mock-target` | 8003 | Target of runbook actions (health check, retry workflow, restart service) |

## Kafka topics

| Topic | Key | Producers | Consumers (group) |
|---|---|---|---|
| `source.*` (e.g. `source.workflows`, `source.trades`) | any | external systems, demo scripts | ingest (`ingest`) |
| `events.raw` | `source_id` | ingest | processor (`processor`) |
| `alerts.changes` | `alert_id` | processor, api, notifier | notifier (`notifier`) |

Delivery is at-least-once everywhere: commit a Kafka offset only after the resulting write is
durable, and make every write idempotent (`event_id`, `change_id`, notification `id`).

## Env vars

| Var | Used by | Example |
|---|---|---|
| `KAFKA_BROKERS` | ingest, processor, notifier, api | `kafka:9092` |
| `DATABASE_URL` | all backend services | `postgres://alerts:alerts@postgres:5432/alerts` |
| `REDIS_URL` | processor, notifier, api | `redis://redis:6379/0` |
| `SMTP_HOST`, `SMTP_PORT` | notifier | `mailhog`, `1025` |
| `SMS_GATEWAY_URL`, `CALL_GATEWAY_URL` | notifier | `http://mock-gateway:8002/sms`, `http://mock-gateway:8002/call` |
| `PUBLIC_BASE_URL` | notifier, api | `http://localhost:3000` (short alert link: `{PUBLIC_BASE_URL}/a/{alert_id}`) |
| `VITE_API_URL` (or the frontend stack's equivalent) | frontend | `http://localhost:8000` |
| `ANTHROPIC_API_KEY` | ai-worker only | optional: without it the system runs on seeded mappings and new schemas stay unmapped |

No auth in the PoC. The UI has a user picker and sends the chosen user as the `X-User` header.
