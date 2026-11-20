# Shared Kafka cluster (local)

One Kafka broker (KRaft, no ZooKeeper) used by every backend service. Local development only.
Topics, keys and records: [contracts/services.md](../../contracts/services.md), [contracts/events.md](../../contracts/events.md).

## Run

```bash
cd backend/kafka
docker compose up -d        # starts the broker and creates the topics
docker compose down -v      # stops it and deletes the data
```

| From | Address |
|---|---|
| Another container in the same compose | `kafka:9092` |
| Your machine | `localhost:9092` |

## Use it from your own compose file

```yaml
include:
  - path: ../kafka/docker-compose.yml
services:
  my-service:
    depends_on:
      kafka-init:
        condition: service_completed_successfully
```

Do not run this compose and an including one at the same time (same host port 9092).

## Topics

Created by `kafka-init` from `create-topics.sh`; safe to run again.

| Topic | Partitions | Notes |
|---|---|---|
| `events.raw` | 6 | Key `source_id`. Processor scales by partition |
| `alerts.changes` | 6 | Key `alert_id` |
| `config.changes` | 1 | Compacted, low volume |
| `alerts.timeline` | 3 | |
| `schema.samples` | 3 | |

Partitions limit consumer parallelism (one consumer per partition in a group). They can be raised later, never lowered, and raising them re-maps keys, so we keep headroom. Replication is 1: one local broker.

## Check

`bash check.sh` validates the compose file and that `create-topics.sh` matches the topic list in `contracts/services.md`. It does not start Kafka.
