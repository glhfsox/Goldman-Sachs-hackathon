"""Sources, schemas and the events built from them."""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import yaml

from .generate import MISSING, Context, apply_overrides, generate_payload, get_path, render_text

ROOT = Path(__file__).resolve().parent.parent
SOURCES_FILE = ROOT / "sources.yaml"
SCHEMAS_DIR = ROOT / "schemas"
SCENARIOS_DIR = ROOT / "scenarios"


@dataclass(frozen=True)
class Source:
    id: str
    kind: str  # "kafka" or "http"
    topic: str | None = None
    key: str | None = None  # dot path into the payload, used as the Kafka message key


def load_sources(path: Path = SOURCES_FILE) -> dict[str, Source]:
    raw = yaml.safe_load(path.read_text())
    sources = {}
    for sid, cfg in raw.items():
        if cfg["kind"] not in ("kafka", "http"):
            raise ValueError(f"{sid}: kind must be kafka or http")
        if cfg["kind"] == "kafka" and not cfg.get("topic"):
            raise ValueError(f"{sid}: a kafka source needs a topic")
        sources[sid] = Source(sid, cfg["kind"], cfg.get("topic"), cfg.get("key"))
    return sources


@dataclass(frozen=True)
class Schema:
    name: str
    source: str
    fields: dict
    description: str = ""
    text: dict | None = None  # {"share": 0.05, "template": "..."}: sometimes sent as a plain-text line


def load_schema(name_or_path: str) -> Schema:
    path = Path(name_or_path)
    if not path.suffix:
        path = SCHEMAS_DIR / f"{name_or_path}.yaml"
    raw = yaml.safe_load(path.read_text())
    return Schema(raw["name"], raw["source"], raw["fields"], raw.get("description", ""), raw.get("text"))


def list_schemas() -> list[str]:
    return sorted(p.stem for p in SCHEMAS_DIR.glob("*.yaml"))


@dataclass(frozen=True)
class Event:
    source_id: str
    kind: str
    schema: str
    body: bytes
    content_type: str
    topic: str | None = None
    key: str | None = None

    def payload(self) -> Any:
        """The body as ingest will see it: parsed JSON, else the text."""
        text = self.body.decode("utf-8")
        try:
            return json.loads(text)
        except ValueError:
            return text


JSON_TYPE = "application/json"
TEXT_TYPE = "text/plain; charset=utf-8"


def build_event(
    schema: Schema,
    ctx: Context,
    sources: dict[str, Source],
    overrides: dict | None = None,
    source_id: str | None = None,
    text: bool | None = None,
    corrupt: bool = False,
) -> Event:
    """One event of `schema`. `source_id` overrides the schema's source (an unknown id is sent over HTTP,
    for the 404 edge case). `text` forces or forbids the plain-text form; None = the schema's share.
    `corrupt` cuts the JSON short, for the invalid-JSON edge case."""
    sid = source_id or schema.source
    source = sources.get(sid) or Source(sid, "http")
    fields = apply_overrides(schema.fields, overrides)
    payload = generate_payload(fields, ctx)

    as_text = text
    if as_text is None:
        as_text = bool(schema.text) and ctx.rng.random() < schema.text.get("share", 0)
    if as_text and schema.text:
        line = render_text(schema.text["template"], ctx, payload, seq_name=schema.name)
        body, ctype = " ".join(line.split()).encode("utf-8"), TEXT_TYPE
    else:
        body, ctype = json.dumps(payload, ensure_ascii=False).encode("utf-8"), JSON_TYPE
        if corrupt:
            body = body[: max(1, len(body) * 2 // 3)]

    key = None
    if source.kind == "kafka" and source.key:
        value = get_path(payload, source.key)
        key = None if value is MISSING else str(value)
    return Event(sid, source.kind, schema.name, body, ctype, source.topic, key)
