#!/usr/bin/env bash
# Sends test events to the sources cluster until the simulator exists.
#   bash produce.sh                     one example event to each topic
#   bash produce.sh source.trades '{"meta": {...}, "payload": {...}}'    your own JSON (one line) to a topic
# Needs the broker running (docker compose up -d). Payloads: contracts/data/sources.md §4.
set -euo pipefail
cd "$(dirname "$0")"

send() {  # <topic> <key> <value>
  printf '%s\t%s\n' "$2" "$3" | docker compose exec -T kafka-sources /opt/kafka/bin/kafka-console-producer.sh \
    --bootstrap-server kafka-sources:9092 --topic "$1" \
    --property parse.key=true --property key.separator=$'\t' > /dev/null
  echo "sent to $1 (key $2)"
}

if [ $# -ge 2 ]; then
  send "$1" "manual" "$2"
  exit 0
fi

send source.workflows WF-20261120-0042 \
  '{"type": "WORKFLOW_FAILED", "workflowId": "WF-20261120-0042", "workflowName": "order-settlement", "step": "settle", "severity": "critical", "environment": "prod", "timestamp": "2026-11-20T10:15:01Z", "error": {"code": "TIMEOUT", "message": "Counterparty service did not respond in 5000 ms"}}'
send source.trades T-88121 \
  '{"meta": {"schema": "trade.rejected.v1", "producer": "trade-gateway", "ts": 1795169702000}, "payload": {"tradeId": "T-88121", "desk": "rates", "notional": 25000000, "currency": "EUR", "reason": "LIMIT_BREACH"}}'
