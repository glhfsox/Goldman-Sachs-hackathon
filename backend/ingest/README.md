# Ingest

Receives events from sources (HTTP webhooks, Kafka `source.*` topics), stamps them and writes them to `events.raw`.
Ports, env vars and topics: [contracts/services.md](../../contracts/services.md).

## Run

```bash
docker compose up --build        # from backend/ingest: Ingest + ingest-db + shared Kafka
curl http://localhost:8001/health
```

## Check

```bash
./check.sh                       # build + unit tests
RUN_INTEGRATION=1 ./check.sh     # also container tests (needs Docker)
```
