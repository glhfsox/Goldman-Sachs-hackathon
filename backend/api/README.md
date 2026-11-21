# API

REST and WebSocket for the UI; owns all configuration and publishes it on `config.changes` (through an outbox).
Ports, env vars and topics: [contracts/services.md](../../contracts/services.md). Endpoints: [contracts/api.md](../../contracts/api.md).
Tables: [contracts/schemas/api.md](../../contracts/schemas/api.md).

Status: `/health`, the outbox publisher and the `/api/sources`, `/api/rules`, `/api/mappings`, `/api/teams`, `/api/users`, `/api/users/{id}/preferences`, `/api/runbooks`, `/api/actions` and `/api/action-targets` endpoints; runbook actions are plugins in `src/AlertApi.Infrastructure/Actions/` and land on `backend/mock-target/` in the compose run. Other config kinds, alerts and WebSocket are added in later PRs.

## Run

```bash
docker compose up --build        # from backend/api: API + api-db + shared Kafka
curl http://localhost:8000/health
```

API docs (on in the compose run): spec `http://localhost:8000/openapi/v1.json`, Swagger UI `http://localhost:8000/swagger`.
The switch is `ENABLE_SWAGGER=true` (or the Development environment); it is off by default.

## Postman

```bash
./postman/run.sh                 # newman against the API; starts docker compose if the API is down
KEEP_UP=1 ./postman/run.sh       # leave the stack running afterwards
```

The collection is `postman/api.postman_collection.json` (import it into Postman; variable `baseUrl`). Only Kafka can create alerts and schema samples, so `run.sh` first puts one alert and two samples on Kafka the way the processor does; ids are unique per run, so it can run again on the same database.

## Failure check: Kafka down

```bash
./postman/failure-check.sh       # stop Kafka, POST a rule, check the API, start Kafka, find the record
```

A config change is written to the `outbox` table in the same transaction as the change, and a background job publishes it, so the API keeps answering with Kafka down and the record reaches `config.changes` when Kafka is back. The script prints `PASS` when it did.

## Check

```bash
./check.sh                       # build + unit tests
RUN_INTEGRATION=1 ./check.sh     # also container tests (needs Docker)
```
