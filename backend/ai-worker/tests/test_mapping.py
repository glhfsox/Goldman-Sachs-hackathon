"""The mapping job against the processor's real mapping code; the model is a scripted list of tool calls."""

from types import SimpleNamespace

import pytest
from anthropic.lib.tools import ToolError

from ai_worker import mapping
from ai_worker.agent import JobFailed

# the seeded alertmanager mapping of backend/processor/tests/conftest.py: same source as the k8s event
EXISTING = [{"id": "alertmanager", "status": "active", "origin": "seed", "priority": 100,
             "match": {"all": [{"field": "source", "op": "eq", "value": "alertmanager-http"},
                               {"field": "raw.alertname", "op": "exists"}]},
             "fields": {"type": {"path": "alertname"}, "service": {"path": "labels.service"},
                        "severity": {"path": "labels.severity"}, "env": {"const": "prod"},
                        "ts": {"path": "startsAt", "format": "iso"}},
             "dedup_fields": ["labels.region"]}]

# data/simulator/schemas/k8s_event.yaml, generated once
K8S = {"apiVersion": "v1", "kind": "Event",
       "metadata": {"uid": "5d1f0c4e-8a7b-4f43-9a51-0d2b8e6c7f19", "namespace": "pricing",
                    "creationTimestamp": "2026-11-21T10:15:01Z"},
       "involvedObject": {"kind": "Pod", "name": "pricing-engine-3-7f9c4-x2k9p", "namespace": "pricing"},
       "reason": "BackOff", "message": "Back-off restarting failed container", "type": "Warning", "count": 7,
       "firstTimestamp": "2026-11-21T10:05:01Z", "lastTimestamp": "2026-11-21T10:15:01Z",
       "source": {"component": "kubelet", "host": "node-7.internal"}}
GOOD_K8S = {"id": "k8s-event",
            "match": {"all": [{"field": "source", "op": "eq", "value": "alertmanager-http"},
                              {"field": "raw.apiVersion", "op": "eq", "value": "v1"},
                              {"field": "raw.kind", "op": "eq", "value": "Event"}]},
            "fields": {"type": {"path": "reason"}, "service": {"path": "involvedObject.name"},
                       "severity": {"path": "type"}, "ts": {"path": "lastTimestamp", "format": "iso"}},
            "severity_map": {"Normal": "info"}, "dedup_fields": ["involvedObject.name", "reason"]}

REJECTED = {"meta": {"schema": "trade.rejected.v1", "producer": "trade-gateway", "ts": 1795169701123},
            "payload": {"tradeId": "T-88231", "desk": "rates", "notional": 25000000, "currency": "EUR",
                        "reason": "LIMIT_BREACH"}}
BOOKED = {"meta": {"schema": "trade.booked.v1", "producer": "trade-gateway", "ts": 1795169702000},
          "payload": {"tradeId": "T-88232", "desk": "rates", "notional": 5000000, "currency": "EUR"}}


class FakeApi:
    def __init__(self):
        self.puts = []

    def put(self, path, body):
        self.puts.append((path, body))
        return body


def run(sample, *submissions, source_id="alertmanager-http", others=()):
    """handle() with a model that calls submit_mapping once per submission; returns (result or error, tool outputs,
    api puts)."""
    outputs, api = [], FakeApi()

    def run_agent(system, user, tool):
        assert "submit_mapping" in system and f'"{source_id}"' in user
        for s in submissions:
            try:
                outputs.append(tool.call(s))
            except ToolError as e:
                outputs.append(f"ERROR {e}")

    ctx = SimpleNamespace(api=api, mappings=EXISTING, other_samples=lambda: list(others), run_agent=run_agent)
    try:
        result = mapping.handle({"fingerprint": "fp-1", "source_id": source_id, "sample": sample}, ctx)
    except JobFailed as e:
        result = e
    return result, outputs, api.puts


def test_good_k8s_mapping_is_saved_with_fields_set_by_code():
    result, outputs, puts = run(K8S, GOOD_K8S)
    assert result == {"mapping_id": "k8s-event"} and "Saved" in outputs[0]
    assert puts == [("/mappings/k8s-event?fingerprint=fp-1",
                     {**GOOD_K8S, "status": "active", "origin": "llm", "priority": 10})]


def _fields(**change):
    return {**GOOD_K8S, "fields": {**GOOD_K8S["fields"], **change}}


@pytest.mark.parametrize("submission, problem", [
    (_fields(service={"path": "involvedObject.pod"}), "fields.service (path 'involvedObject.pod') does not resolve"),
    (_fields(severity={"path": "reason"}), "fields.severity (path 'reason') is \"BackOff\", which is not on the scale"),
    (_fields(ts={"path": "count", "format": "iso"}), "fields.ts (path 'count') is 7, which does not parse"),
    ({**GOOD_K8S, "dedup_fields": ["involvedObject.uid"]}, "dedup_fields entry 'involvedObject.uid' does not resolve"),
    ({**GOOD_K8S, "match": {"all": [{"field": "source", "op": "eq", "value": "alertmanager-http"}]}},
     "match is true even for an empty payload"),
    ({**GOOD_K8S, "id": "alertmanager"}, "id 'alertmanager' belongs to an existing mapping"),
    ({**GOOD_K8S, "fields": {"type": {"path": "reason"}, "message": {"path": "message"}}}, "the mapping is not valid"),
])
def test_invalid_mapping_is_not_saved_and_the_sample_is_marked_failed(submission, problem):
    result, outputs, puts = run(K8S, submission)
    assert outputs[0].startswith("ERROR Nothing was saved") and problem in outputs[0]
    assert isinstance(result, JobFailed) and "no valid mapping after 1 attempts" in str(result)
    assert puts == [("/schema-samples/fp-1", {"status": "failed", "error": str(result)})]


def test_a_match_that_also_takes_a_sibling_schema_is_rejected_then_fixed():
    base = {"id": "trade-rejected-v1",
            "fields": {"type": {"path": "meta.schema"}, "service": {"path": "meta.producer"},
                       "ts": {"path": "meta.ts", "format": "epoch_ms"}},
            "dedup_fields": ["payload.desk", "payload.reason"]}
    source = {"field": "source", "op": "eq", "value": "trades-kafka"}
    by_producer = {**base, "match": {"all": [source, {"field": "raw.meta.producer", "op": "eq", "value": "trade-gateway"}]}}
    by_schema = {**base, "match": {"all": [source, {"field": "raw.meta.schema", "op": "eq", "value": "trade.rejected.v1"}]}}
    others = [{"fingerprint": "fp-2", "source_id": "trades-kafka", "sample": BOOKED}]

    result, outputs, puts = run(REJECTED, by_producer, by_schema, source_id="trades-kafka", others=others)
    assert "match also takes the sample of another schema from 'trades-kafka'" in outputs[0]
    assert "trade.booked.v1" in outputs[0]
    assert result == {"mapping_id": "trade-rejected-v1"} and "Saved" in outputs[1]
    assert [p for p, _ in puts] == ["/mappings/trade-rejected-v1?fingerprint=fp-1"]


@pytest.mark.parametrize("sample", [["not", "an", "object"], {"_truncated": True, "_chars": 20000, "_preview": "{"}])
def test_unmappable_sample_fails_without_a_model_call(sample):
    result, outputs, puts = run(sample, GOOD_K8S)
    assert isinstance(result, JobFailed) and outputs == []
    assert puts == [("/schema-samples/fp-1", {"status": "failed", "error": str(result)})]
