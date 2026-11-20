import pytest

from conftest import EXAMPLE_RULES, PAYLOADS, SEED_MAPPINGS, make_event
from processor import Envelope, build_envelope, compile_mappings, compile_rules, fingerprint, match_rules

RECEIVED = "2026-11-20T10:15:02.123Z"


def _env(name, mappings, **payload_changes):
    source, payload = PAYLOADS[name]
    return build_envelope(make_event(source, payload | payload_changes), mappings)


def _common(event_id="evt-0"):
    return {"event_id": event_id, "received_at": RECEIVED}


@pytest.mark.parametrize("name, expected", [
    ("workflow", {"source": "workflows-kafka", "type": "WORKFLOW_FAILED", "severity": "critical", "env": "prod",
                  "service": "order-settlement", "ts": "2026-11-20T10:15:01.000Z",
                  "dedup_hints": ["order-settlement", "TIMEOUT"], "mapped": True, "mapping_id": "workflow-failed"}),
    ("metric", {"source": "alertmanager-http", "type": "HighErrorRate", "severity": "critical", "env": "prod",
                "service": "payments-api", "ts": "2026-11-20T10:14:00.250Z",
                "dedup_hints": ["eu-west"], "mapped": True, "mapping_id": "alertmanager"}),
    ("trade", {"source": "trades-kafka", "type": "trade.rejected.v1", "severity": "error", "env": "prod",
               "service": "trade-gateway", "ts": "2026-11-20T10:15:01.123Z",
               "dedup_hints": ["rates", "LIMIT_BREACH"], "mapped": True, "mapping_id": "trade-rejected-v1"}),
])
def test_appendix_envelopes(name, expected, mappings):
    env = _env(name, mappings)
    assert env == _common() | expected | {"raw": PAYLOADS[name][1]}
    Envelope.model_validate(env)  # conforms to the contract shape


def _ts_mapping(fmt):
    return compile_mappings([{
        "id": "m", "status": "active", "origin": "human", "priority": 1,
        "match": {"all": []}, "fields": {"ts": {"path": "t", "format": fmt}},
    }])[0]


@pytest.mark.parametrize("fmt, value, expected", [
    ("iso", "2026-11-20T10:15:01Z", "2026-11-20T10:15:01.000Z"),
    ("iso", "2026-11-20T12:15:01.123456+02:00", "2026-11-20T10:15:01.123Z"),
    ("iso", "2026-11-20T10:15:01", "2026-11-20T10:15:01.000Z"),  # naive is taken as UTC
    ("epoch_ms", 1795169701123, "2026-11-20T10:15:01.123Z"),
    ("epoch_s", 1795169701, "2026-11-20T10:15:01.000Z"),
    ("epoch_s", 1795169701.5, "2026-11-20T10:15:01.500Z"),
    ("iso", "yesterday", RECEIVED),  # unparseable -> received_at
    ("iso", 1795169701, RECEIVED),
    ("epoch_ms", "1795169701123", RECEIVED),
    ("epoch_s", 10 ** 20, RECEIVED),
])
def test_timestamps(fmt, value, expected):
    env = build_envelope(make_event("s", {"t": value}), _ts_mapping(fmt))
    assert env["ts"] == expected


@pytest.mark.parametrize("value, expected", [
    ("P1", "critical"), ("p1", "critical"), ("Warn", "warning"), ("ERROR", "error"),
    ("critical", "critical"), ("Info", "info"), (3, "error"), ("bogus", "warning"), (None, "warning"),
])
def test_severity_map(value, expected):
    m = compile_mappings([{
        "id": "m", "status": "active", "origin": "human", "priority": 1, "match": {"all": []},
        "fields": {"severity": {"path": "sev"}},
        "severity_map": {"P1": "critical", "WARN": "warning", "ERROR": "error", "3": "error"},
    }])[0]
    assert build_envelope(make_event("s", {"sev": value}), m)["severity"] == expected


def test_unresolvable_field_gets_default():
    body = SEED_MAPPINGS[1] | {"fields": SEED_MAPPINGS[1]["fields"] | {"service": {"path": "labels.app"}}}
    m, _ = compile_mappings([body])
    env = _env("metric", m, startsAt="not a time")
    assert (env["service"], env["ts"], env["mapped"]) == ("alertmanager-http", RECEIVED, True)


