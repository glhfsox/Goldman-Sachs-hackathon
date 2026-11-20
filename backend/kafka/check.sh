#!/usr/bin/env bash
# Fast checks: the compose file is valid and create-topics.sh creates exactly the topics
# that contracts/services.md lists for the internal `kafka` cluster. Does not start Kafka.
set -euo pipefail
cd "$(dirname "$0")"

if command -v docker > /dev/null 2>&1; then
  docker compose -f docker-compose.yml config -q
else
  echo "docker not found: skipping compose validation"
fi

bash -n create-topics.sh

python3 - <<'PY'
import re
import sys

script = open("create-topics.sh").read()
created = set(re.findall(r'^\s+"([a-z][a-z0-9._-]*) \d+', script, re.M))

contract = open("../../contracts/services.md").read()
section = contract.split("## Kafka topics", 1)[1].split("\n## ", 1)[0]
expected = set()
for line in section.splitlines():
    cells = [cell.strip() for cell in line.strip().strip("|").split("|")]
    # Row: | topic | cluster | key | ... ; keep rows on the internal `kafka` cluster.
    if len(cells) > 2 and cells[1] == "`kafka`":
        expected.add(re.sub(r"\s*\(.*\)", "", cells[0]).strip("`"))

if created != expected:
    print(f"create-topics.sh: {sorted(created)}\ncontracts/services.md: {sorted(expected)}")
    sys.exit("topic lists differ")
print(f"ok: {len(created)} topics match the contract")
PY
