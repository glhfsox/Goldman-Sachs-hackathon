# Services, ports, topics, env vars

Shared by every zone. The design itself is in [OVERVIEW.md](../OVERVIEW.md), section "Architecture".
Each service's code lives in `backend/<service>/` with its own dependencies and is stateless:
all state is in Kafka, Redis or the service's own Postgres, so every service can run in two or more replicas.
One internal Kafka cluster and one Redis are shared; every service has its own database and no service
reads another's tables ([storage.md](storage.md)).
The stack of each service is its owners' choice; the interfaces here are JSON shapes.

## Services

| Service | Code | Port | Consumes | Produces |
|---|---|---|---|---|
| ingest | `backend/ingest/` | 8001 (HTTP) | HTTP webhooks; `source.*` topics on the sources Kafka cluster; Kafka `config.changes` (sources) | Kafka `events.raw` |
| processor (owner: glhfsox) | `backend/processor/` | none | Kafka `events.raw`, `config.changes` (mappings, rules, service map), `alerts.changes` (acked, resolved) | Kafka `alerts.changes`, `schema.samples`; Redis `ui.alerts` |
| notifier | `backend/notifier/` | none | Kafka `alerts.changes`, `config.changes` (rules, teams, users, memberships, preferences, runbooks) | Channel adapters; Kafka `alerts.timeline` |
| api | `backend/api/` | 8000 (HTTP + WebSocket) | Kafka `alerts.changes`, `alerts.timeline`, `schema.samples`; Redis `ui.alerts` | Kafka `config.changes`, `alerts.changes` (acked, resolved); REST and WebSocket |
| ai-worker (owners: glhfsox, spdsslg) | `backend/ai-worker/` | none | Kafka `schema.samples`, `config.changes` (runbook drafts, mappings, rules), `alerts.changes` (every record); the api's `GET /alerts/{id}`, `GET /runs` (pending risk checks, finished runs); Claude API; TypeSafe API (Jev) | REST calls to the api: mappings, canonical runbooks, run risk checks (`POST /runs/{id}/risk-check`), proposals (`runbook`, `rule_severity`, `runbook_start`) |
| UI | `frontend/` | 3000 | API on 8000 | none |

Interface details: [events.md](events.md), [rules.md](rules.md), [storage.md](storage.md),
[api.md](api.md), [runbooks.md](runbooks.md).

## Infrastructure and mocks (docker compose service names)

| Name | Port | Purpose |
|---|---|---|
| `kafka` | 9092 | Internal cluster shared by all backend services: `events.raw`, `alerts.changes`, `config.changes`, `alerts.timeline`, `schema.samples`. Only our services connect to it |
| `kafka-sources` | 9094 on the host, `kafka-sources:9092` inside compose | Separate cluster that third-party systems publish to (`source.*` topics). Only ingest reads it |
| `redis` | 6379 | Alert counters (processor only), pub/sub channel `ui.alerts` |
| `ingest-db`, `processor-db`, `notifier-db`, `api-db`, `ai-worker-db` | 5432 inside compose; 5433 to 5437 on the host, in that order | One Postgres per service, see [storage.md](storage.md) |
| `mailhog` | 1025 SMTP, 8025 web | Mock email |
| `mock-gateway` | 8002 | Mock SMS and phone call provider: logs each delivery with a timestamp |
| `mock-target` | 8003 | Target of runbook actions (`backend/mock-target/`): answers any path, logs each call with its `Idempotency-Key` (`GET /_calls`), replays the first answer for a repeated key, `POST /_fail` makes calls fail. Every seeded action target points here |

## Kafka topics

| Topic | Cluster | Key | Producers | Consumers (group) |
|---|---|---|---|---|
| `source.*` (e.g. `source.workflows`, `source.trades`) | `kafka-sources` | any | third-party systems, demo scripts | ingest (`ingest`) |
| `events.raw` | `kafka` | `source_id` | ingest | processor (`processor`) |
| `alerts.changes` | `kafka` | `alert_id` | processor, api | notifier (`notifier`), api (`api`), processor (`processor-changes`), ai-worker (`ai-worker`) |
| `config.changes` (compacted) | `kafka` | `<kind>:<id>` | api | ingest, processor, notifier (one group each); ai-worker (no group: it reads the topic from the beginning at every start) |
| `alerts.timeline` | `kafka` | `alert_id` | notifier | api (`api`) |
| `schema.samples` | `kafka` | `fingerprint` | processor | ai-worker (`ai-worker`), api (`api`) |

