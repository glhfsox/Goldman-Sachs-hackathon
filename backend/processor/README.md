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
docker compose exec -T processor-db psql -U alerts -d processor -c "TRUNCATE alerts, outbox, outbox_dead, schema_samples, parked_events"
docker compose exec -T redis redis-cli FLUSHALL
docker compose up -d processor
```

Ports on the host: Kafka `localhost:9092`, Postgres `localhost:5434` (user/password `alerts`, db
`processor`), Redis `localhost:6379`.

## Unmapped events wait for their mapping

An event no mapping fits still runs through the rules as `unmapped`, and is also parked in `parked_events`
(up to 1,000 per schema, for 1 hour). When a replica's compiled config changes, and once at its start, it
re-runs the parked events through the current mappings, one replica at a time; those that map now are
counted like fresh events (the rules they already matched as unmapped are skipped) and deleted, the rest stay.
Known limits: a crash between counting a replayed page and deleting it, or an `events.raw` batch redelivered
before or after its parked rows were replayed, can over-count by at most one batch (the alert and its `opened` record
are never duplicated); replayed events keep their original `received_at`; past 1,000 events of one schema
the rest are not parked, so the alert's count starts from those 1,000.

## What is mocked or missing

- No ingest: `load.py` writes `events.raw` directly and stamps `received_at` as ingest would.
- No api: configuration comes from `seed.py`, using the brief's mappings and rules in `tests/conftest.py`.
- No notifier, UI or ai-worker: nothing consumes `alerts.changes` or `schema.samples` except `load.py`.
- `acked` / `resolved` records are applied, but nothing publishes them yet (the api will).
- Incidents are not built.

## Measured (21-11, one Mac, Docker Desktop, mock data from `load.py`)

Published flat out unless a rate is given. `load.py` introduces new alert keys at a constant rate over the
whole run, so the latency is that of steady state, not of a burst of new alerts at the start. The table was
measured when `load.py` keyed events by source, so only 3 of the 6 `events.raw` partitions got traffic; it now
keys them by `event_id`, as ingest does, so all 6 do and the events of one alert reach every worker.
Measured again that way, with the atomic Redis batch (100,000 events, 90% duplicates, flat out, backlog):
3 workers 31,913 events/s, 6 workers 36,310 events/s, and the alert counters add up to the events sent.

| Workers | Events | Duplicates | Rate | Processed events/s | Ingest → alert visible, p50 / p99 |
|---|---|---|---|---|---|
| 1 | 100,000 | 90% | flat out | 15,599 | backlog (publisher is 10× faster) |
| 6 | 100,000 | 90% | flat out | 24,333 | backlog |
| 1 | 100,000 | 90% | 10,000/s | 9,569, kept up | 94 ms / 530 ms |
| 6 | 100,000 | 90% | 10,000/s | 9,677, kept up | 125 ms / 1,518 ms |
| 1 | 50,000 | 0% (every event a new alert) | flat out | 6,618 | backlog |
| 1 | 30,000 | 0% | 1,000/s | 996, kept up | 103 ms / 352 ms |

Known: new alerts are opened per Kafka batch in one transaction with a fixed number of round trips, so a
new alert costs 0.15 ms in all (6,618/s) instead of 0.48 ms; about two thirds of that is Postgres writing the row,
its three indexes and the outbox row, which is now the limit of all-distinct traffic. The 2 to 4 s delay seen before came from two things, both fixed:
the relay published at most 500 outbox rows per step, so a burst of `count_threshold` records held up newer
`opened` records, and `load.py` sent every new key in the first second. With 6 workers at 10,000/s
every worker flushed the same dirty alerts and auto-resolved the same quiet ones, queueing on each other's row
locks; now one worker at a time does each (advisory locks) and no lock waits are left. The tail remains and is
not explained: p99 204, 534, 3,266, 234 and 1,435 ms in runs without auto-resolve, 3,620 ms in a run during which
6,792 alerts auto-resolved. The profiler then shows the relay sending outbox rows (the `resolved` records go
out ahead of newer `opened` ones) and WAL writes on commit, not locks.
