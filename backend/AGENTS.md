# AGENTS.md — backend

Rules of the root `AGENTS.md` apply. Backend-specific notes:

## Run Kafka locally
The shared Kafka cluster lives in `backend/kafka/` ([README](kafka/README.md)). Do not write your own Kafka container.
Add it to your own compose file and run only what you need:

```yaml
include:
  - path: ../kafka/docker-compose.yml   # adjust the relative path
services:
  my-service:
    depends_on:
      kafka-init:
        condition: service_completed_successfully   # topics exist
```

- Inside compose use `kafka:9092`. From the host use `localhost:9092`.
- Run the standalone Kafka compose or an including one, not both: they use the same host port.
- Topics come from `contracts/services.md`. A new topic goes there and in `backend/kafka/create-topics.sh` in the same PR (the `check.sh` there enforces it).