Ingest is the only bridge between the two clusters: third-party systems never get access to the internal one.

Delivery is at-least-once everywhere: commit a Kafka offset only after the resulting write is
durable, and make every write idempotent (`event_id`, `change_id`, notification `id`).

## Env vars

| Var | Used by | Example |
|---|---|---|
| `KAFKA_BROKERS` | all backend services | `kafka:9092` (internal cluster) |
| `SOURCE_KAFKA_BROKERS` | ingest | `kafka-sources:9092` (sources cluster) |
| `LOG_THROTTLE_SECONDS` | ingest | A repeating failure (Kafka unreachable, database error) is logged at most once per this many seconds per error kind, with the count of hidden lines; default `10`, `0` logs every time |
| `ENABLE_SWAGGER` | ingest | `true` turns on the OpenAPI spec (`/openapi/v1.json`) and Swagger UI (`/swagger`); also on in Development, off by default |
| `DATABASE_URL` | all backend services, each its own value | `postgres://alerts:alerts@processor-db:5432/processor` (hosts and database names: [storage.md](storage.md)) |
| `API_URL` | ai-worker | `http://api:8000/api` (the REST base of [api.md](api.md), `/api` included) |
| `REDIS_URL` | processor, notifier, api | `redis://redis:6379/0` |
| `RUNBOOK_EXECUTOR` | api | `true` (default) runs the runbook executor in this replica; `false` for replicas that only serve REST, with one more container running it ([runbooks.md](runbooks.md) §2) |
| `RUNBOOK_EXECUTION_ENABLED` | api | `true` (default). `false` is the kill switch: the executor starts but takes no step, so nothing runs until it is set back |
| `RUNBOOK_CONCURRENCY`, `RUNBOOK_POLL_MS`, `RUNBOOK_LEASE_S` | api | Executor loops per replica (`4`), pause when idle (`1000`), claim length in seconds (`120`; a step's timeout is cut to fit) |
| `PROCESSOR_WORKERS` | processor | `1` (default). Worker processes inside one container; each is a full replica with its own Kafka consumer in group `processor` |
| `SMTP_HOST`, `SMTP_PORT` | notifier | `mailhog`, `1025` |
| `SMS_GATEWAY_URL`, `CALL_GATEWAY_URL` | notifier | `http://mock-gateway:8002/sms`, `http://mock-gateway:8002/call` |
| `PUBLIC_BASE_URL` | notifier, api | `http://localhost:3000` (short alert link: `{PUBLIC_BASE_URL}/a/{alert_id}`) |
| `VITE_API_URL` (or the frontend stack's equivalent) | frontend | `http://localhost:8000` |
| `ANTHROPIC_API_KEY` | ai-worker only | optional: without it the system runs on seeded mappings and new schemas stay unmapped |
| `ANTHROPIC_MODEL` | ai-worker only | optional, default `claude-sonnet-5-5` |
| `TYPESAFE_API_KEY` | ai-worker only | optional: without it the ai-worker makes no `rule_severity` or `runbook_start` proposal and posts no run risk check (it does not poll `GET /runs`; those jobs stay pending) |
| `TYPESAFE_BASE_URL` | ai-worker only | optional, default `https://api.typesafe.ai` (a local stand-in for tests) |
| `TYPESAFE_MODEL` | ai-worker only | optional, default `jev-latest` |
| `AGENT_METRICS_FILE` | ai-worker only | optional, default `logs/agent-metrics.jsonl` (one JSON line per job attempt: tokens, cost, tool calls, retries; docker compose mounts `backend/ai-worker/logs`) |

No auth in the PoC. The UI has a user picker and sends the chosen user as the `X-User` header.
