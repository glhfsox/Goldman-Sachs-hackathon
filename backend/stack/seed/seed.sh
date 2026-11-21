#!/bin/sh
# Loads the brief's mappings, rules and runbook through the api (contracts/api.md), so events flow end to end
# without the ai-worker. Every call is create-or-replace, so running it again changes nothing.
set -eu
API="${API_URL:-http://api:8000/api}"
DIR="$(dirname "$0")"
OUT=/tmp/seed-response

tries=0
until curl -fs -o /dev/null "$API/teams"; do
  tries=$((tries + 1))
  [ "$tries" -ge 90 ] && { echo "seed: the api did not answer at $API" >&2; exit 1; }
  sleep 2
done

# call METHOD PATH [FILE] -> prints the HTTP status, keeps the body in $OUT
call() {
  if [ -n "${3:-}" ]; then
    curl -sS -o "$OUT" -w '%{http_code}' -X "$1" -H 'Content-Type: application/json' --data-binary "@$3" "$API$2"
  else
    curl -sS -o "$OUT" -w '%{http_code}' -X "$1" "$API$2"
  fi
}

ok() { echo "seed: $1 $2"; }
fail() { echo "seed: $1 failed with $2: $(cat "$OUT")" >&2; exit 1; }

# Runbooks first: a rule names its runbook. Stored as the team wrote it (YAML), with the canonical form
# saved next to it, so the demo does not need a Claude key.
for f in "$DIR"/runbooks/*.canonical.json; do
  id=$(basename "$f" .canonical.json)
  if [ "$(call GET "/runbooks/$id")" = 404 ]; then
    # {team_id, title, format, source}; the api makes the id from the title
    s=$(call POST /runbooks "$DIR/runbooks/$id.draft.json"); [ "$s" = 201 ] || [ "$s" = 200 ] || fail "runbook $id" "$s"
  fi
  printf '{"canonical":%s,"status":"active"}' "$(cat "$f")" > /tmp/runbook.json
  s=$(call PUT "/runbooks/$id" /tmp/runbook.json); [ "$s" = 200 ] || fail "runbook $id" "$s"
  ok runbook "$id"
done

for f in "$DIR"/mappings/*.json; do
  id=$(basename "$f" .json)
  s=$(call PUT "/mappings/$id" "$f"); [ "$s" = 200 ] || [ "$s" = 201 ] || fail "mapping $id" "$s"
  ok mapping "$id"
done

for f in "$DIR"/rules/*.json; do
  id=$(basename "$f" .json)
  s=$(call POST /rules "$f")
  [ "$s" = 409 ] && s=$(call PUT "/rules/$id" "$f")
  [ "$s" = 200 ] || [ "$s" = 201 ] || fail "rule $id" "$s"
  ok rule "$id"
done

echo "seed: done"