def test_unmapped_event_and_raw_rule(mappings):
    payload = {"foo": {"bar": 1}}
    env = build_envelope(make_event("mystery", payload), mappings)
    assert env == _common() | {
        "source": "mystery", "type": "unmapped", "severity": "warning", "env": "unknown", "service": "mystery",
        "ts": RECEIVED, "dedup_hints": [], "mapped": False, "mapping_id": None, "raw": payload}
    rules = compile_rules([EXAMPLE_RULES[3] | {"id": "r1", "when": {"all": [
        {"field": "raw.foo.bar", "op": "eq", "value": 1}, {"field": "type", "op": "eq", "value": "unmapped"}]}}])[0]
    assert [m.rule_id for m in match_rules(env, rules)] == ["r1"]


def test_string_payload(mappings, rules):
    line = "2026-11-20 10:15:01 ERROR pricing-engine-eu-1 cache miss"
    env = build_envelope(make_event("logs", line), mappings)
    assert (env["mapped"], env["raw"], env["service"]) == (False, line, "logs")
    assert match_rules(env, rules) == []
    by_source = compile_mappings([{
        "id": "logs", "status": "active", "origin": "human", "priority": 1,
        "match": {"all": [{"field": "source", "op": "eq", "value": "logs"}]},
        "fields": {"service": {"path": "logger"}, "env": {"const": "prod"}}, "dedup_fields": ["x"]}])[0]
    env = build_envelope(make_event("logs", line), by_source)
    assert (env["mapped"], env["service"], env["env"], env["dedup_hints"]) == (True, "logs", "prod", [None])
    assert fingerprint("logs", line) == fingerprint("logs", "another line")


@pytest.mark.parametrize("payload, match", [
    ({"type": "X"}, {"field": "type", "op": "eq", "value": "X"}),
    ({"alertname": "HighErrorRate"}, {"field": "alertname", "op": "exists"}),
    ({"meta": {"schema": "trade.rejected.v1"}}, {"field": "meta.schema", "op": "glob", "value": "trade.*"}),
])
def test_mapping_match_bare_path_is_payload(payload, match):
    m, _ = compile_mappings([{"id": "m", "status": "active", "origin": "llm", "priority": 1,
                              "match": {"all": [match]}, "fields": {}}])
    assert build_envelope(make_event("s", payload), m)["mapping_id"] == "m"
    assert build_envelope(make_event("s", {"other": 1}), m)["mapped"] is False


def test_bad_rows_do_not_block_valid_ones():
    rules, errors = compile_rules([{"team_id": "x"}, EXAMPLE_RULES[0], EXAMPLE_RULES[1] | {"when": "bad"}])
    assert [r.id for r in rules] == ["settlements-workflow-failed"]
    assert [row for row, _ in errors] == [0, "pricing-engine-errors"]
    mappings, errors = compile_mappings([SEED_MAPPINGS[0], "not a mapping", SEED_MAPPINGS[1] | {"priority": "high"}])
    assert [m.id for m in mappings] == ["workflow-failed"]
    assert [row for row, _ in errors] == [1, "alertmanager"] and "priority" in errors[1][1]


def test_template_renders_non_strings_as_json():
    rules, _ = compile_rules([EXAMPLE_RULES[3] | {"id": "r", "when": {"all": []},
                                                  "present": {"title": "{{d}} {{n}} {{b}}"}}])
    env = build_envelope(make_event("s", {"d": {"city": "Zürich", "café": True}, "n": 1.5, "b": False}), [])
    assert match_rules(env, rules)[0].title == '{"city": "Zürich", "café": true} 1.5 false'


def test_mapping_priority():
    def m(id_, priority, status="active"):
        return {"id": id_, "status": status, "origin": "human", "priority": priority,
                "match": {"all": []}, "fields": {"type": {"const": id_}}}
    for bodies in ([m("low", 10), m("high", 200)], [m("high", 200), m("low", 10)]):
        assert build_envelope(make_event("s", {}), compile_mappings(bodies + [m("off", 999, "disabled")])[0])["type"] == "high"


