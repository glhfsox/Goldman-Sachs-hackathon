#!/usr/bin/env bash
# Creates the internal-cluster topics from contracts/services.md. Idempotent.
# Format: <topic> <partitions> [topic config ...]
# Partitions are the unit of scale: a consumer group runs at most one consumer per partition.
# They can be raised later but never lowered, and raising them re-maps keys, so keep headroom.
set -euo pipefail

BOOTSTRAP="${KAFKA_BOOTSTRAP:-kafka:9092}"
KAFKA_TOPICS="/opt/kafka/bin/kafka-topics.sh"

TOPICS=(
  "events.raw 6"
  "alerts.changes 6"
  "config.changes 1 cleanup.policy=compact"
  "alerts.timeline 3"
  "schema.samples 3"
)

for entry in "${TOPICS[@]}"; do
  read -r -a parts <<< "$entry"
  topic="${parts[0]}"
  partitions="${parts[1]}"
  config_args=()
  for config in "${parts[@]:2}"; do
    config_args+=(--config "$config")
  done

  # Replication factor 1: local single broker only.
  "$KAFKA_TOPICS" --bootstrap-server "$BOOTSTRAP" --create --if-not-exists \
    --topic "$topic" --partitions "$partitions" --replication-factor 1 "${config_args[@]}"
done

echo "Topics ready:"
"$KAFKA_TOPICS" --bootstrap-server "$BOOTSTRAP" --list
