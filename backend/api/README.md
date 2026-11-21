# API

REST and WebSocket for the UI; owns all configuration and publishes it on `config.changes` (through an outbox).
Ports, env vars and topics: [contracts/services.md](../../contracts/services.md). Endpoints: [contracts/api.md](../../contracts/api.md).
Tables: [contracts/schemas/api.md](../../contracts/schemas/api.md).

Status: `/health`, the outbox publisher and the `/api/sources`, `/api/rules`, `/api/mappings`, `/api/teams`, `/api/users`, `/api/users/{id}/preferences` and `/api/runbooks` endpoints. Other config kinds, alerts and WebSocket are added in later PRs.

## Run

```bash
docker compose up --build        # from backend/api: API + api-db + shared Kafka
curl http://localhost:8000/health
```

API docs (on in the compose run): spec `http://localhost:8000/openapi/v1.json`, Swagger UI `http://localhost:8000/swagger`.
The switch is `ENABLE_SWAGGER=true` (or the Development environment); it is off by default.

## Check

```bash
./check.sh                       # build + unit tests
RUN_INTEGRATION=1 ./check.sh     # also container tests (needs Docker)
```