VARIANTS = [  # (payload name, payload changes, the rule ids that must match)
    ("workflow", {}, {"settlements-workflow-failed"}),
    ("metric", {}, {"anna-payments-critical"}),
    ("trade", {}, {"rates-large-rejections"}),
    ("pricing", {}, {"pricing-engine-errors"}),
    ("workflow", {"environment": "staging"}, set()),
    ("workflow", {"workflowName": "order-booking"}, set()),
    ("metric", {"labels": {"service": "payments-api", "severity": "warning"}}, set()),
    ("trade", {"payload": {"desk": "rates", "notional": 5000000}}, set()),
    ("trade", {"payload": {"desk": "fx", "notional": 25000000}}, set()),
    ("pricing", {"level": "warning"}, set()),
    ("pricing", {"level": "FATAL"}, {"pricing-engine-errors"}),
]


@pytest.mark.parametrize("name, changes, expected", VARIANTS)
def test_example_rules(name, changes, expected, mappings, rules):
    assert {m.rule_id for m in match_rules(_env(name, mappings, **changes), rules)} == expected


def test_presentation(mappings, rules):
    [m] = match_rules(_env("workflow", mappings), rules)
    assert (m.team_id, m.title, m.body, m.color) == (
        "settlements", "order-settlement failed at step settle",
        "Counterparty service did not respond in 5000 ms", "red")
    assert m.fields == {"step": "settle", "error.code": "TIMEOUT",
                        "error.message": "Counterparty service did not respond in 5000 ms"}
    [m] = match_rules(_env("trade", mappings), rules)
    assert m.title == "Trade T-88231 rejected: LIMIT_BREACH (25000000 EUR)"
    [m] = match_rules(_env("pricing", mappings, instrument=None), rules)
    assert m.title == "Pricing errors on "
    [m] = match_rules(_env("metric", mappings), rules)
    assert (m.title, m.body, m.fields, m.color) == ("HighErrorRate · payments-api", "", {}, None)
    named, _ = compile_rules([EXAMPLE_RULES[3] | {"name": "Critical from payments-api"}])
    assert match_rules(_env("metric", mappings), named)[0].title == "Critical from payments-api"


def test_alert_keys(mappings, rules):
    source, payload = PAYLOADS["workflow"]
    keys = {
        k.alert_key
        for i in range(500)
        for k in match_rules(build_envelope(make_event(source, payload | {"workflowId": f"WF-{i}"}, i), mappings),
                             rules)
    }
    assert len(keys) == 1

    def key(name, rule_set=rules, **changes):
        [m] = match_rules(_env(name, mappings, **changes), rule_set)
        return m.alert_key

    assert key("pricing", instrument="EURUSD") != key("pricing", instrument="GBPUSD")
    # no instrument at all: a fixed placeholder keeps the key stable
    no_instrument = [
        match_rules(build_envelope(make_event("pricing-logs", {"logger": "pricing-engine-eu-1", "level": "ERR",
                                                                "msg": msg}), mappings), rules)[0]
        for msg in ("a", "b")
    ]
    assert no_instrument[0].alert_key == no_instrument[1].alert_key != key("pricing")
    assert no_instrument[0].title == "Pricing errors on "
    twin, _ = compile_rules([EXAMPLE_RULES[0] | {"id": "settlements-twin"}])
    assert key("workflow", twin) != key("workflow")
    # default alert_key: type, service + dedup_hints
    assert key("trade") == key("trade", payload=PAYLOADS["trade"][1]["payload"] | {"tradeId": "T-2"})
    assert key("trade") != key("trade", payload=PAYLOADS["trade"][1]["payload"] | {"reason": "SANCTIONS"})


def test_fingerprint():
    source, trade = PAYLOADS["trade"]
    other_values = {"meta": {"schema": "trade.rejected.v1", "producer": "x", "ts": 1},
                    "payload": {"tradeId": "T-1", "desk": "fx", "notional": 1, "currency": "USD", "reason": "X"}}
    assert fingerprint(source, trade) == fingerprint(source, other_values)
    other_schema = {"meta": {**trade["meta"], "schema": "trade.booked.v1"}, "payload": trade["payload"]}
    assert fingerprint(source, trade) != fingerprint(source, other_schema)
    assert fingerprint(source, trade) != fingerprint("other-source", trade)
    assert len(fingerprint(source, trade)) == 64
