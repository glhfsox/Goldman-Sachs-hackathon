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
| `alerts.changes`: a rule's first `opened`, and a `resolved` once per 20 resolved alerts of the rule | `severity:<rule id>:first`, `severity:<rule id>:<n>` | `severity` | Proposes the rule's severity (TypeSafe Jev, not Claude): `POST /alerts/{id}/proposals`, kind `rule_severity` |

`config.changes` also feeds in-memory copies of the current mappings, which the mapping tool checks a new mapping
against, and of the rules, which the severity judge reads. A job is retried up to 3 times on unexpected errors (api down, model API errors); a refusal, a rejected
submission or a 4xx from the api fails it at once.

Status: all three tools are built (`ai_worker/mapping.py`, `ai_worker/runbooks.py`) and were run end to end
against the real Claude API; the api they save to is still the stub.

## Run

```bash
bash check.sh                                   # tests (Postgres from TEST_DATABASE_URL or a temporary initdb)
docker compose up -d --build                    # ai-worker + stub api + its Postgres + the processor stack and Kafka
ANTHROPIC_API_KEY=sk-... docker compose up -d ai-worker
TYPESAFE_API_KEY=... docker compose up -d ai-worker   # the Jev judges; then scripts/judge_demo.py or scripts/runs_demo.py
docker compose logs -f ai-worker stub-api
docker compose down -v
```

Next to another stack that already uses the default ports, run it as its own project with a ports override file, and
export `KAFKA_HOST_PORT` (e.g. `29192`) for every compose command: without it Kafka advertises `localhost:9092` and a
host client silently talks to the other stack's broker.

Env: `KAFKA_BROKERS`, `DATABASE_URL`, `API_URL` (with `/api`), optional `ANTHROPIC_API_KEY` and `ANTHROPIC_MODEL`
(default `claude-opus-5-5`), optional `TYPESAFE_API_KEY`, `TYPESAFE_BASE_URL` (default `https://api.typesafe.ai`) and
`TYPESAFE_MODEL` (default `jev-latest`). Without a key the worker starts, keeps consuming and enqueuing, logs one
warning per missing key and runs no job of the kinds that need it (Anthropic: mapping, runbook, proposal; TypeSafe:
severity, run_risk, run_start; without the TypeSafe key it does not poll `GET /runs` either). The image also holds the processor's `core.py` and `models.py` (pure mapping code, imported, not copied).

## Severity judge (`ai_worker/judge.py`)

