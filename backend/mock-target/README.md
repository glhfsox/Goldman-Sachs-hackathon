# mock-target

The system runbook actions call in the demo, standing in for workflow engines, services, databases, ticketing and
escalation. Port 8003 ([contracts/services.md](../../contracts/services.md)). Every seeded action target of the api
(`action_targets`, [contracts/runbooks.md](../../contracts/runbooks.md) §3) points here under its own id, e.g.
`http://mock-target:8003/workflow-engine/workflows/WF-1/retry`. A real system replaces it with one
`PUT /api/action-targets/{id}`; no code changes.

| Path | What it does |
|---|---|
| any other path, any method | Answers `200 {"ok": true, "path": ...}` and logs `{at, method, path, query, body, idempotency_key, status, replay}`. A repeated `Idempotency-Key` gets the first answer again and is logged with `replay: true`. |
| `POST /_fail` `{"path_prefix": "/escalation", "times": 2, "status": 503}` | The next `times` calls whose path starts with `path_prefix` answer `status` (to show a failing step) |
| `GET /_calls` | Every call so far, oldest first |
| `DELETE /_calls` | Forgets calls, stored answers and failure rules |

State is in memory only. What is mocked: everything behind it; the api's calls to it are real HTTP.

## Run and check

```bash
bash check.sh                     # build + tests
docker compose up --build         # from backend/api: the api starts mock-target too
curl http://localhost:8003/_calls
```
