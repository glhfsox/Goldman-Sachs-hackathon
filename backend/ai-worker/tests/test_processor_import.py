from processor.core import build_envelope, compile_mappings


def test_processor_mapping_code_is_importable():
    compiled, errors = compile_mappings([{
        "id": "m", "status": "active", "origin": "llm", "priority": 100,
        "match": {"all": [{"field": "raw.kind", "op": "eq", "value": "x"}]},
        "fields": {"type": {"path": "kind"}, "service": {"const": "svc"}, "severity": {"const": "error"},
                   "env": {"const": "prod"}, "ts": {"path": "at", "format": "iso"}}}])
    assert errors == []
    env = build_envelope({"event_id": "e", "source_id": "s", "received_at": "2026-11-20T10:15:02.123Z",
                          "payload": {"kind": "x", "at": "2026-11-20T10:15:01Z"}}, compiled)
    assert env["mapped"] and env["mapping_id"] == "m" and env["service"] == "svc"
