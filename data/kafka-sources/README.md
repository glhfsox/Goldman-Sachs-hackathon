# Sources Kafka cluster (local)

One Kafka broker (KRaft, no ZooKeeper) playing the third parties' cluster: they publish `source.*` topics here,
and only ingest reads it. Separate from the internal cluster in [backend/kafka/](../../backend/kafka/README.md).
What goes on the topics: [contracts/data/sources.md](../../contracts/data/sources.md) §3–§4.

## Run

```bash
cd data/kafka-sources
docker compose up -d        # starts the broker and creates the topics
bash produce.sh             # sends one example event to each topic
docker compose down -v      # stops it and deletes the data
```

| From | Address (`SOURCE_KAFKA_BROKERS`) |
|---|---|
| Another container in the same compose | `kafka-sources:9092` |
| Your machine | `localhost:9094` |

It runs fine next to `backend/kafka` (port 9092): different container names, port, volume and cluster id.

## Use it from your own compose file

```yaml
include:
  - path: ../kafka/docker-compose.yml                    # internal cluster
  - path: ../../data/kafka-sources/docker-compose.yml    # sources cluster (adjust the path)
services:
  ingest:
    environment:
      KAFKA_BROKERS: kafka:9092
      SOURCE_KAFKA_BROKERS: kafka-sources:9092
    depends_on:
      kafka-init:
        condition: service_completed_successfully
      kafka-sources-init:
        condition: service_completed_successfully
```

Do not run this compose and an including one at the same time (same host port 9094).

## Topics

Created by `kafka-sources-init` from `create-topics.sh`; safe to run again. Auto-creation is off.

| Topic | Partitions | Key | Source id |
|---|---|---|---|
| `source.workflows` | 3 | `workflowId` | `workflows-kafka` |
| `source.trades` | 3 | `payload.tradeId` | `trades-kafka` |

## Test events until the simulator exists

```bash
bash produce.sh                                              # the two examples from the contract
bash produce.sh source.trades '{"meta": {"schema": "trade.booked.v1"}, "payload": {"tradeId": "T-1"}}'
```

Read a topic back:

```bash
docker compose exec kafka-sources /opt/kafka/bin/kafka-console-consumer.sh \
  --bootstrap-server kafka-sources:9092 --topic source.workflows --from-beginning
```

## Check

`bash check.sh` validates the compose file and that `create-topics.sh` matches the topics registered in
`contracts/data/sources.md`. It does not start Kafka.
