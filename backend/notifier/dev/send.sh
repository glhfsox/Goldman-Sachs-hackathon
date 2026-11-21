#!/usr/bin/env bash
# Publishes sample records to the dev Kafka, standing in for the api (config.changes) and the processor (alerts.changes).
#   backend/notifier/dev/send.sh config.changes backend/notifier/dev/samples/config.jsonl
#   backend/notifier/dev/send.sh alerts.changes backend/notifier/dev/samples/settlements-opened.json
# A .jsonl file is one "key|record" per line; a .json file is one record keyed by its first "id".
# __CHANGE_ID__ and __NOW__ are replaced, so a sample can be sent again.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
topic="$1"
file="$2"

now="$(date -u +%Y-%m-%dT%H:%M:%S.000Z)"
substitute() { sed -e "s/__CHANGE_ID__/$(date +%s)-$$-$RANDOM/g" -e "s/__NOW__/$now/g"; }

if [[ "$file" == *.jsonl ]]; then
  lines="$(grep -v '^\s*$' "$file" | substitute)"
else
  json="$(tr -d '\n\r' < "$file" | substitute)"
  key="$(printf '%s' "$json" | grep -o '"id": *"[^"]*"' | head -1 | sed 's/.*"\([^"]*\)"$/\1/')"
  lines="$key|$json"
fi

printf '%s\n' "$lines" | docker compose -f "$here/../docker-compose.dev.yml" exec -T kafka \
  /opt/kafka/bin/kafka-console-producer.sh --bootstrap-server kafka:9092 --topic "$topic" \
  --reader-property parse.key=true --reader-property key.separator='|'
echo "sent $(printf '%s\n' "$lines" | wc -l) record(s) from $(basename "$file") to $topic"
