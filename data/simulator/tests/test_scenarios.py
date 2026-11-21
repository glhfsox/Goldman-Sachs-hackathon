"""Scenarios load, and the demo tells the story of data/README.md (played on a fake clock)."""

import asyncio
import re

import pytest

from sim.model import load_sources
from sim.run import list_scenarios, load_scenario, run_scenario, total_rate
from sim.sinks import Router, Stats
from tests.conftest import Collect, FakeClock


def play(name, **kw):
    stats, clock = Stats(), FakeClock()
    sink = Collect(stats)
    result = asyncio.run(run_scenario(load_scenario(name), load_sources(), Router({"kafka": sink, "http": sink}),
                                      stats, progress=False, clock=clock, sleep=clock.sleep, **kw))
    return sink.events, result


@pytest.mark.parametrize("name", ["demo", "steady", "burst", "load", "edge"])
def test_scenarios_exist_and_load(name):
    assert name in list_scenarios()
    load_scenario(name)


def test_demo_tells_the_story():
    events, result = play("demo")
    assert 200 < result.elapsed_s < 230 and not result.stopped
    payloads = [(e.schema, e.payload()) for e in events]

    storm = [p for s, p in payloads if s == "workflow_event" and p.get("type") == "WORKFLOW_FAILED"]
    assert 400 < len(storm) < 600  # 8/s for 40 s + 4/s for 30 s + 2/s for 30 s
    assert all(p["workflowName"] == "order-settlement" and p["error"]["code"] == "TIMEOUT"
               and p["environment"] == "prod" and p["severity"] == "critical" for p in storm)

    big = [p for s, p in payloads if s == "trade_event" and p["payload"].get("notional") == 25_000_000]
    assert len(big) == 1 and big[0]["payload"]["tradeId"] == "T-88121"
    assert big[0]["meta"]["schema"] == "trade.rejected.v1" and big[0]["payload"]["desk"] == "rates"
    small_rates_rejections = [p for s, p in payloads if s == "trade_event" and p["meta"]["schema"] == "trade.rejected.v1"
                              and p["payload"]["desk"] == "rates" and p["payload"]["notional"] <= 10_000_000]
    assert len(small_rates_rejections) == 3

    pricing = [p for s, p in payloads if s == "log_record" and isinstance(p, dict) and p["level"] == "ERROR"]
    assert {p["instrument"] for p in pricing} == {"EURUSD", "UST10Y"}
    assert all(p["latency_ms"] >= 1000 for p in pricing)

    assert any(s == "k8s_event" for s, _ in payloads)
    assert any(s == "log_record" and isinstance(p, str) for s, p in payloads)  # raw line in phase 7

    resolved = [p for s, p in payloads if s == "metric_alert" and p["status"] == "resolved"]
    assert {(p["alertname"], p["labels"]["service"]) for p in resolved} == {
        ("HighErrorRate", "payments-api"), ("HighLatency", "counterparty-service")}
    assert all("endsAt" in p for p in resolved)
    recovered = [p for s, p in payloads if s == "workflow_event" and p["type"] == "WORKFLOW_RECOVERED"]
    assert len(recovered) == 3 and all("error" not in p for p in recovered)

    # background never pages anyone: nothing critical outside the storm and the payments alert
    critical_other = [p for s, p in payloads if s == "workflow_event" and p.get("severity") == "critical"
                      and p.get("workflowName") != "order-settlement"]
    assert critical_other == []


def test_demo_is_reproducible():
    a, _ = play("demo", max_duration_s=60)
    b, _ = play("demo", max_duration_s=60)
    assert len(a) > 100 and masked(a) == masked(b)


TIMESTAMPS = re.compile(rb"\d{4}-\d\d-\d\dT[\d:.]+Z|\b\d{13}\b|WF-\d{8}-")


def masked(events):
    """Events with wall-clock parts blanked: only those may differ between two runs with one seed."""
    return [(e.source_id, e.key and TIMESTAMPS.sub(b"T", e.key.encode()), TIMESTAMPS.sub(b"T", e.body))
            for e in events]


def test_burst_is_500_per_minute():
    events, _ = play("burst")
    storm = [e for e in events if e.schema == "workflow_event"]
    assert 450 < len(storm) < 550


def test_load_rate_and_transport_mix():
    scenario = load_scenario("load")
    assert total_rate(scenario) == 1000
    events, _ = play("load", max_duration_s=5)
    assert 4500 < len(events) < 5500
    kafka_share = sum(e.kind == "kafka" for e in events) / len(events)
    assert 0.85 < kafka_share < 0.95


def test_rate_scale_and_only_kind():
    events, _ = play("steady", max_duration_s=10, rate_scale=2, only_kind="http")
    assert all(e.kind == "http" for e in events)
    assert 300 < len(events) < 500  # (2 + 18) * 2 * 10


def test_edge_cases_are_all_sent():
    events, _ = play("edge")
    assert sum(e.source_id == "no-such-source" for e in events) == 3
    invalid = [e for e in events if e.content_type == "application/json" and isinstance(e.payload(), str)]
    assert {e.kind for e in invalid} == {"http", "kafka"} and len(invalid) == 6
    assert sum(e.schema == "k8s_event" for e in events) == 3
    bodies = [e.body for e in events if e.schema == "workflow_event" and isinstance(e.payload(), dict)]
    assert max(bodies.count(b) for b in bodies) == 50
    assert any("日本円" in e.body.decode() for e in events)
