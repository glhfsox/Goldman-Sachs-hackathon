#!/usr/bin/env bash
# Creates the sources-cluster topics from contracts/data/sources.md. Idempotent.
# Format: <topic> <partitions>
set -euo pipefail

BOOTSTRAP="${KAFKA_BOOTSTRAP:-kafka-sources:9092}"
KAFKA_TOPICS="/opt/kafka/bin/kafka-topics.sh"

TOPICS=(
  "source.workflows 3"
  "source.trades 3"
)

for entry in "${TOPICS[@]}"; do
  read -r topic partitions <<< "$entry"
  # Replication factor 1: local single broker only.
  "$KAFKA_TOPICS" --bootstrap-server "$BOOTSTRAP" --create --if-not-exists \
    --topic "$topic" --partitions "$partitions" --replication-factor 1
done

echo "Topics ready:"
"$KAFKA_TOPICS" --bootstrap-server "$BOOTSTRAP" --list
