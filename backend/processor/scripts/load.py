"""Load test: publish N raw events to `events.raw` as ingest would, then measure the processor.

    uv run python scripts/load.py 100000 --dup 0.9 [--rate 5000]      (from backend/processor)

Events cycle through the brief's three schemas (tests/conftest.py PAYLOADS). K = N * (1 - dup) alert keys are
introduced at a constant rate over the run, key j at event j * N / K (event 0 is always new); every other event
repeats a random key already introduced (seeded, so runs are alike), so a share `dup` of the events are
duplicates and the load looks like steady state, not a burst of new alerts. Keys carry a run tag, so every run
opens fresh alerts. Prints, all measured:
- publish rate;
- processed events/s: first publish until the `processor` group's committed offsets reach the end offsets;
- ingest -> alert visible: Kafka timestamp of each `opened` record on alerts.changes minus its
  alert's first_received_at (the received_at this script stamped).
"""

import argparse
import copy
import json
import os
import random
import sys
import threading
import time
from datetime import UTC, datetime
from pathlib import Path

from confluent_kafka import ConsumerGroupTopicPartitions, Consumer, Producer, TopicPartition
from confluent_kafka.admin import AdminClient, OffsetSpec

sys.path.insert(0, str(Path(__file__).parent.parent))
from tests.conftest import PAYLOADS  # noqa: E402

BROKERS = os.environ.get("KAFKA_BROKERS", "localhost:9092")
RAW, CHANGES, GROUP = "events.raw", "alerts.changes", "processor"


def payload_for(k: int, run: str) -> tuple[str, dict]:
    """Alert key k -> (source_id, payload); the varied field is part of that rule's alert key."""
    name = ("workflow", "metric", "trade")[k % 3]
    source, p = PAYLOADS[name]
    p = copy.deepcopy(p)
    tag = f"{run}-{k}"
    if name == "workflow":
        p["error"]["code"] = tag  # settlements rule: alert_key raw.workflowName, raw.error.code
    elif name == "metric":
        p["labels"]["region"] = tag  # anna rule: default key type, service, dedup hint labels.region
    else:
        p["payload"]["reason"] = tag  # rates rule: default key type, service, desk, reason
    return source, p


def end_offsets(admin, topic: str) -> dict[int, int]:
    n = len(admin.list_topics(topic, timeout=10).topics[topic].partitions)
    futures = admin.list_offsets({TopicPartition(topic, p): OffsetSpec.latest() for p in range(n)})
    return {tp.partition: f.result().offset for tp, f in futures.items()}


def committed(admin, partitions) -> dict[int, int]:
    req = ConsumerGroupTopicPartitions(GROUP, [TopicPartition(RAW, p) for p in partitions])
    res = admin.list_consumer_group_offsets([req])[GROUP].result()
    return {tp.partition: tp.offset for tp in res.topic_partitions}


def pct(xs: list[float], q: float) -> float:
    return xs[min(len(xs) - 1, int(q * len(xs)))]


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("n", type=int)
    ap.add_argument("--dup", type=float, default=0.9, help="share of events that repeat a key (0..1)")
    ap.add_argument("--rate", type=float, default=0, help="target events/s; 0 = flat out")
    a = ap.parse_args()
    keys = max(1, round(a.n * (1 - a.dup)))
    run = f"load{int(time.time())}"
    admin = AdminClient({"bootstrap.servers": BROKERS})

    # watch alerts.changes from its current end
    obs = Consumer({"bootstrap.servers": BROKERS, "group.id": "load-observer", "enable.auto.commit": False})
    obs.assign([TopicPartition(CHANGES, p, o) for p, o in end_offsets(admin, CHANGES).items()])
    latencies, changes, stop = [], {}, threading.Event()

    def watch():
        while not stop.is_set():
            msg = obs.poll(0.2)
            if msg is None or msg.error():
                continue
            rec = json.loads(msg.value())
            if not rec["alert"]["sample_event"]["event_id"].startswith(run + "-"):
                continue
            changes[rec["change"]] = changes.get(rec["change"], 0) + 1
            if rec["change"] == "opened":
                first = datetime.fromisoformat(rec["alert"]["first_received_at"])
                latencies.append(msg.timestamp()[1] - first.timestamp() * 1000)

    t = threading.Thread(target=watch, daemon=True)
    t.start()

    prod = Producer({"bootstrap.servers": BROKERS, "linger.ms": 5, "queue.buffering.max.messages": 1_000_000})
    per_partition: dict[int, int] = {}

    def delivered(err, msg):
        if err is None:
            per_partition[msg.partition()] = per_partition.get(msg.partition(), 0) + 1

    rng, introduced = random.Random(1), 0
    start = time.time()
    for i in range(a.n):
        if a.rate and (ahead := start + i / a.rate - time.time()) > 0:
            time.sleep(ahead)
        if introduced < keys and introduced * a.n <= i * keys:  # the next key is due
            k, introduced = introduced, introduced + 1
        else:
            k = rng.randrange(introduced)
        source, payload = payload_for(k, run)
        event = {"event_id": f"{run}-{i}", "source_id": source,
                 "received_at": datetime.now(UTC).isoformat(timespec="milliseconds")[:-6] + "Z", "payload": payload}
        while True:
            try:
                prod.produce(RAW, key=event["event_id"], value=json.dumps(event), on_delivery=delivered)
                break
            except BufferError:
                prod.poll(0.05)
        prod.poll(0)
    if prod.flush(30):
        sys.exit("load: some events were not delivered")
    published = time.time() - start
    print(f"published {sum(per_partition.values())} events in {published:.2f} s = {a.n / published:.0f} events/s; "
          f"per partition {dict(sorted(per_partition.items()))}")

    target = end_offsets(admin, RAW)
    while any(committed(admin, target).get(p, -1) < o for p, o in target.items() if o > 0):
        if time.time() - start > 600:
            sys.exit(f"load: the processor did not catch up in 600 s; committed {committed(admin, target)}, end {target}")
        time.sleep(0.05)
    caught_up = time.time() - start
    print(f"processed: group caught up {caught_up:.2f} s after the first publish = {a.n / caught_up:.0f} events/s")

    deadline = time.time() + 15  # the relay may still be draining opened records
    while len(latencies) < keys and time.time() < deadline:
        time.sleep(0.1)
    stop.set()
    t.join()
    obs.close()
    lat = sorted(latencies)
    print(f"dup ratio {a.dup}, distinct alert keys {keys}, alerts opened {len(lat)}, changes {changes}")
    if lat:
        print(f"ingest -> alert visible ms: p50 {pct(lat, .5):.0f}  p95 {pct(lat, .95):.0f}  "
              f"p99 {pct(lat, .99):.0f}  max {lat[-1]:.0f}")


if __name__ == "__main__":
    main()
