# Database `ai_worker` (service: ai-worker)

Host `ai-worker-db`. Only the ai-worker connects. Migrations: `backend/ai-worker/migrations/`.
Shared rules: [../storage.md](../storage.md).

The ai-worker owns only its queue. Everything it produces (mappings, canonical runbooks, proposals)
belongs to the api and is sent there over REST ([../api.md](../api.md)).

## Owned tables

### `jobs`

| Column | Type | Notes |
|---|---|---|
| `id` | text, primary key | `schema:<fingerprint>` or `runbook:<runbook id>:<version>`; inserting with `ON CONFLICT DO NOTHING` makes a redelivered record harmless |
| `kind` | text, not null | `mapping` (from Kafka `schema.samples`) or `runbook` (from `config.changes`, kind `runbook`, status `draft`) |
| `input` | jsonb, not null | The sample payload, truncated, or the runbook source text |
| `status` | text, not null | `pending`, `running`, `done`, `failed` |
| `attempts` | int, not null | |
| `result` | jsonb | What was sent to the api |
| `error` | text | |
| `created_at`, `finished_at` | timestamptz | |

Workers claim rows with `FOR UPDATE SKIP LOCKED` where `status = 'pending'`.

Example row:

```json
{"id": "schema:9f2c41d0...", "kind": "mapping", "status": "done", "attempts": 1,
 "input": {"source_id": "trades-kafka", "sample": {"meta": {"schema": "trade.rejected.v1"}, "payload": {"desk": "rates"}}},
 "result": {"mapping_id": "trade-rejected-v1"}, "created_at": "2026-11-20T10:15:02.300Z",
 "finished_at": "2026-11-20T10:15:09.800Z"}
```
