# STATE

Cross-zone changes: only what other teammates' agents need to know. Rules are in AGENTS.md, section "STATE.md". Write only in your own section, newest on top, delete what's superseded.

Format: `- DD-MM HH:MM · **what** — what changed for others. Affects: zones. Where: path.`

## glhfsox — ai, data
- 20-11 22:40 · **Shared contracts v1** — service layout (`backend/<service>/`), ports, topics, env vars, Kafka records, condition/mapping/rule language, Postgres tables, REST + WebSocket API, runbook format and plugin interfaces. Build against these; change them by PR. Affects: backend, frontend, data, ai. Where: contracts/services.md, events.md, rules.md, storage.md, api.md, runbooks.md.
- 20-11 22:40 · **Architecture agreed** — five services (ingest, processor, notifier, api, ai-worker) on Kafka + Redis + Postgres; no Claude call per event; the LLM only writes schema mappings and converts runbooks, off the event path. Affects: all zones. Where: OVERVIEW.md "Architecture", docs/architecture/map.jsonl.

## spdsslg — data

## SviatoslavShpylovyi — backend

## oshuptar — frontend, presentation
