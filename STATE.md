# STATE

Cross-zone changes: only what other teammates' agents need to know. Rules are in AGENTS.md, section "STATE.md". Write only in your own section, newest on top, delete what's superseded.

Format: `- DD-MM HH:MM · **what** — what changed for others. Affects: zones. Where: path.`

## glhfsox — ai, data
- 20-11 22:38 · **Sources get their own Kafka cluster** — third-party systems publish `source.*` to `kafka-sources` (host port 9094, `kafka-sources:9092` in compose); ingest reads it through `SOURCE_KAFKA_BROKERS` and is the only bridge to the internal `kafka` cluster. Affects: backend, data. Where: contracts/services.md, contracts/events.md.
- 20-11 22:38 · **`backend/ai-worker/` is owned by glhfsox** — the rest of `backend/` is unchanged. Affects: backend. Where: .github/CODEOWNERS, AGENTS.md.
- 20-11 22:40 · **Shared contracts v1** — service layout (`backend/<service>/`), ports, topics, env vars, Kafka records, condition/mapping/rule language, Postgres tables, REST + WebSocket API, runbook format and plugin interfaces. Build against these; change them by PR. Affects: backend, frontend, data, ai. Where: contracts/services.md, events.md, rules.md, storage.md, api.md, runbooks.md.
- 20-11 22:40 · **Architecture agreed** — five services (ingest, processor, notifier, api, ai-worker) on Kafka + Redis + Postgres; no Claude call per event; the LLM only writes schema mappings and converts runbooks, off the event path. Affects: all zones. Where: OVERVIEW.md "Architecture", docs/architecture/map.jsonl.

## spdsslg — data

## SviatoslavShpylovyi — backend

## oshuptar — frontend, presentation
