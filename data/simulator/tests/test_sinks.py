"""Sender behaviour of contracts/data/sources.md §2 (HTTP) and §3 (Kafka), against fakes."""

import asyncio

import httpx

from sim.model import Event
from sim.sinks import MAX_ATTEMPTS, HttpSink, KafkaSink, Stats

EVENT = Event("alertmanager-http", "http", "metric_alert", b'{"a": 1}', "application/json")


async def no_sleep(_):
    return None


def run_http(responses):
    """Send EVENT once; the fake ingest answers with `responses` in order (an exception = network error)."""
    calls = []

    def handler(request: httpx.Request):
        calls.append(request)
        r = responses[min(len(calls), len(responses)) - 1]
        if isinstance(r, Exception):
            raise r
        return httpx.Response(r, json={"event_id": "e1"} if r == 202 else None)

    async def go():
        stats = Stats()
        sink = HttpSink(stats, "http://ingest:8001", transport=httpx.MockTransport(handler), sleep=no_sleep)
        await sink.send(EVENT)
        await sink.close()
        return stats.by_source["alertmanager-http"]

    return asyncio.run(go()), calls


def test_http_posts_one_event_to_its_source_path():
    c, calls = run_http([202])
    assert (c.sent, c.failed, c.retries) == (1, 0, 0)
    req = calls[0]
    assert req.method == "POST" and str(req.url) == "http://ingest:8001/ingest/alertmanager-http"
    assert req.headers["content-type"] == "application/json" and req.content == b'{"a": 1}'


def test_http_retries_503_then_succeeds():
    c, calls = run_http([503, 503, 202])
    assert (c.sent, c.failed, c.retries, len(calls)) == (1, 0, 2, 3)


def test_http_does_not_retry_404_or_other_errors():
    for status in (404, 400, 500):
        c, calls = run_http([status])
        assert (c.sent, c.failed, len(calls)) == (0, 1, 1), status


def test_http_gives_up_after_max_attempts_on_network_errors():
    c, calls = run_http([httpx.ConnectError("down")])
    assert (c.sent, c.failed, c.retries, len(calls)) == (0, 1, MAX_ATTEMPTS - 1, MAX_ATTEMPTS)
    c, calls = run_http([httpx.ReadTimeout("slow")])
    assert (c.failed, len(calls)) == (1, MAX_ATTEMPTS)


class FakeProducer:
    def __init__(self, config):
        self.config, self.sent, self.pending = config, [], []

    def produce(self, topic, value, key, on_delivery):
        self.sent.append((topic, key, value))
        self.pending.append(on_delivery)

    def poll(self, timeout):
        return 0

    def flush(self, timeout):
        for cb in self.pending:
            cb(None, None)
        self.pending = []
        return 0


def test_kafka_publishes_value_and_key_with_idempotent_producer():
    stats = Stats()
    sink = KafkaSink(stats, "localhost:9094", producer_factory=FakeProducer)
    ev = Event("workflows-kafka", "kafka", "workflow_event", b'{"x": 1}', "application/json",
               topic="source.workflows", key="WF-1")

    async def go():
        await sink.send(ev)
        await sink.close()

    asyncio.run(go())
    assert sink.producer.sent == [("source.workflows", b"WF-1", b'{"x": 1}')]
    assert sink.producer.config["enable.idempotence"] is True and sink.producer.config["acks"] == "all"
    assert stats.by_source["workflows-kafka"].sent == 1
