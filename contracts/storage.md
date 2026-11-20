# Storage: one Postgres per service

Every backend service has its own Postgres database and is the only service that connects to it.
The internal Kafka cluster and Redis are shared. The tables of each database are described in
`schemas/`, one file per service:

| Service | Compose host | Host port | Database | Schema |
|---|---|---|---|---|
| ingest | `ingest-db` | 5433 | `ingest` | [schemas/ingest.md](schemas/ingest.md) |
| processor | `processor-db` | 5434 | `processor` | [schemas/processor.md](schemas/processor.md) |
| notifier | `notifier-db` | 5435 | `notifier` | [schemas/notifier.md](schemas/notifier.md) |
| api | `api-db` | 5436 | `api` | [schemas/api.md](schemas/api.md) |
| ai-worker | `ai-worker-db` | 5437 | `ai_worker` | [schemas/ai-worker.md](schemas/ai-worker.md) |

User and password are `alerts` / `alerts` everywhere. Inside compose every database listens on 5432.
Each service gets only its own `DATABASE_URL` ([services.md](services.md)).

## Who owns what

| Data | Owner (source of truth) | Copies, and what feeds them |
|---|---|---|
| Sources, mappings, rules, teams, users, memberships, preferences, service map, runbooks | api | ingest (sources), processor (mappings, rules, service map), notifier (rules, teams, users, memberships, preferences, runbooks): Kafka `config.changes` |
| Alerts, incidents | processor | notifier and api: Kafka `alerts.changes`; api also takes `count` and `last_seen` from Redis `ui.alerts` |
| Notifications (delivery outbox) | notifier | api: Kafka `alerts.timeline` |
| Timeline (`alert_events`), action runs, proposals | api | none |
| Schema samples | processor (which schemas were seen) | api (list for the UI) and ai-worker (jobs): Kafka `schema.samples` |
| AI jobs | ai-worker | none; results go to the api over REST ([api.md](api.md)) |

Record formats of the topics: [events.md](events.md).

## Rules every service follows

- One owner per table. No service connects to another service's database, and no table is shared.
  Data crosses services only as Kafka records, or through the API's REST endpoints.
- A service that needs another service's data keeps its own copy and updates it from the topic.
  A copy holds only the columns that service uses, and may name or type them differently from the
  owner's table. That is why work on any exchange between two services starts from both schema files.
- A schema change or any migration changes two things in the same PR: the database (a migration in
  `backend/<service>/migrations/`) and that service's file in `schemas/`. If the changed column travels
  to another service, the record format in [events.md](events.md) changes in the same PR too.
- A service that must change its database and publish a Kafka record does both atomically: the record
  goes into an `outbox` table in the same transaction, and a relay publishes it afterwards.
- Configuration rows (`rules`, `mappings`, `sources`, `runbooks`) are never hard-deleted by their owner:
  set `enabled = false` / `status = 'disabled'` and touch `updated_at`. The change is published to
  `config.changes` like any other.
- Applying a record from Kafka is idempotent: upsert by primary key and ignore a record older than
  the row (`updated_at`, or the alert's `count` and status).
- Conventions: ids are `text` (opaque ULID or UUID), times are `timestamptz`, JSON columns are `jsonb`
  and hold the objects defined in the other contract files, unchanged.
