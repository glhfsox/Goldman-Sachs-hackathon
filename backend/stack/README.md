# stack — the whole system in one compose project

The root [docker-compose.yml](../../docker-compose.yml) starts every service with its infrastructure:

```bash
docker compose up -d --build      # from the repo root; first build takes a few minutes
docker compose logs -f seed       # wait for "seed: done"
docker compose down -v            # stop everything and delete the data
```

The root file only `include`s each service's own compose file, so every service still runs alone from its
folder. Shared files (`backend/kafka`, `data/kafka-sources`) are included by several services and merged into
one copy. This folder holds only what the full stack adds:

| File | What |
|---|---|
| `ai-worker.override.yml` | Applied to `backend/ai-worker/docker-compose.yml`: the ai-worker calls the real api (`http://api:8000/api`); its `stub-api` gets profile `stub` and no port, so it does not start and does not take port 8000 |
| `docker-compose.yml` | The one-shot `seed` service |
| `seed/` | What `seed` loads through the api's REST endpoints ([contracts/api.md](../../contracts/api.md)): the runbook `settlement-timeout` (YAML source plus its canonical form, saved `active`, so no Claude key is needed), the four mappings of [contracts/rules.md](../../contracts/rules.md) §3 and the four example rules of §4. Every call is create-or-replace: `docker compose run --rm seed` again changes nothing but the runbook's version |

The api seeds sources, teams, users and preferences itself on first start; `seed` adds the rest, and every
consumer copies it from `config.changes`.

## What runs where (host ports)

| Service | Host port |
|---|---|
| api (REST, WebSocket, Swagger `/swagger`) | 8000 |
| ingest (`POST /ingest/{source_id}`) | 8001 |
| mock-gateway (SMS and calls) | 8002 |
| mock-target (runbook actions, `GET /_calls`) | 8003 |
| MailHog web / SMTP | 8025 / 1025 |
| kafka (internal) / kafka-sources | 9092 / 9094 |
| redis | 6379 (127.0.0.1) |
| ingest-db, processor-db, notifier-db, api-db, ai-worker-db | 5433 to 5437 |

Do not run a single service's compose file at the same time: it publishes the same ports.

## Send events

```bash
# one Alertmanager-style event over HTTP
curl -X POST localhost:8001/ingest/alertmanager-http -H 'Content-Type: application/json' -d '{
  "alertname": "HighErrorRate", "status": "firing", "startsAt": "2026-11-21T10:15:00Z",
  "labels": {"service": "payments-api", "region": "eu-west", "severity": "critical"},
  "annotations": {"summary": "Error rate 12% over the last 5 minutes"}}'
curl localhost:8000/api/alerts?team=payments

# the scripted demo morning (Kafka and HTTP): data/simulator/README.md
cd data/simulator && bash check.sh && .venv/bin/sim run demo
```

Optional keys, read from the environment or a root `.env`: `ANTHROPIC_API_KEY` (the ai-worker writes mappings
for unseen schemas and converts runbooks), `TYPESAFE_API_KEY` (the Jev judges).
