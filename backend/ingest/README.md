# Ingest

Receives events from sources (HTTP webhooks, Kafka `source.*` topics), stamps them and writes them to `events.raw`.
Ports, env vars and topics: [contracts/services.md](../../contracts/services.md).

## Run

```bash
docker compose up --build        # from backend/ingest: Ingest + ingest-db + shared Kafka
curl http://localhost:8001/health
```

API docs (on in the compose run): spec `http://localhost:8001/openapi/v1.json`, Swagger UI `http://localhost:8001/swagger`.
The switch is `ENABLE_SWAGGER=true` (or the Development environment); it is off by default.

## Postman

```bash
./postman/run.sh                 # newman against Ingest; starts docker compose if Ingest is down
KEEP_UP=1 ./postman/run.sh       # leave the stack running afterwards
```

The collection is `postman/ingest.postman_collection.json` (import it into Postman; variable `baseUrl`).

## Check

```bash
./check.sh                       # build + unit tests
RUN_INTEGRATION=1 ./check.sh     # also container tests (needs Docker)
```
