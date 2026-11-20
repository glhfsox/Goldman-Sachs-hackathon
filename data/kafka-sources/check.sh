#!/usr/bin/env bash
# Fast checks: the compose file is valid and create-topics.sh creates exactly the source.* topics
# that contracts/data/sources.md registers. Does not start Kafka.
set -euo pipefail
cd "$(dirname "$0")"

if command -v docker > /dev/null 2>&1; then
  docker compose -f docker-compose.yml config -q
else
  echo "docker not found: skipping compose validation"
fi

bash -n create-topics.sh
bash -n produce.sh

python3 - <<'PY'
import re
import sys

script = open("create-topics.sh").read()
created = set(re.findall(r'^\s+"(source\.[a-z0-9._-]+) \d+"', script, re.M))

contract = open("../../contracts/data/sources.md").read()
registry = contract.split("## 1. Source registry", 1)[1].split("\n## ", 1)[0]
expected = set(re.findall(r'"topic": "(source\.[a-z0-9._-]+)"', registry))

if not expected or created != expected:
    print(f"create-topics.sh: {sorted(created)}\ncontracts/data/sources.md: {sorted(expected)}")
    sys.exit("topic lists differ")
print(f"ok: {len(created)} topics match the contract")
PY
