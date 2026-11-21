"""Sinks: where events go. One class per transport; the runner picks one by the source's `kind`.

Adding a transport (e.g. a real vendor SDK) = one class with `send` and `close`, plus its kind in SINKS.
Sender behaviour follows contracts/data/sources.md §2 (HTTP) and §3 (Kafka).
"""

from __future__ import annotations

import asyncio
import json
import random
import sys
from collections import defaultdict
from collections.abc import Callable
from dataclasses import dataclass, field
from typing import TextIO

import httpx

from .model import Event


@dataclass
class Counter:
    sent: int = 0  # confirmed by the receiver (202 / Kafka ack)
    failed: int = 0  # given up on
    retries: int = 0


@dataclass
class Stats:
    by_source: dict[str, Counter] = field(default_factory=lambda: defaultdict(Counter))
    errors_logged: int = 0

    def ok(self, sid: str) -> None:
        self.by_source[sid].sent += 1

    def retry(self, sid: str) -> None:
        self.by_source[sid].retries += 1

    def fail(self, sid: str, why: str) -> None:
        self.by_source[sid].failed += 1
        if self.errors_logged < 20:  # enough to see what is wrong without flooding the terminal
            print(f"[sim] {sid}: gave up: {why}", file=sys.stderr)
            self.errors_logged += 1

    def totals(self) -> Counter:
        t = Counter()
        for c in self.by_source.values():
            t.sent += c.sent
            t.failed += c.failed
            t.retries += c.retries
        return t


class StdoutSink:
    """Prints one JSON line per event instead of sending it (dry runs, piping into files, tests)."""

    def __init__(self, stats: Stats, out: TextIO = sys.stdout) -> None:
        self.stats, self.out = stats, out

    async def send(self, event: Event) -> None:
        record = {"source_id": event.source_id, "kind": event.kind, "schema": event.schema,
                  "topic": event.topic, "key": event.key, "content_type": event.content_type,
                  "body": event.body.decode("utf-8")}
        self.out.write(json.dumps(record, ensure_ascii=False) + "\n")
        self.stats.ok(event.source_id)

    async def close(self) -> None:
        self.out.flush()


class KafkaSink:
    """Publishes to the sources cluster. Idempotent producer: retries inside the client never duplicate."""

    def __init__(self, stats: Stats, brokers: str, producer_factory: Callable | None = None) -> None:
        self.stats = stats
        config = {
            "bootstrap.servers": brokers,
            "acks": "all",
            "enable.idempotence": True,
            "linger.ms": 5,
            "compression.type": "lz4",
            "client.id": "sources-simulator",
        }
        if producer_factory is None:
            from confluent_kafka import Producer

            producer_factory = Producer
        self.producer = producer_factory(config)

    async def send(self, event: Event) -> None:
        def delivered(err, msg, sid=event.source_id):
            if err is None:
                self.stats.ok(sid)
            else:
                self.stats.fail(sid, f"kafka: {err}")

        key = event.key.encode("utf-8") if event.key is not None else None
        while True:
            try:
                self.producer.produce(event.topic, value=event.body, key=key, on_delivery=delivered)
                break
            except BufferError:  # local queue full: let deliveries drain, then try again
                self.producer.poll(0.05)
                await asyncio.sleep(0)
        self.producer.poll(0)

    async def close(self) -> None:
        left = self.producer.flush(30)
        if left:
            print(f"[sim] kafka: {left} events still unconfirmed after 30 s", file=sys.stderr)


RETRYABLE_STATUS = {503}
MAX_ATTEMPTS = 6
BACKOFF_FIRST_S, BACKOFF_MAX_S = 0.2, 5.0


class HttpSink:
    """POSTs one event per request to {INGEST_URL}/ingest/{source_id}, up to `concurrency` at once."""

    def __init__(self, stats: Stats, base_url: str, concurrency: int = 64, timeout_s: float = 5.0,
                 transport: httpx.AsyncBaseTransport | None = None, sleep=asyncio.sleep) -> None:
        self.stats = stats
        self.client = httpx.AsyncClient(
            base_url=base_url.rstrip("/"), timeout=timeout_s, transport=transport,
            limits=httpx.Limits(max_connections=concurrency, max_keepalive_connections=concurrency),
        )
        self.slots = asyncio.Semaphore(concurrency)
        self.tasks: set[asyncio.Task] = set()
        self.sleep = sleep
        self.rng = random.Random()

    async def send(self, event: Event) -> None:
        await self.slots.acquire()  # back-pressure: the runner slows down instead of piling up requests
        task = asyncio.create_task(self._deliver(event))
        self.tasks.add(task)
        task.add_done_callback(self._done)

    def _done(self, task: asyncio.Task) -> None:
        self.tasks.discard(task)
        self.slots.release()

    async def _deliver(self, event: Event) -> None:
        sid = event.source_id
        for attempt in range(1, MAX_ATTEMPTS + 1):
            try:
                r = await self.client.post(f"/ingest/{sid}", content=event.body,
                                           headers={"Content-Type": event.content_type})
            except (httpx.TimeoutException, httpx.TransportError) as e:
                why = f"{type(e).__name__}"
            else:
                if 200 <= r.status_code < 300:
                    self.stats.ok(sid)
                    return
                if r.status_code not in RETRYABLE_STATUS:  # 404 and every other answer: no retry
                    self.stats.fail(sid, f"HTTP {r.status_code}")
                    return
                why = f"HTTP {r.status_code}"
            if attempt == MAX_ATTEMPTS:
                self.stats.fail(sid, f"{why} after {MAX_ATTEMPTS} attempts")
                return
            self.stats.retry(sid)
            delay = min(BACKOFF_MAX_S, BACKOFF_FIRST_S * 2 ** (attempt - 1))
            await self.sleep(delay * self.rng.uniform(0.8, 1.2))

    async def close(self) -> None:
        if self.tasks:
            await asyncio.gather(*list(self.tasks), return_exceptions=True)
        await self.client.aclose()


class Router:
    """Sends each event to the sink of its source's kind."""

    def __init__(self, sinks: dict[str, object]) -> None:
        self.sinks = sinks

    async def send(self, event: Event) -> None:
        sink = self.sinks.get(event.kind)
        if sink is None:
            raise ValueError(f"no sink for kind {event.kind!r} (source {event.source_id})")
        await sink.send(event)

    async def close(self) -> None:
        for sink in {id(s): s for s in self.sinks.values()}.values():
            await sink.close()
