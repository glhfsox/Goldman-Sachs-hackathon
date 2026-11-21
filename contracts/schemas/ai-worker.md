# Database `ai_worker` (service: ai-worker)

Host `ai-worker-db`. Only the ai-worker connects. Migrations: `backend/ai-worker/migrations/`.
Shared rules: [../storage.md](../storage.md).

The ai-worker owns its queue, `alert_history` (how people reacted to alerts) and `run_history` (how runbook
runs went).
Everything it produces (mappings, canonical runbooks, proposals) belongs to the api and is sent there
over REST ([../api.md](../api.md)).

## Owned tables

### `jobs`

| Column | Type | Notes |
|---|---|---|
| `id` | text, primary key | `schema:<fingerprint>`, `runbook:<runbook id>:<version>`, `proposal:<alert id>`, `severity:<rule id>:first`, `severity:<rule id>:<n>` (`n` = the rule's resolved alerts rounded down to a multiple of 20), `run_risk:<run id>` or `run_start:<rule id>:<runbook id>:<n>` (both ids URL-encoded; `n` = the pair's finished runs rounded down to a multiple of 10); inserting with `ON CONFLICT DO NOTHING` makes a redelivered record harmless |
| `kind` | text, not null | `mapping` (from Kafka `schema.samples`), `runbook` (from `config.changes`, kind `runbook`, status `draft`, no `canonical` yet), `proposal` (from `alerts.changes`, change `resolved`) or `severity` (from `alerts.changes`: an `opened` change for a rule with no resolved alert in `alert_history` yet, or a `resolved` change once the rule has 20 or more resolved alerts: one job per 20, so repeats and the 21st to 39th resolves add nothing), `run_risk` (from the api's `GET /runs?risk_check=pending`) or `run_start` (when `run_history` gets new runs of a pair that has 10 or more) |
| `input` | jsonb, not null | `{fingerprint, source_id, sample}`, the runbook record's body, `{alert_id}`, or `{rule_id, alert_id, mode: "first" \| "history", example}` (`example`: title, body, fields and the sample event's type, service, env, severity, from the triggering record; null when that record had only `alert.id`), the `RunbookRun` as the api returned it, or `{rule_id, runbook_id}` |
| `status` | text, not null | `pending`, `done`, `failed` |
| `attempts` | int, not null | A job that keeps failing for a temporary reason becomes `failed` at 6 |
| `run_after` | timestamptz | Set when a failed attempt sends the job back to `pending`: it is not claimed before then (10 s doubling to at most 300 s, with jitter; 2 s for `run_risk`). Null otherwise (migration `004_job_backoff.sql`) |
| `result` | jsonb | What was sent to the api |
| `error` | text | |
| `created_at`, `finished_at` | timestamptz | |

Workers claim rows with `FOR UPDATE SKIP LOCKED` where `status = 'pending'`, `run_after` is null or past, and the kind is one whose
API key is set (`ANTHROPIC_API_KEY`: mapping, runbook, proposal; `TYPESAFE_API_KEY`: severity, run_risk,
run_start), `run_risk` first (it is useless after the run's `start_at`), and hold
the row lock for the whole job, so a worker that dies releases its job; there is no stored `running` state.

Example row:

```json
{"id": "schema:9f2c41d0...", "kind": "mapping", "status": "done", "attempts": 1,
 "input": {"fingerprint": "9f2c41d0...", "source_id": "trades-kafka", "sample": {"meta": {"schema": "trade.rejected.v1"}, "payload": {"desk": "rates"}}},
 "result": {"mapping_id": "trade-rejected-v1"}, "created_at": "2026-11-20T10:15:02.300Z",
 "finished_at": "2026-11-20T10:15:09.800Z"}
```

### Checkpoint tables (LangGraph)

`checkpoint_migrations`, `checkpoints`, `checkpoint_blobs`, `checkpoint_writes`: the state of each running Claude
job's tool-use loop (the conversation sent to Claude, the model's replies, reply and token counters), one thread per
job, `thread_id` = `jobs.id`. Created and migrated by `langgraph-checkpoint-postgres` (`PostgresSaver.setup()`,
its own versions in `checkpoint_migrations`), which the worker runs at start under the same advisory lock as
`migrations/`; their columns are that library's (`langgraph-checkpoint-postgres` 3.2.1:
thread_id, checkpoint_ns, checkpoint_id, parent_checkpoint_id, checkpoint jsonb, metadata jsonb; blobs and writes as
bytea). A job that resumes reads its thread; the rows of a job are deleted in the same transaction that sets the job
`done` or `failed`, so none are left behind. Nothing else reads them.

