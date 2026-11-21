# ai-worker

An LLM agent off the event path: no event waits for it. It turns three kinds of Kafka records into jobs (table
`jobs`, [contracts/schemas/ai-worker.md](../../contracts/schemas/ai-worker.md)) and runs one Claude tool-use loop
per job. Each loop has exactly one tool; its handler validates what the model submits in code, returns the errors to
the model so it can fix and resubmit, and saves the result through the api's REST endpoints once it is valid.

| Record | Job id | Kind | What the job does |
|---|---|---|---|
| `schema.samples` (group `ai-worker`, from the earliest offset) | `schema:<fingerprint>` | `mapping` | Proposes a mapping for an unmapped schema: `PUT /mappings/{id}` |
| `config.changes` kind `runbook`, `status: draft`, `canonical: null` (no group, read from the start at every start) | `runbook:<id>:<version>` | `runbook` | Converts the runbook source to the canonical form: `PUT /runbooks/{id}` |
| `alerts.changes` with `change: resolved` (group `ai-worker`, from the latest offset) | `proposal:<alert id>` | `proposal` | Drafts a runbook update from the alert's timeline: `POST /alerts/{id}/proposals` |

`config.changes` also feeds an in-memory copy of the current mappings, which the mapping tool checks a new mapping
against. A job is retried up to 3 times on unexpected errors (api down, model API errors); a refusal, a rejected
submission or a 4xx from the api fails it at once.

Status: the loop, queue, consumers and REST client are built; the three tools (`ai_worker/mapping.py`,
`ai_worker/runbooks.py`) are placeholders that fail every job with "not implemented".

## Run

```bash
bash check.sh                                   # tests (Postgres from TEST_DATABASE_URL or a temporary initdb)
docker compose up -d --build                    # ai-worker + stub api + its Postgres + the processor stack and Kafka
ANTHROPIC_API_KEY=sk-... docker compose up -d ai-worker
docker compose logs -f ai-worker stub-api
docker compose down -v
```

Env: `KAFKA_BROKERS`, `DATABASE_URL`, `API_URL` (with `/api`), optional `ANTHROPIC_API_KEY` and `ANTHROPIC_MODEL`
(default `claude-opus-5-5`). Without a key the worker starts, keeps consuming and enqueuing, logs one warning and runs
no job. The image also holds the processor's `core.py` and `models.py` (pure mapping code, imported, not copied).

## What is mocked

- The api: `scripts/stub_api.py` (port 8000) answers the endpoints the worker calls, keeps state in memory, logs every
  request, publishes the mappings and runbooks it saves to `config.changes`, and returns `scripts/alert_fixture.json`
  for any `GET /alerts/{id}`. Replace `API_URL` with the real api once it serves these endpoints.

## What is sent to Claude

Only the job's input, as untrusted data: one truncated sample payload per new schema, a runbook's source text, or
one resolved alert with its timeline. Nothing the model writes is saved until code has validated it.