Proposes what severity a rule should fix for its alerts (the rule's `severity`, contracts/rules.md §4), from how
people reacted to them. It never changes a rule: it files a `rule_severity` proposal (contracts/api.md) that a person
accepts or rejects. One TypeSafe Jev call per job (a decision model: typed choices with probabilities, no text);
none per event.

- Every `alerts.changes` record updates one row of `alert_history` (opened, first ack, last resolve, reopened, count).
- On a rule's first alert it judges from the rule and that alert alone. With some history it stays silent. Each time
  the rule's resolved alerts reach a multiple of 20 it judges from its most recent resolved alerts: at most 200,
  resolved in the last 30 days (`MAX_ALERTS`, `MAX_AGE_DAYS`); fewer than 20 inside that window files nothing.
- Jev answers one choice (info / warning / error / critical, with probabilities) and a few yes/no factors. A
  proposal is filed only when the choice differs from the rule's current `severity` with confidence at least 0.5.
  The reasons shown to people are written by code: a fixed phrase per factor at probability 0.7 or more, and
  sentences from the stats.

What is sent to TypeSafe (each value cut to 2,000 characters of JSON): the rule's team id, id, name, `when`, `route`,
title template and current `severity`; the triggering alert's title, body, fields and its sample event's type,
service, env and severity (not the raw payload); the stats (number of resolved alerts, share acknowledged by a person,
median minutes to acknowledge, share resolved on their own without an ack, share reopened, median count, severity
mix, the window). The only user ids sent are those the rule's `route` names; the history's
`acked_by` / `resolved_by` are not sent.

`scripts/judge_demo.py` publishes a rule and 20 alerts' changes to Kafka and prints what the stub api received.

## Run judges (`ai_worker/runs.py`, issue #75)

Two more Jev judges, for runbook runs (the run model of issue #73). On its own a judge can only make a run more
careful; more automation is only ever a proposal a person accepts.

- **Live risk check.** Every 2 s the worker asks the api for scheduled `auto` runs without a verdict
  (`GET /runs?risk_check=pending`) and makes a `run_risk` job per run. The job asks Jev `clear` or `hold` and posts
  the verdict (`POST /runs/{id}/risk-check`): `clear` only when Jev chose it and both its confidence and the
  probability of `clear` are at least 0.6 (`CLEAR_MIN`), else `hold`. A run whose nodes are over 16,000 characters
  of JSON is held without asking Jev ("the run is too large to check"). A job that runs after the run's `start_at` does nothing; the api has already held the
  run for a person. A `409` (too late) is not an error. `run_risk` jobs are claimed before any other job.
- **Start mode.** Every 30 s the worker copies finished runs (`GET /runs?finished_since=`) into `run_history`. Each
  time a (rule, runbook) pair's runs reach a multiple of 10 it judges from the pair's most recent ones: at most 50,
  finished in the last 30 days (`MAX_RUNS`, `MAX_RUN_AGE_DAYS`); fewer than 10 inside that window files nothing. A
  `runbook_start` proposal (`auto` or `approval`) is filed only when Jev's choice differs from how the pair's runs
  start today (`auto` when at least half of the window's runs started by themselves), with confidence of at least
  0.6. A run dated more than 5 minutes in the future is skipped.
- An api without `/runs` (404), an api that is down or returns junk: logged once per outage; the pending poll retries
  every 5 s (every 30 s after a 404), the finished poll every 30 s; Kafka consumption goes on.

What is sent to TypeSafe: for a risk check, the run's risk and trigger, each node's action, registry risk, filled
params and text (each value cut to 2,000 characters of JSON); the alert's title, severity and
its event's type, service and env (no raw payload, no user ids); the rule's name and `when`. For a start-mode job,
the rule's id, name and `when`, the runbook id, the latest run's actions (names and risk, no params), how runs start
today, and the stats (shares of runs started by themselves, approved, cancelled before and after start, held by the
risk check, failed; median seconds from the alert to the approval; how many of the rule's alerts got no run; the
window). No user ids.

`scripts/runs_demo.py` schedules three `auto` runs in the stub api (a staging retry, a 25,000,000 EUR prod settlement
retry, and one whose window is already over), prints each verdict, then adds 10 finished runs and prints the
`runbook_start` proposal.

`evals/run_risk_golden.jsonl` holds 30 labelled runs (15 `clear`, 15 `hold`) for the risk check;
`scripts/eval_run_risk.py` sends each through the job's own code to the real Jev and counts the misses. Measured
on 21-11: 25 of 30 right, no run cleared that should have been held, 5 held that could have started, the same
verdicts on three repeats. Run it before and after any change of the question wording or of `CLEAR_MIN`.

## What is mocked

- The api: `scripts/stub_api.py` (port 8000) answers the endpoints the worker calls, keeps state in memory, logs every
  request, publishes the mappings and runbooks it saves to `config.changes`, and returns `scripts/alert_fixture.json`
  for any `GET /alerts/{id}` it was not given. It also plays the run side of issue #75: `GET /runs`, risk checks,
  approve (403 for a service actor), and a scheduled run with no verdict going to `waiting_approval` at `start_at`;
  `POST /api/stub/runs` and `POST /api/stub/finished-runs` add runs for demos. Replace `API_URL` with the real api
  once it serves these endpoints.

## What is sent to Claude

Only the job's input, marked as untrusted data, with the job kind's system prompt. Nothing the model writes is saved
until code has validated it.

- `mapping`: the source id, the whole sample payload as the processor published it (the processor already cuts a
  payload over 16 KB to a preview; such a sample fails the job without a model call), and the id and `match` of every
  current mapping. The tool's error messages echo offending values, and the sample of another schema the mapping
  would also take, as JSON cut to 600 characters (an api 422 body to 2,000).
- `runbook`: the title, the format and the whole source text. A `link` runbook sends nothing.
- `proposal`: the alert's title, type, env and service; its sample event with `raw` as JSON text cut to 4,000
  characters; the timeline (time, actor, kind) with each action run, its `result` as JSON text cut to 500 characters.
  A cut ends with a visible `[truncated: ...]` marker.

## Known limits

- A worker killed mid-job re-runs that job on its next start without counting an attempt. A `proposal`,
  `severity` or `run_start` job that died between the `POST` and the commit files a second proposal.
- One worker runs one job at a time: a long Claude job can delay a `run_risk` job past its run's `start_at`, and the
  run then waits for a person (safe, but less automation).
- A `mapping` job that fails is not retried: the processor publishes one sample per schema, so a person writes that
  mapping.
- The stub api stands in for the real one.
