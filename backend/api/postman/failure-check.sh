#!/usr/bin/env bash
# Failure check: Kafka goes down, the API must keep working and lose nothing.
#   1. stop Kafka
#   2. POST a rule: still 201, because the change goes to the outbox table, not straight to Kafka
#   3. /health and the read endpoints still answer
#   4. start Kafka: the outbox publisher sends the waiting record to config.changes, no record is lost
# Starts `docker compose up` itself when the API does not answer, and stops it again when it did.
# Usage: ./postman/failure-check.sh      (BASE_URL=http://localhost:8000 by default)
set -euo pipefail
cd "$(dirname "$0")/.."

BASE_URL="${BASE_URL:-http://localhost:8000}"
started=0

healthy() { curl -fsS --max-time 2 "$BASE_URL/health" > /dev/null 2>&1; }
fail() { echo "FAIL: $*"; exit 1; }
step() { echo "== $*"; }

if ! healthy; then
  echo "API is not running at $BASE_URL: starting docker compose..."
  docker compose up -d --build
  started=1
  for _ in $(seq 1 90); do
    healthy && break
    sleep 2
  done
  healthy || fail "API did not become healthy within 180 s"
fi

cleanup() {
  docker compose start kafka > /dev/null 2>&1 || true
  if [ "$started" = "1" ]; then
    docker compose down -v > /dev/null 2>&1
  fi
}
trap cleanup EXIT

id="fc-rule-$(date +%s)"
body="{\"id\":\"$id\",\"team_id\":\"rates\",\"when\":{\"all\":[{\"field\":\"type\",\"op\":\"glob\",\"value\":\"trade.rejected*\"}]},\"route\":{\"recipients\":[{\"team\":\"rates\"}],\"channels\":[\"ui\"]}}"

step "1. stop Kafka"
docker compose stop kafka > /dev/null

step "2. POST a rule while Kafka is down"
code="$(curl -s -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' -d "$body" "$BASE_URL/api/rules")"
[ "$code" = "201" ] || fail "POST /api/rules returned $code, expected 201"
echo "POST answered $code"

step "3. the API still answers"
curl -fsS --max-time 5 "$BASE_URL/health" > /dev/null || fail "/health failed with Kafka down"
curl -fsS --max-time 5 "$BASE_URL/api/rules?team=rates" | grep -q "$id" || fail "the rule was not stored"
curl -fsS --max-time 5 "$BASE_URL/api/alerts" > /dev/null || fail "GET /api/alerts failed with Kafka down"
echo "health, rules and alerts answer; the rule is stored"

step "4. start Kafka again"
docker compose start kafka > /dev/null
for _ in $(seq 1 60); do
  docker compose exec -T kafka /opt/kafka/bin/kafka-broker-api-versions.sh --bootstrap-server kafka:9092 > /dev/null 2>&1 && break
  sleep 2
done

step "5. the record reaches config.changes"
for _ in $(seq 1 60); do
  records="$(docker compose exec -T kafka /opt/kafka/bin/kafka-console-consumer.sh --bootstrap-server kafka:9092 \
    --topic config.changes --from-beginning --timeout-ms 5000 --property print.key=true 2>/dev/null || true)"
  if grep -q "rule:$id" <<< "$records"; then
    echo "found record with key rule:$id"
    echo "PASS"
    exit 0
  fi
  sleep 2
done
fail "no record with key rule:$id on config.changes after Kafka came back"
