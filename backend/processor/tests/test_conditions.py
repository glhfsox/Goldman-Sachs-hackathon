import time

import pytest
from conftest import EXAMPLE_RULES, SEED_MAPPINGS
from processor import MISSING, compile_condition, compile_mappings, compile_path, compile_rules, resolve

DOC = {
    "type": "t", "severity": "error", "service": "pricing-engine-eu-1",
    "raw": {"n": 5, "f": 2.5, "s": "abc", "nul": None, "b": True, "z": 0, "d": {"x": 1},
            "error": {"code": "TIMEOUT"}},
}


@pytest.mark.parametrize("field, op, value, expected", [
    ("raw.s", "eq", "abc", True), ("raw.s", "eq", "abd", False),
    ("raw.nul", "eq", None, True), ("raw.nope", "eq", None, False),
    ("raw.s", "ne", "x", True), ("raw.s", "ne", "abc", False), ("raw.nope", "ne", "x", False),
    ("raw.n", "gt", 4, True), ("raw.n", "gt", 5, False), ("raw.s", "gt", 1, False),
    ("raw.b", "gt", 0, False), ("raw.d", "gt", 0, False), ("raw.nope", "gt", 0, False),
    ("raw.n", "gte", 5, True), ("raw.f", "gte", 3, False), ("raw.s", "gte", 0, False),
    ("raw.f", "lt", 3, True), ("raw.n", "lt", 5, False), ("raw.nul", "lt", 1, False),
    ("raw.n", "lte", 5, True), ("raw.n", "lte", 4.9, False), ("raw.nope", "lte", 9, False),
    ("raw.s", "in", ["x", "abc"], True), ("raw.s", "in", ["x"], False),
    ("raw.d", "in", [1, "x"], False), ("raw.nope", "in", [None], False),
    # JSON equality: booleans are not numbers, int and float compare
    ("raw.b", "eq", True, True), ("raw.b", "eq", 1, False), ("raw.n", "eq", 5.0, True), ("raw.z", "eq", False, False),
    ("raw.b", "ne", 1, True), ("raw.z", "ne", 0.0, False),
    ("raw.z", "in", [False], False), ("raw.z", "in", [0.0], True), ("raw.b", "in", [1], False),
    ("raw.d", "eq", {"x": True}, False), ("raw.d", "eq", {"x": 1.0}, True),
    ("service", "glob", "pricing-engine*", True), ("service", "glob", "*-eu-*", True),
    ("service", "glob", "pricing", False), ("raw.s", "glob", "a.c", False),
    ("raw.s", "glob", "abc", True), ("raw.s", "glob", "ABC", False), ("raw.s", "glob", "a*c", True),
    ("raw.s", "glob", "*b*", True), ("raw.s", "glob", "a**", True), ("raw.s", "glob", "ab*bc", False),
    ("raw.s", "glob", "*c*a", False), ("raw.s", "glob", "", False),
    ("raw.n", "glob", "*", False), ("raw.nope", "glob", "*", False),
    ("severity", "sev_gte", "error", True), ("severity", "sev_gte", "info", True),
    ("severity", "sev_gte", "critical", False), ("raw.s", "sev_gte", "info", False),
    ("raw.n", "sev_gte", "info", False), ("raw.nope", "sev_gte", "info", False),
    ("raw.nul", "exists", MISSING, True), ("raw.nope", "exists", MISSING, False),
    ("raw.s.deeper", "exists", MISSING, False),
    # first segment not an envelope field: resolved against raw
    ("s", "eq", "abc", True), ("d.x", "eq", 1, True), ("error.code", "eq", "TIMEOUT", True),
])
def test_ops(field, op, value, expected):
    leaf = {"field": field, "op": op} | ({} if value is MISSING else {"value": value})
    assert compile_condition(leaf)(DOC) is expected


@pytest.mark.parametrize("cond, expected", [
    ({"all": []}, True),
    ({"any": []}, False),
    ({"all": [{"field": "raw.n", "op": "gt", "value": 1},
              {"any": [{"field": "raw.s", "op": "eq", "value": "no"},
                       {"all": [{"field": "raw.d.x", "op": "eq", "value": 1}]}]}]}, True),
    ({"any": [{"field": "raw.nope", "op": "exists"},
              {"all": [{"field": "raw.n", "op": "gt", "value": 1}, {"field": "raw.s", "op": "eq", "value": "no"}]}]},
     False),
])
def test_groups(cond, expected):
    assert compile_condition(cond)(DOC) is expected


def test_paths():
    assert compile_path("error.code") == ("raw", "error", "code")
    assert compile_path("raw.error.code") == ("raw", "error", "code")
    assert compile_path("severity") == ("severity",)
    assert resolve(DOC, compile_path("error.code")) == "TIMEOUT"
    assert resolve(DOC, ("raw", "nul")) is None
    assert resolve(DOC, ("raw", "nope")) is MISSING
    assert resolve(DOC, ("raw", "s", "x")) is MISSING


def _rule(**changes):
    return [EXAMPLE_RULES[0] | changes]


def _leaf_in_rule(leaf):
    return _rule(when={"all": [leaf]})


@pytest.mark.parametrize("load, message", [
    (lambda: compile_rules(_rule(colour="red")), "Extra inputs"),
    (lambda: compile_rules(_leaf_in_rule({"field": "type", "op": "gtt", "value": 1})), "Input should be 'eq'"),
    (lambda: compile_rules(_leaf_in_rule({"field": "type", "value": 1})), "op"),
    (lambda: compile_rules(_leaf_in_rule({"field": "type", "op": "gt", "value": "1"})), "numeric"),
    (lambda: compile_rules(_leaf_in_rule({"field": "type", "op": "exists", "value": 1})), "no value"),
    (lambda: compile_rules(_leaf_in_rule({"field": "type", "op": "sev_gte", "value": "high"})), "sev_gte"),
    (lambda: compile_rules(_leaf_in_rule({"field": "a..b", "op": "exists"})), "pattern"),
    (lambda: compile_rules(_rule(when={"all": "x"})), "list"),
    (lambda: compile_rules(_rule(when={"all": [], "field": "type"})), "Extra inputs"),
    (lambda: compile_rules(_rule(present={"title": "{{a..b}}"})), "template path"),
    (lambda: compile_rules(_rule(route={"recipients": [{"user": "anna", "on_call": True}]})), "recipient"),
    (lambda: compile_mappings([SEED_MAPPINGS[0] | {"fields": {"region": {"path": "x"}}}]), "Extra inputs"),
    (lambda: compile_mappings([SEED_MAPPINGS[0] | {"fields": {"type": {"path": "x", "const": "y"}}}]), "either"),
    (lambda: compile_mappings([SEED_MAPPINGS[0] | {"severity_map": {"P1": "urgent"}}]), "severity_map"),
])
def test_config_rejected(load, message):
    compiled, [(_, error)] = load()
    assert compiled == [] and message in error


def test_glob_does_not_backtrack():
    leaf = compile_condition({"field": "s", "op": "glob", "value": "*a*a*a*a*b"})
    start = time.perf_counter()
    assert leaf({"raw": {"s": "a" * 400}}) is False
    assert time.perf_counter() - start < 0.05
