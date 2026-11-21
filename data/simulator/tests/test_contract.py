"""Generated payloads and the source registry must match contracts/data/sources.md."""

import json
import re
from pathlib import Path

import pytest

from sim.generate import MISSING, get_path
from sim.model import build_event, load_schema, load_sources

CONTRACT = (Path(__file__).resolve().parents[3] / "contracts" / "data" / "sources.md").read_text()
SECTIONS = {"workflow_event": "4.1", "trade_event": "4.2", "metric_alert": "4.3", "log_record": "4.4"}
SAMPLES = 300


def section(number: str) -> str:
    return CONTRACT.split(f"### {number} ", 1)[1].split("\n### ", 1)[0].split("\n## ", 1)[0]


def field_table(number: str) -> list[dict]:
    """Rows of the `| Field | Type | Values |` table of a catalog section."""
    rows = []
    for line in section(number).splitlines():
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if len(cells) != 3 or not cells[0].startswith("`"):
            continue
        path = re.match(r"`([^`]+)`", cells[0]).group(1)
        rows.append({
            "path": path,
            "optional": "*opt*" in cells[0],
            "type": cells[1],
            "values": cells[2],
        })
    return rows


def enum_of(row: dict) -> set | None:
    """The allowed values, if the cell is a closed list (not a pattern, example or range)."""
    cell = row["values"]
    if row["type"] != "string" or "e.g." in cell or "…" in cell:
        return None
    listed = cell.rsplit(": ", 1)[-1]  # drop a lead-in such as "ISO 4217: " or "only on `x`: "
    tokens = re.findall(r"`([^`]+)`", listed)
    if not listed.startswith("`") or len(tokens) < 2:  # prose that merely mentions values
        return None
    return set(tokens)


TYPES = {"string": str, "integer": int, "object": dict}


def test_registry_matches_contract():
    registry = CONTRACT.split("## 1. Source registry", 1)[1].split("\n## ", 1)[0]
    block = re.search(r"```json\n(.*?)```", registry, re.DOTALL).group(1)
    expected = {r["id"]: (r["kind"], r["config"].get("topic")) for r in json.loads(block)}
    actual = {s.id: (s.kind, s.topic) for s in load_sources().values()}
    assert actual == expected


@pytest.mark.parametrize("schema_name", sorted(SECTIONS))
def test_payloads_match_catalog(schema_name, ctx):
    rows = field_table(SECTIONS[schema_name])
    assert rows, f"no field table found in §{SECTIONS[schema_name]}"
    documented = [r["path"] for r in rows]
    schema, sources = load_schema(schema_name), load_sources()
    assert schema.source in sources

    for _ in range(SAMPLES):
        payload = build_event(schema, ctx, sources, text=False).payload()
        for row in rows:
            if row["path"].endswith(".*"):
                continue
            value = get_path(payload, row["path"])
            if value is MISSING:
                assert row["optional"], f"{schema_name}: required {row['path']} missing in {payload}"
                continue
            py_type = TYPES[row["type"]]
            assert isinstance(value, py_type) and not isinstance(value, bool), \
                f"{schema_name}: {row['path']} = {value!r} is not {row['type']}"
            allowed = enum_of(row)
            if allowed is not None:
                assert value in allowed, f"{schema_name}: {row['path']} = {value!r} not in {sorted(allowed)}"
        for path in leaf_paths(payload):
            assert any(path == d or path.startswith(d.rstrip("*").rstrip(".") + ".") for d in documented), \
                f"{schema_name}: field {path} is not in the contract"


def leaf_paths(doc: dict, prefix: str = "") -> list[str]:
    out = []
    for k, v in doc.items():
        path = f"{prefix}{k}"
        out.extend(leaf_paths(v, path + ".") if isinstance(v, dict) else [path])
    return out


def test_kafka_keys_follow_contract(ctx):
    sources = load_sources()
    wf = build_event(load_schema("workflow_event"), ctx, sources)
    assert wf.topic == "source.workflows" and wf.key == wf.payload()["workflowId"]
    tr = build_event(load_schema("trade_event"), ctx, sources)
    assert tr.topic == "source.trades" and tr.key == tr.payload()["payload"]["tradeId"]


def test_rates_notional_tail_crosses_threshold(ctx):
    """Contract: about 1 in 5 rates trades above 10 000 000 (what the Rates rule fires on)."""
    schema, sources = load_schema("trade_event"), load_sources()
    notionals = [build_event(schema, ctx, sources, overrides={"payload.desk": "rates"}).payload()["payload"]["notional"]
                 for _ in range(2000)]
    share = sum(n > 10_000_000 for n in notionals) / len(notionals)
    assert 0.12 < share < 0.32


def test_text_form_is_one_plain_line(ctx):
    e = build_event(load_schema("log_record"), ctx, load_sources(), text=True)
    assert e.content_type.startswith("text/plain")
    line = e.body.decode()
    assert "\n" not in line and line.startswith("2026-11-20T10:15:01.204Z ")
    assert isinstance(e.payload(), str)


def test_body_size_limit(ctx):
    sources = load_sources()
    for name in ["workflow_event", "trade_event", "metric_alert", "log_record", "k8s_event"]:
        for _ in range(50):
            assert len(build_event(load_schema(name), ctx, sources).body) <= 64 * 1024
