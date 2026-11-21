# Database `ai_worker` (service: ai-worker)

Host `ai-worker-db`. Only the ai-worker connects. Migrations: `backend/ai-worker/migrations/`.
Shared rules: [../storage.md](../storage.md).

The ai-worker owns only its queue. Everything it produces (mappings, canonical runbooks, proposals)
belongs to the api and is sent there over REST ([../api.md](../api.md)).

## Owned tables

### `jobs`

| Column | Type | Notes |
|---|---|---|
| `id` | text, primary key | `schema:<fingerprint>`, `runbook:<runbook id>:<version>` or `proposal:<alert id>`; inserting with `ON CONFLICT DO NOTHING` makes a redelivered record harmless |
| `kind` | text, not null | `mapping` (from Kafka `schema.samples`), `runbook` (from `config.changes`, kind `runbook`, status `draft`, no `canonical` yet) or `proposal` (from `alerts.changes`, change `resolved`) |
| `input` | jsonb, not null | `{fingerprint, source_id, sample}`, the runbook record's body, or `{alert_id}` |
| `status` | text, not null | `pending`, `done`, `failed` |
| `attempts` | int, not null | A job that keeps failing for a temporary reason becomes `failed` at 3 |
| `result` | jsonb | What was sent to the api |
| `error` | text | |
| `created_at`, `finished_at` | timestamptz | |

Workers claim rows with `FOR UPDATE SKIP LOCKED` where `status = 'pending'` and hold the row lock for
the whole job, so a worker that dies releases its job; there is no stored `running` state.

Example row:

```json
{"id": "schema:9f2c41d0...", "kind": "mapping", "status": "done", "attempts": 1,
 "input": {"fingerprint": "9f2c41d0...", "source_id": "trades-kafka", "sample": {"meta": {"schema": "trade.rejected.v1"}, "payload": {"desk": "rates"}}},
 "result": {"mapping_id": "trade-rejected-v1"}, "created_at": "2026-11-20T10:15:02.300Z",
 "finished_at": "2026-11-20T10:15:09.800Z"}
```
