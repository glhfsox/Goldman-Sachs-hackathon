"""Publish the brief's mappings and rules to `config.changes` (events.md §6), standing in for the api.

    uv run python scripts/seed.py            (from backend/processor, Kafka on localhost:9092)

The data is the test fixtures' (tests/conftest.py), so tests and the real run use one copy.
"""

import json
import os
import sys
from datetime import UTC, datetime
from pathlib import Path

from confluent_kafka import Producer

sys.path.insert(0, str(Path(__file__).parent.parent))
from tests.conftest import EXAMPLE_RULES, SEED_MAPPINGS  # noqa: E402

BROKERS = os.environ.get("KAFKA_BROKERS", "localhost:9092")


def publish(records: list[tuple[str, dict]]) -> None:
    """(kind, body) pairs -> one config.changes record each, key `<kind>:<id>`, updated_at = now."""
    at = datetime.now(UTC).isoformat(timespec="milliseconds")[:-6] + "Z"
    p = Producer({"bootstrap.servers": BROKERS})
    for kind, body in records:
        record = {"kind": kind, "id": body["id"], "updated_at": at, "deleted": False, "body": body}
        p.produce("config.changes", key=f"{kind}:{body['id']}", value=json.dumps(record))
        print(f"{kind}:{body['id']}")
    if p.flush(10):
        sys.exit("seed: some records were not delivered")


if __name__ == "__main__":
    publish([("mapping", m) for m in SEED_MAPPINGS] + [("rule", r) for r in EXAMPLE_RULES])
