# Database `ingest` (service: ingest)

Host `ingest-db`. Only ingest connects. Migrations: `backend/ingest/migrations/`.
Shared rules: [../storage.md](../storage.md).

Ingest owns no data. It keeps one copy so that it can answer `404` for an unknown source and know
which `source.*` topics to consume without calling another service.

## Copies (fed by Kafka `config.changes`)

### `sources`: from kind `source`

| Column | Type | Notes |
|---|---|---|
| `id` | text, primary key | The `source_id` in the ingest URL and on every raw event |
| `kind` | text, not null | `http` or `kafka` |
| `config` | jsonb, not null | For `kafka`: `{"topic": "source.workflows"}` |
| `enabled` | boolean, not null | A disabled source answers `404` and is not consumed |
| `updated_at` | timestamptz, not null | A record older than the row is ignored |

Example row:

```json
{"id": "workflows-kafka", "kind": "kafka", "config": {"topic": "source.workflows"}, "enabled": true,
 "updated_at": "2026-11-20T09:00:00.000Z"}
```

## Not here

Raw events are never stored in Postgres: they go straight to Kafka `events.raw`.
