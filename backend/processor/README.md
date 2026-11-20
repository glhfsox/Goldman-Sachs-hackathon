# processor

Reads raw events from Kafka `events.raw`, applies schema mappings and rules, deduplicates into alerts
(Postgres `processor-db`, counters in Redis) and publishes alert changes to `alerts.changes` and new
schemas to `schema.samples`. Interfaces: [contracts/events.md](../../contracts/events.md),
[contracts/rules.md](../../contracts/rules.md), [contracts/schemas/processor.md](../../contracts/schemas/processor.md).

Unit tests: `bash check.sh`.

## Run it for real (Docker, from a fresh clone)

Needs Docker and [uv](https://docs.astral.sh/uv/). From `backend/processor`:

```bash
docker compose up -d --build          # shared Kafka (../kafka), Redis, processor-db, processor
uv run python scripts/seed.py         # mappings and rules to config.changes (stands in for the api)
uv run python scripts/load.py 100000 --dup 0.9               # flat out
uv run python scripts/load.py 100000 --dup 0.9 --rate 5000   # at a target rate
docker compose logs -f processor
docker compose down -v                # stop everything and delete the data
```

More workers (each is a full replica in consumer group `processor`; `events.raw` has 6 partitions):
`PROCESSOR_WORKERS=3 docker compose up -d processor`.

`load.py` prints the publish rate, processed events/s (first publish until the `processor` group's
committed offsets reach the end of `events.raw`) and ingest → alert visible latency (Kafka timestamp of
each `opened` record minus the alert's `first_received_at`). Each run uses fresh alert keys.

Reset alert state between runs (keeps the seeded config):

```bash
docker compose stop processor
docker compose exec -T processor-db psql -U alerts -d processor -c "TRUNCATE alerts, outbox, outbox_dead, schema_samples"
docker compose exec -T redis redis-cli FLUSHALL
docker compose up -d processor
```

Ports on the host: Kafka `localhost:9092`, Postgres `localhost:5434` (user/password `alerts`, db
`processor`), Redis `localhost:6379`.

## What is mocked or missing

- No ingest: `load.py` writes `events.raw` directly and stamps `received_at` as ingest would.
- No api: configuration comes from `seed.py`, using the brief's mappings and rules in `tests/conftest.py`.
- No notifier, UI or ai-worker: nothing consumes `alerts.changes` or `schema.samples` except `load.py`.
- `acked` / `resolved` records are applied, but nothing publishes them yet (the api will).
- Incidents are not built.

## Measured (21-11, one Mac, Docker Desktop, mock data from `load.py`)

100,000 events, published flat out unless a rate is given. Only 3 of the 6 `events.raw` partitions get
traffic, because events are keyed by source and the samples have 3 sources.

| Workers | Duplicates | Rate | Processed events/s | Ingest → alert visible, p50 / p99 |
|---|---|---|---|---|
| 1 | 90% | flat out | 12,397 | backlog (publisher is 10× faster) |
| 6 | 90% | flat out | 16,508 | backlog |
| 6 | 90% | 10,000/s | 9,015, kept up | 1.1 s / 1.8 s |
| 1 | 0% (every event a new alert) | flat out | about 2,400 | backlog |
| 1 | 0%, 30,000 events | 1,000/s | 995, kept up | 0.2 s / 0.4 s |

Known: a new alert costs one Postgres transaction, so all-distinct traffic is the slow case and its rate
varied between runs (1,200 to 3,000 events/s with 3 workers); the 1 to 3 s delay at 90% duplicates is not
explained yet.
