# STATE

Cross-zone changes: only what other teammates' agents need to know. Rules are in AGENTS.md, section "STATE.md". Write only in your own section, newest on top, delete what's superseded.

Format: `- DD-MM HH:MM · **what** — what changed for others. Affects: zones. Where: path.`

## glhfsox — ai, data
- 20-11 23:25 · **One Postgres per service** — no shared database: hosts `ingest-db`, `processor-db`, `notifier-db`, `api-db`, `ai-worker-db` (host ports 5433 to 5437), each service has its own `DATABASE_URL` and migrations in `backend/<service>/migrations/`. Tables per service are in `contracts/schemas/<service>.md`; a schema change or migration updates that file in the same PR (AGENTS.md "Database schemas"). Data crosses services only over the shared Kafka cluster: new topics `config.changes` (api → everyone's copies of rules, mappings, sources, teams, preferences, runbooks), `alerts.timeline` (notifier → api) and `schema.samples` (processor → ai-worker, api). Auto-resolve moved from notifier to processor; ai-worker saves results through the api (`API_URL`). Affects: backend, data, frontend, ai. Where: contracts/storage.md, contracts/schemas/, contracts/events.md §6–§8, contracts/services.md, OVERVIEW.md.
- 20-11 22:45 · **No approvals required to merge** — the merge bot no longer waits for a zone owner's or a contract consumer's approval; a green `gate` is enough. Still agree breaking contract changes with consumers and mention them in the PR. Affects: all zones. Where: AGENTS.md, .claude/agents/pr-merger.md.
- 20-11 22:38 · **Sources get their own Kafka cluster** — third-party systems publish `source.*` to `kafka-sources` (host port 9094, `kafka-sources:9092` in compose); ingest reads it through `SOURCE_KAFKA_BROKERS` and is the only bridge to the internal `kafka` cluster. Affects: backend, data. Where: contracts/services.md, contracts/events.md.
- 20-11 22:38 · **`backend/ai-worker/` is owned by glhfsox** — the rest of `backend/` is unchanged. Affects: backend. Where: .github/CODEOWNERS, AGENTS.md.
- 20-11 22:40 · **Shared contracts v1** — service layout (`backend/<service>/`), ports, topics, env vars, Kafka records, condition/mapping/rule language, REST + WebSocket API, runbook format and plugin interfaces. Build against these; change them by PR. Affects: backend, frontend, data, ai. Where: contracts/services.md, events.md, rules.md, storage.md, api.md, runbooks.md.
- 20-11 22:40 · **Architecture agreed** — five services (ingest, processor, notifier, api, ai-worker) on a shared Kafka + Redis, one Postgres each; no Claude call per event; the LLM only writes schema mappings and converts runbooks, off the event path. Affects: all zones. Where: OVERVIEW.md "Architecture", docs/architecture/map.jsonl.

## spdsslg — data
- 21-11 00:02 · **Sources contract v1** — what simulated (and real) sources send to ingest: 4 source ids to seed (`workflows-kafka`, `trades-kafka`, `alertmanager-http`, `pricing-logs-http`), `source.workflows` / `source.trades` records on `kafka-sources`, one event per HTTP request or Kafka message, sender retry rules, payload catalog with field types and examples, deliberate edge cases, volume profiles. The data zone will provide the `kafka-sources` broker; the simulator itself comes later. Affects: backend (ingest, processor seed mappings). Where: contracts/data/sources.md, contracts/events.md §1.

## SviatoslavShpylovyi — backend

## oshuptar — frontend, presentation
- 20-11 23:49 · **Shared Kafka for local runs** — `docker compose up -d` in `backend/kafka/` starts one broker (`kafka:9092` in compose, `localhost:9092` on the host) and creates the five internal topics; other compose files add it with `include: - path: ../kafka/docker-compose.yml` and wait for `kafka-init`. New rule: a service that produces or consumes Kafka records documents or reads them in `contracts/events.md` and updates it with the code (AGENTS.md "Kafka events"). The sources cluster is not part of it. Affects: backend, data, ai. Where: backend/kafka/, backend/AGENTS.md, AGENTS.md.
