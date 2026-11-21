import copy

import psycopg
import pytest

from conftest import EXAMPLE_RULES, PAYLOADS, SEED_MAPPINGS, make_event
from processor import build_envelope, match_rules
from processor.config import Config

T1, T2 = "2026-11-20T09:30:00.000Z", "2026-11-20T09:31:00.000Z"


def rec(kind, body, at=T1, deleted=False, id=None):
    return {"kind": kind, "id": id or body["id"], "updated_at": at, "deleted": deleted, "body": body}


def changed(body, **kw):
    return {**copy.deepcopy(body), **kw}


@pytest.fixture
def conns(db):
    opened = [psycopg.connect(db, autocommit=True)]
    opened[0].execute("TRUNCATE mappings, rules, service_deps")

    def make():
        opened.append(psycopg.connect(db))
        return opened[-1]
    yield make
    for c in opened:
        c.close()


@pytest.fixture
def cfg(conns):
    return Config(conns())


def rule_ids(cfg):
    return sorted(r.id for r in cfg.rules)


def test_applied_config_runs_an_appendix_a_event_end_to_end(cfg):
    assert (cfg.mappings, cfg.rules) == ([], [])
    cfg.apply([rec("mapping", m) for m in SEED_MAPPINGS] + [rec("rule", r) for r in EXAMPLE_RULES])
    assert cfg.refresh() is True
    env = build_envelope(make_event(*PAYLOADS["workflow"]), cfg.mappings)
    assert (env["mapping_id"], env["severity"]) == ("workflow-failed", "critical")
    [m] = match_rules(env, cfg.rules)
    assert (m.rule_id, m.title, m.color) == ("settlements-workflow-failed", "order-settlement failed at step settle", "red")
    assert cfg.refresh() is False


def test_older_record_does_not_overwrite_a_newer_row(cfg, conns):
    rule = EXAMPLE_RULES[0]
    cfg.apply([rec("rule", changed(rule, name="new"), at=T2)])
    cfg.apply([rec("rule", changed(rule, name="old"), at=T1)])
    cfg.refresh()
    assert cfg.rules[0].rule.name == "new"
    cfg.apply([rec("rule", changed(rule, name="newer"), at="2026-11-20T09:32:00Z")])
    assert cfg.refresh() is True
    assert cfg.rules[0].rule.name == "newer"


def test_disabling_a_rule_and_a_mapping_is_picked_up(cfg):
    mapping, rule = SEED_MAPPINGS[0], EXAMPLE_RULES[0]
    cfg.apply([rec("mapping", mapping), rec("rule", rule), rec("rule", EXAMPLE_RULES[1])])
    cfg.refresh()
    assert ([m.id for m in cfg.mappings], len(cfg.rules)) == (["workflow-failed"], 2)
    cfg.apply([rec("rule", changed(rule, enabled=False), at=T2)])
    assert cfg.refresh() is True
    assert rule_ids(cfg) == ["pricing-engine-errors"]
    cfg.apply([rec("mapping", changed(mapping, status="disabled"), at=T2)])
    assert cfg.refresh() is True
    assert cfg.mappings == []


def test_another_replica_sees_apply_after_its_refresh(cfg, conns):
    other = Config(conns())
    cfg.apply([rec("rule", EXAMPLE_RULES[0])])
    assert other.rules == []
    assert other.refresh() is True
    assert rule_ids(other) == ["settlements-workflow-failed"]
    assert other.refresh() is False


def test_an_invalid_rule_does_not_block_the_valid_ones(cfg, caplog):
    bad = changed(EXAMPLE_RULES[1], id="bad", window_s=-1)
    cfg.apply([rec("rule", bad), rec("rule", EXAMPLE_RULES[0])])
    assert cfg.refresh() is True
    assert rule_ids(cfg) == ["settlements-workflow-failed"]
    assert "invalid rule bad" in caplog.text


def test_malformed_records_are_skipped(cfg, caplog):
    cfg.apply([
        None, {"id": "x", "body": {}}, {"kind": "rule", "body": {}}, {"kind": "rule", "id": "r"},
        {"kind": "rule", "id": 5, "updated_at": T1, "body": {}},
        {"kind": "rule", "id": "r", "updated_at": T1, "body": "not an object"},
        {"kind": "rule", "id": "r", "updated_at": "yesterday", "body": {"id": "r"}},
        {"kind": "mapping", "id": "m", "updated_at": T1, "body": {"id": "m", "priority": 2 ** 40}},  # Postgres rejects the int
        {"kind": "service_dep", "id": "a:b", "updated_at": T1, "body": {"service": "a"}},
        {"kind": "team", "id": "t", "updated_at": T1, "body": {"id": "t", "name": "T"}},  # not ours: ignored
        rec("rule", EXAMPLE_RULES[0]),
    ])
    assert cfg.refresh() is True
    assert rule_ids(cfg) == ["settlements-workflow-failed"]
    assert caplog.text.count("skipping malformed config record") == 9


def test_service_dep_insert_and_delete(cfg, conns):
    check = conns()
    deps = lambda: check.execute("SELECT service, depends_on FROM service_deps ORDER BY 1, 2").fetchall()  # noqa: E731
    dep = {"service": "payments-api", "depends_on": "counterparty-service"}
    cfg.apply([rec("service_dep", dep, id="payments-api:counterparty-service")] * 2)
    assert deps() == [("payments-api", "counterparty-service")]
    cfg.apply([{"kind": "service_dep", "id": "payments-api:counterparty-service", "updated_at": T2,
                "deleted": True, "body": None}])
    assert deps() == []
    assert cfg.refresh() is False  # the service map is stored only


def test_a_body_too_deep_for_postgres_is_skipped(cfg, caplog):
    deep = []
    for _ in range(20_000):
        deep = [deep]
    cfg.apply([rec("rule", {"id": "deep", "x": deep}), rec("rule", EXAMPLE_RULES[0])])
    assert cfg.refresh() is True
    assert rule_ids(cfg) == ["settlements-workflow-failed"]
    assert caplog.text.count("skipping malformed config record") == 1


@pytest.mark.parametrize("kind, body", [("rule", EXAMPLE_RULES[1]), ("mapping", SEED_MAPPINGS[1])])
def test_a_record_whose_body_id_differs_is_skipped(cfg, caplog, kind, body):
    cfg.apply([rec(kind, body, id="other")])
    cfg.refresh()
    assert (cfg.mappings, cfg.rules) == ([], [])
    assert "skipping malformed config record" in caplog.text
