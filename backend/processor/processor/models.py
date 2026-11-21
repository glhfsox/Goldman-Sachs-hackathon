"""Contract models (contracts/events.md, contracts/rules.md). Validated once, at config load."""

import re
from typing import Annotated, Any, Literal, Union

from pydantic import BaseModel, ConfigDict, Discriminator, Field, Tag, field_validator, model_validator

SEVERITIES = ("info", "warning", "error", "critical")
ENVELOPE_FIELDS = frozenset(
    ("event_id", "source", "type", "severity", "env", "service", "ts",
     "dedup_hints", "mapped", "mapping_id", "received_at", "raw")
)
TEMPLATE_RE = re.compile(r"\{\{\s*(.*?)\s*\}\}")
_PATH = r"[^.]+(\.[^.]+)*"

Path = Annotated[str, Field(pattern=f"^{_PATH}$")]
Severity = Literal["info", "warning", "error", "critical"]


class _Model(BaseModel):
    model_config = ConfigDict(extra="forbid")


# --- events.md ---

class RawEvent(_Model):
    event_id: str
    source_id: str
    received_at: str
    payload: Any


class Envelope(_Model):
    event_id: str
    source: str
    type: str
    severity: Severity
    env: str
    service: str
    ts: str
    dedup_hints: list[Any]
    mapped: bool
    mapping_id: str | None
    received_at: str
    raw: Any


# --- rules.md §2: condition language ---

Op = Literal["eq", "ne", "gt", "gte", "lt", "lte", "in", "glob", "sev_gte", "exists"]


class Leaf(_Model):
    field: Path
    op: Op
    value: Any = None

    @model_validator(mode="after")
    def _check_value(self):
        has, v, op = "value" in self.model_fields_set, self.value, self.op
        if op == "exists":
            ok, need = not has, "no value"
        elif op in ("eq", "ne"):
            ok, need = has, "a value"
        elif op in ("gt", "gte", "lt", "lte"):
            ok, need = type(v) in (int, float), "a numeric value"
        elif op == "in":
            ok, need = type(v) is list, "an array value"
        elif op == "glob":
            ok, need = type(v) is str, "a string value"
        else:  # sev_gte
            ok, need = v in SEVERITIES, f"a value in {list(SEVERITIES)}"
        if not ok:
            raise ValueError(f"op '{op}' needs {need}")
        return self


class AllOf(_Model):
    all: list["Condition"]


class AnyOf(_Model):
    any: list["Condition"]


def _condition_tag(v: Any) -> str:
    if isinstance(v, dict):
        return "all" if "all" in v else "any" if "any" in v else "leaf"
    return {AllOf: "all", AnyOf: "any"}.get(type(v), "leaf")


Condition = Annotated[
    Union[Annotated[Leaf, Tag("leaf")], Annotated[AllOf, Tag("all")], Annotated[AnyOf, Tag("any")]],
    Discriminator(_condition_tag),
]
AllOf.model_rebuild()
AnyOf.model_rebuild()


# --- rules.md §3: schema mapping ---

class FieldSpec(_Model):
    path: Path | None = None
    const: Any = None

    @model_validator(mode="after")
    def _one_of(self):
        if len(self.model_fields_set & {"path", "const"}) != 1:
            raise ValueError("a field is either {'path': ...} or {'const': ...}")
        return self


class TsSpec(FieldSpec):
    format: Literal["iso", "epoch_ms", "epoch_s"] = "iso"


class MappingFields(_Model):
    type: FieldSpec | None = None
    service: FieldSpec | None = None
    severity: FieldSpec | None = None
    env: FieldSpec | None = None
    ts: TsSpec | None = None


class Mapping(_Model):
    id: str
    status: Literal["active", "disabled"]
    origin: Literal["seed", "human", "llm"]
    priority: int
    match: Condition
    fields: MappingFields
    severity_map: dict[str, Severity] = {}
    dedup_fields: list[Path] = []


# --- rules.md §4: rule ---

class Recipient(_Model):
    team: str | None = None
    user: str | None = None
    on_call: bool = False

    @model_validator(mode="after")
    def _one_of(self):
        if (self.team is None) == (self.user is None) or (self.on_call and self.user is not None):
            raise ValueError("a recipient is {'team': id}, {'team': id, 'on_call': true} or {'user': id}")
        return self


class Route(_Model):
    recipients: list[Recipient]
    channels: list[Literal["ui", "email", "sms", "call"]] | None = None


class Present(_Model):
    title: str | None = None
    body: str | None = None
    fields: list[Path] = []
    color: Literal["red", "orange", "yellow", "blue", "grey"] | None = None

    @field_validator("title", "body")
    @classmethod
    def _template_paths(cls, v):
        for p in TEMPLATE_RE.findall(v or ""):
            if not re.fullmatch(_PATH, p):
                raise ValueError(f"bad template path '{{{{{p}}}}}'")
        return v


class Rule(_Model):
    id: str
    team_id: str
    name: str | None = None
    enabled: bool = True
    when: Condition
    severity: Severity | None = None
    alert_key: list[Path] | None = None
    window_s: int = Field(300, gt=0)
    route: Route
    present: Present = Present()
    runbook_id: str | None = None
