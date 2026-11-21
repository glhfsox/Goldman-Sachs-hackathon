#!/usr/bin/env bash
# Runs the Postman collection against a running API with newman.
# Starts `docker compose up` itself when the API does not answer, and stops it again when it did.
# Before the run it puts one alert and two schema samples on Kafka, the way the processor does,
# because only Kafka can create them (ids are unique per run, so the script can run again).
# Usage: ./run.sh            (BASE_URL=http://localhost:8000 by default)
#        KEEP_UP=1 ./run.sh  (leave the stack running)
set -euo pipefail
cd "$(dirname "$0")/.."

BASE_URL="${BASE_URL:-http://localhost:8000}"
started=0

healthy() { curl -fsS --max-time 2 "$BASE_URL/health" > /dev/null 2>&1; }

if ! healthy; then
  echo "API is not running at $BASE_URL: starting docker compose..."
  docker compose up -d --build
  started=1
  for _ in $(seq 1 90); do
    healthy && break
    sleep 2
  done
  healthy || { echo "API did not become healthy within 180 s"; docker compose logs api | tail -30; exit 1; }
fi

cleanup() {
  if [ "$started" = "1" ] && [ "${KEEP_UP:-0}" != "1" ]; then
    docker compose down -v
  fi
}
trap cleanup EXIT

run="$(date +%s)"
alert_id="pm-alert-$run"
fp_mapped="pm-fp-mapped-$run"
fp_failed="pm-fp-failed-$run"

# Key and value are separated by "|", the key is the record key (contracts/events.md).
produce() {
  docker compose exec -T kafka /opt/kafka/bin/kafka-console-producer.sh --bootstrap-server kafka:9092 \
    --topic "$1" --reader-property parse.key=true --reader-property 'key.separator=|'
}

echo "{\"change_id\":\"pm-change-$run\",\"change\":\"opened\",\"at\":\"2026-11-20T10:15:02.180Z\",\"actor\":\"processor\",\"alert\":{\"id\":\"$alert_id\",\"rule_id\":\"settlements-workflow-failed\",\"team_id\":\"settlements\",\"incident_id\":null,\"status\":\"open\",\"severity\":\"critical\",\"title\":\"order-settlement failed at step settle\",\"body\":\"Counterparty service did not respond in 5000 ms\",\"fields\":{\"step\":\"settle\"},\"color\":\"red\",\"count\":1,\"first_seen\":\"2026-11-20T10:15:01.000Z\",\"last_seen\":\"2026-11-20T10:15:01.000Z\",\"first_received_at\":\"2026-11-20T10:15:02.123Z\",\"runbook_id\":null,\"acked_by\":null,\"resolved_by\":null,\"sample_event\":{\"type\":\"WORKFLOW_FAILED\"}}}" \
  | sed "s/^/$alert_id|/" | produce alerts.changes

for fp in "$fp_mapped" "$fp_failed"; do
  echo "{\"fingerprint\":\"$fp\",\"source_id\":\"trades-kafka\",\"seen_at\":\"2026-11-20T10:15:02.123Z\",\"sample\":{\"meta\":{\"schema\":\"trade.rejected.v1\"}}}" \
    | sed "s/^/$fp|/" | produce schema.samples
done

# The API reads Kafka in the background: wait until it has stored all three.
for _ in $(seq 1 60); do
  if curl -fsS "$BASE_URL/api/alerts/$alert_id" > /dev/null 2>&1 \
    && curl -fsS "$BASE_URL/api/schema-samples?status=pending" | grep -q "$fp_failed" \
    && curl -fsS "$BASE_URL/api/schema-samples?status=pending" | grep -q "$fp_mapped"; then
    ready=1
    break
  fi
  sleep 1
done
[ "${ready:-0}" = "1" ] || { echo "The API did not store the Kafka records within 60 s"; docker compose logs api | tail -30; exit 1; }

# Pinned, so everyone runs the same newman.
npx --yes newman@6.2.1 run postman/api.postman_collection.json \
  --env-var "baseUrl=$BASE_URL" --env-var "alertId=$alert_id" \
  --env-var "fingerprintMapped=$fp_mapped" --env-var "fingerprintFailed=$fp_failed"