### `alert_history`

One row per alert, written from every `alerts.changes` record ([../events.md](../events.md) §4) before
the offset is committed. Every column is set once, a flag, or a latest value, so a redelivered record
changes nothing. A record with only `alert.id` (the api's `acked` / `resolved`) updates an existing row
and is skipped when there is none. The severity judge reads it: its stats use the rule's most recent
resolved alerts, at most 200 and resolved in the last 30 days.

| Column | Type | Notes |
|---|---|---|
| `alert_id` | text, primary key | `alert.id` |
| `rule_id` | text, not null | `alert.rule_id` |
| `team_id` | text | `alert.team_id` |
| `severity` | text | `alert.severity`, latest record |
| `count` | bigint | `alert.count`, highest seen |
| `opened_at` | timestamptz | `alert.first_received_at`, else the `at` of the `opened` change; set once |
| `acked_at`, `acked_by` | timestamptz, text | `at` and `actor` of the first `acked` change |
| `resolved_at`, `resolved_by` | timestamptz, text | `at` and `actor` (a user id or `auto`) of the latest `resolved` change |
| `reopened` | boolean, not null | true once a `reopened` change was seen |

Index: `(rule_id, resolved_at DESC) WHERE resolved_at IS NOT NULL`.

Example row:

```json
{"alert_id": "5b0f...", "rule_id": "settlements-workflow-failed", "team_id": "settlements",
 "severity": "critical", "count": 3, "opened_at": "2026-11-20T10:15:02.123Z",
 "acked_at": "2026-11-20T10:17:10.000Z", "acked_by": "marek",
 "resolved_at": "2026-11-20T10:21:00.000Z", "resolved_by": "marek", "reopened": false}
```

### `run_history`

One row per finished runbook run, copied from the api's `GET /runs?finished_since=` ([../api.md](../api.md),
"Runbook runs"). A finished run does not change, so a row is inserted once and a repeat changes nothing. The
start-mode judge reads it: its stats use the pair's most recent finished runs, at most 50 and finished in the last
30 days.

| Column | Type | Notes |
|---|---|---|
| `run_id` | text, primary key | `RunbookRun.id` |
| `alert_id`, `rule_id`, `runbook_id` | text, not null | |
| `team_id` | text | |
| `trigger` | text | `auto` or `manual` |
| `status` | text, not null | `done`, `failed` or `cancelled` |
| `risk_check` | text | `risk_check.verdict` (`clear` or `hold`), null when it had none |
| `actions` | jsonb | `[{action, risk}]` of its nodes, no params |
| `start_at`, `approved_at`, `started_at` | timestamptz | |
| `finished_at` | timestamptz, not null | |
| `approved_by`, `cancelled_by` | text | user ids |

Index: `(rule_id, runbook_id, finished_at DESC)`.

Example row:

```json
{"run_id": "run-7f3a", "alert_id": "5b0f...", "rule_id": "settlements-workflow-failed",
 "runbook_id": "settlement-timeout", "team_id": "settlements", "trigger": "manual", "status": "done",
 "risk_check": null, "actions": [{"action": "retry_workflow", "risk": "auto"}],
 "start_at": "2026-11-21T10:00:00.000Z", "approved_by": "marek", "approved_at": "2026-11-21T10:00:08.000Z",
 "started_at": "2026-11-21T10:00:08.000Z", "finished_at": "2026-11-21T10:00:40.000Z", "cancelled_by": null}
```
