#!/usr/bin/env bash
# Runs the Postman collection against a running Ingest with newman.
# Starts `docker compose up` itself when Ingest does not answer, and stops it again when it did.
# Usage: ./run.sh            (BASE_URL=http://localhost:8001 by default)
#        KEEP_UP=1 ./run.sh  (leave the stack running)
set -euo pipefail
cd "$(dirname "$0")/.."

BASE_URL="${BASE_URL:-http://localhost:8001}"
started=0

healthy() { curl -fsS --max-time 2 "$BASE_URL/health" > /dev/null 2>&1; }

if ! healthy; then
  echo "Ingest is not running at $BASE_URL: starting docker compose..."
  docker compose up -d --build
  started=1
  for _ in $(seq 1 90); do
    healthy && break
    sleep 2
  done
  healthy || { echo "Ingest did not become healthy within 180 s"; docker compose logs ingest | tail -30; exit 1; }
fi

cleanup() {
  if [ "$started" = "1" ] && [ "${KEEP_UP:-0}" != "1" ]; then
    docker compose down
  fi
}
trap cleanup EXIT

# Pinned, so everyone runs the same newman.
npx --yes newman@6.2.1 run postman/ingest.postman_collection.json --env-var "baseUrl=$BASE_URL"
