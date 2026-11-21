import pytest

from sim.generate import Context, apply_overrides, generate_payload
from tests.conftest import FIXED_NOW


def make(seed=1):
    return Context(seed=seed, now=lambda: FIXED_NOW)


SPEC = {
    "type": {"$choice": ["A", "B"], "$weights": [1, 1]},
    "id": {"$pattern": "X-{date}-{seq:03d}"},
    "amount": {"$lognormal": 1000, "$min": 10, "$max": 5000},
    "detail": {"$if": {"path": "type", "eq": "A"}, "code": {"$map": "type", "$values": {"A": "a-code"}}},
    "ts": {"$now": "iso_ms"},
    "epoch": {"$now": "epoch_ms", "$offset_s": -1},
    "maybe": {"$p": 0.0, "$const": 1},
    "copy": {"$ref": "type"},
    "msg": {"$pattern": "{type} happened"},
}


def test_same_seed_same_payloads():
    a, b = make(7), make(7)
    assert [generate_payload(SPEC, a) for _ in range(20)] == [generate_payload(SPEC, b) for _ in range(20)]


def test_generators(ctx):
    for i in range(1, 30):
        p = generate_payload(SPEC, ctx)
        assert p["type"] in ("A", "B") and p["copy"] == p["type"]
        assert p["id"] == f"X-20261120-{i:03d}"
        assert 10 <= p["amount"] <= 5000 and isinstance(p["amount"], int)
        assert ("detail" in p) == (p["type"] == "A")
        if "detail" in p:
            assert p["detail"] == {"code": "a-code"}
        assert p["ts"] == "2026-11-20T10:15:01.204Z"
        assert p["epoch"] == int(FIXED_NOW.timestamp() * 1000) - 1000
        assert "maybe" not in p
        assert p["msg"] == f"{p['type']} happened"


def test_overrides_replace_leaves_and_keep_the_rest(ctx):
    spec = {"error": {"code": {"$choice": ["X", "Y"]}, "message": "m"}, "level": {"$choice": ["INFO"]}}
    fields = apply_overrides(spec, {"error": {"code": "TIMEOUT"}, "level": {"$choice": ["ERROR"]}, "new.deep": 1})
    p = generate_payload(fields, ctx)
    assert p == {"error": {"code": "TIMEOUT", "message": "m"}, "level": "ERROR", "new": {"deep": 1}}
    assert spec["error"]["code"] == {"$choice": ["X", "Y"]}  # original untouched


def test_unknown_generator_is_an_error(ctx):
    with pytest.raises(ValueError):
        generate_payload({"x": {"$nope": 1, "$p": 1.0, "inner": 1}}, ctx)
