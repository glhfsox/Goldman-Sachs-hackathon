#!/usr/bin/env bash
# End-to-end check of the running dev stack (docker-compose.dev.yml): config.changes and alerts.changes in,
# notification rows, mock gateway deliveries and alerts.timeline records out. Exits non-zero on the first failure.
#   docker compose -f backend/notifier/docker-compose.dev.yml up -d --build
#   bash backend/notifier/dev/e2e.sh
# Every run uses fresh alert ids, so it can be repeated against the same stack.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
compose=(docker compose -f "$here/../docker-compose.dev.yml")
gateway="${MOCK_GATEWAY_URL:-http://localhost:8002}"
run="e2e$(date +%s)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

sql() { "${compose[@]}" exec -T notifier-db psql -U alerts -d notifier -tAc "$1"; }
fail() { echo "FAIL: $*" >&2; exit 1; }
# wait_for <seconds> <description> <sql returning one value> <expected value>
wait_for() {
  local deadline=$((SECONDS + $1)) got=""
  while (( SECONDS < deadline )); do
    got="$(sql "$3" 2>/dev/null || true)"
    [[ "$got" == "$4" ]] && { echo "ok: $2"; return 0; }
    sleep 0.5
  done
  fail "$2: expected '$4', got '$got' after $1 s"
}
# sample <file> <alert id in the file>: a copy with this run's alert and event ids. The event id is the
# group key of "one message per person per incident", so a reused one suppresses the next run's pushes.
sample() { sed -e "s/$2/$2-$run/g" -e "s/\"evt-\([^\"]*\)\"/\"evt-\1-$run\"/g" "$here/samples/$1" > "$tmp/$1"; echo "$tmp/$1"; }

bash "$here/send.sh" config.changes "$here/samples/config.jsonl" >/dev/null
wait_for 30 "config copies" "select count(*) from rules where id in ('settlements-workflow-failed','rates-large-rejections','anna-payments-critical')" 3

# Critical: call + sms + ui to the on-call person at once, through the mock gateway.
alert="alert-settlements-1-$run"
bash "$here/send.sh" alerts.changes "$(sample settlements-opened.json alert-settlements-1)" >/dev/null
wait_for 30 "settlements: call, sms, ui sent" \
  "select string_agg(channel || ':' || status, ',' order by channel) from notifications where alert_id = '$alert'" \
  "call:sent,sms:sent,ui:sent"
for channel in sms call; do
  id="$(sql "select id from notifications where alert_id = '$alert' and channel = '$channel'")"
  curl -fsS "$gateway/deliveries?channel=$channel&limit=1000" | grep -q "\"$id\"" \
    || fail "mock gateway has no $channel delivery for notification $id"
  echo "ok: mock gateway logged the $channel"
done
wait_for 30 "settlements: 3 timeline records published" \
  "select count(*) from outbox where key = '$alert' and topic = 'alerts.timeline' and processed_at is not null" 3
wait_for 5 "settlements: no row reports send_at after sent_at" \
  "select count(*) from notifications where alert_id = '$alert' and send_at > sent_at" 0

# A second version of a runbook replaces the first copy.
runbook="e2e-runbook-$run"
for version in 1 2; do
  printf 'runbook:%s|{"kind":"runbook","id":"%s","updated_at":"%s","deleted":false,"body":{"id":"%s","team_id":"settlements","title":"v%s","status":"active","version":%s,"canonical":{"steps":[]}}}\n' \
    "$runbook" "$runbook" "$(date -u +%Y-%m-%dT%H:%M:%S.00${version}Z)" "$runbook" "$version" "$version" >> "$tmp/runbook.jsonl"
done
bash "$here/send.sh" config.changes "$tmp/runbook.jsonl" >/dev/null
wait_for 30 "runbook update applied" "select version from runbooks where id = '$runbook'" 2

# Warning: UI at once, email waits for the push delay; the ack cancels it.
alert="alert-rates-1-$run"
bash "$here/send.sh" alerts.changes "$(sample rates-warning-opened.json alert-rates-1)" >/dev/null
wait_for 30 "rates: ui sent, email pending" \
  "select string_agg(channel || ':' || status, ',' order by channel) from notifications where alert_id = '$alert'" \
  "email:pending,ui:sent"
bash "$here/send.sh" alerts.changes "$(sample rates-acked.json alert-rates-1)" >/dev/null
wait_for 30 "rates: ack cancels the email" \
  "select string_agg(channel || ':' || status, ',' order by channel) from notifications where alert_id = '$alert'" \
  "email:cancelled,ui:sent"

echo "PASS ($run)"
