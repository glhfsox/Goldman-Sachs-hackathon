"""Pure per-event core: paths, conditions, envelope building, fingerprint, rule matching.

Config (rules, mappings) is validated and compiled once; per-event code works on plain dicts.
"""

import hashlib
import json
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from typing import Any, Callable, Iterable

from pydantic import TypeAdapter, ValidationError

from .models import ENVELOPE_FIELDS, SEVERITIES, TEMPLATE_RE, AllOf, AnyOf, Condition, Mapping, Rule

MISSING = object()  # a path that does not resolve; distinct from a present null
SEV_RANK = {s: i for i, s in enumerate(SEVERITIES)}
_NUM = (int, float)  # checked with type(), so bool is not a number
_EPOCH = datetime(1970, 1, 1, tzinfo=UTC)
# a day inside Python's datetime range, so Postgres can hand them back in any session time zone
TS_MIN, TS_MAX = datetime(1, 1, 2, tzinfo=UTC), datetime(9999, 12, 30, tzinfo=UTC)
_DISCRIMINATOR_KEYS = frozenset(("type", "schema", "event", "eventType", "kind", "alertname"))

_condition = TypeAdapter(Condition)

Keys = tuple[str, ...]
Pred = Callable[[dict], bool]


# --- rules.md §1: paths ---

def compile_path(path: str) -> Keys:
    """Envelope-rooted keys; a first segment that is not an envelope field is looked up in `raw`."""
    keys = tuple(path.split("."))
    return keys if keys[0] in ENVELOPE_FIELDS else ("raw", *keys)


def _match_path(path: str) -> Keys:
    """Mapping `match` sees only {source, raw}, so any other first segment is a payload key."""
    keys = tuple(path.split("."))
    return keys if keys[0] in ("source", "raw") else ("raw", *keys)


def resolve(doc: Any, keys: Keys) -> Any:
    """Walk dict keys; returns MISSING when any step is absent or not a dict."""
    for k in keys:
        if type(doc) is not dict:
            return MISSING
        doc = doc.get(k, MISSING)
        if doc is MISSING:
            return MISSING
    return doc


# --- rules.md §2: conditions ---

def _glob(pattern: str) -> Callable[[str], bool]:
    """Whole-string `*` glob. Chunks are found left to right, so there is no backtracking."""
    if "*" not in pattern:
        return lambda s: s == pattern
    head, *mid, tail = pattern.split("*")

    def match(s: str) -> bool:
        end = len(s) - len(tail)
        if end < len(head) or not s.startswith(head) or not s.endswith(tail):
            return False
        i = len(head)
        for chunk in mid:
            i = s.find(chunk, i, end)
            if i < 0:
                return False
            i += len(chunk)
        return True
    return match


def _same(a: Any, b: Any) -> bool:
    """JSON equality: true is not 1, 1 equals 1.0, arrays and objects compare element by element."""
    if type(a) is bool or type(b) is bool:
        return a is b
    if type(a) is list:
        return type(b) is list and len(a) == len(b) and all(map(_same, a, b))
    if type(a) is dict:
        return type(b) is dict and a.keys() == b.keys() and all(_same(x, b[k]) for k, x in a.items())
    return a == b


def _leaf_test(op: str, v: Any) -> Callable[[Any], bool]:
    if op == "eq":
        return lambda x: _same(x, v)
    if op == "ne":
        return lambda x: not _same(x, v)
    if op == "gt":
        return lambda x: type(x) in _NUM and x > v
    if op == "gte":
        return lambda x: type(x) in _NUM and x >= v
    if op == "lt":
        return lambda x: type(x) in _NUM and x < v
    if op == "lte":
        return lambda x: type(x) in _NUM and x <= v
    if op == "in":
        return lambda x: any(_same(x, e) for e in v)
    if op == "glob":
        glob = _glob(v)
        return lambda x: type(x) is str and glob(x)
    if op == "sev_gte":
        rank = SEV_RANK[v]
        return lambda x: type(x) is str and SEV_RANK.get(x, -1) >= rank
    return lambda x: True  # exists: reaching the test means the path resolved


def _compile(c, path: Callable[[str], Keys] = compile_path) -> Pred:
    if isinstance(c, (AllOf, AnyOf)):
        preds = [_compile(n, path) for n in (c.all if isinstance(c, AllOf) else c.any)]
        want = isinstance(c, AnyOf)  # any: stop on the first True; all: stop on the first False

        def group(doc):
            for p in preds:
                if p(doc) is want:
                    return want
            return not want
        return group

    keys, test = path(c.field), _leaf_test(c.op, c.value)

    def leaf(doc):
        x = resolve(doc, keys)
        return x is not MISSING and test(x)
    return leaf


def compile_condition(cond: dict | Any) -> Pred:
    """Validate a condition tree (dict or model) and return a predicate over an envelope dict."""
    return _compile(_condition.validate_python(cond))


# --- templates ---

def _text(v: Any) -> str:
    return "" if v is MISSING or v is None else v if type(v) is str else json.dumps(v, ensure_ascii=False)


def _compile_template(t: str | None) -> list:
    parts = TEMPLATE_RE.split(t or "")  # odd indexes are the {{paths}}
    return [compile_path(p) if i % 2 else p for i, p in enumerate(parts) if p or i % 2]


def _render(parts: list, env: dict) -> str:
    return "".join(p if type(p) is str else _text(resolve(env, p)) for p in parts)


# --- rules.md §3: mappings and the envelope ---

@dataclass(slots=True, frozen=True)
class CompiledMapping:
    id: str
    priority: int
    match: Pred
    fields: tuple  # (envelope field, payload keys or None, const, ts format)
    severity_map: dict[str, str]
    dedup: tuple[Keys, ...]


def _validate_each(model, bodies: Iterable[dict]) -> tuple[list, list[tuple[Any, str]]]:
    """Validate rows one by one, so one bad row does not keep the others from loading."""
    ok, errors = [], []
    for i, b in enumerate(bodies):
        try:
            ok.append(model.model_validate(b))
        except ValidationError as e:
            errors.append((b.get("id", i) if type(b) is dict else i, str(e)))
    return ok, errors


def compile_mappings(bodies: Iterable[dict]) -> tuple[list[CompiledMapping], list[tuple[Any, str]]]:
    """Return the valid active mappings, highest priority first, and (row id or index, message) per invalid row."""
    out = []
    valid, errors = _validate_each(Mapping, bodies)
    for m in valid:
        if m.status != "active":
            continue
        fields = tuple(
            (name, None if "const" in spec.model_fields_set else tuple(spec.path.split(".")),
             spec.const, getattr(spec, "format", None))
            for name in ("type", "service", "severity", "env", "ts")
            if (spec := getattr(m.fields, name)) is not None
        )
        out.append(CompiledMapping(
            m.id, m.priority, _compile(m.match, _match_path), fields,
            {k.lower(): v for k, v in m.severity_map.items()},
            tuple(tuple(p.split(".")) for p in m.dedup_fields),
        ))
    out.sort(key=lambda m: (-m.priority, m.id))  # ties: by id, so the order does not depend on load order
    return out, errors


def _iso(v: Any, fmt: str) -> str | None:
    try:
        if fmt == "iso":
            if type(v) is not str:
                return None
            dt = datetime.fromisoformat(v)
            dt = dt.replace(tzinfo=UTC) if dt.tzinfo is None else dt.astimezone(UTC)
        else:
            if type(v) not in _NUM:
                return None
            dt = _EPOCH + timedelta(milliseconds=v) if fmt == "epoch_ms" else _EPOCH + timedelta(seconds=v)
    except (ValueError, OverflowError):
        return None
    if not TS_MIN <= dt <= TS_MAX:
        return None
    return dt.isoformat(timespec="milliseconds")[:-6] + "Z"


def _scalar(v: Any) -> str | None:
    return v if type(v) is str else str(v) if type(v) in _NUM else None


def build_envelope(event: dict, mappings: list[CompiledMapping]) -> dict:
    """Raw event dict (events.md §2) -> envelope dict (events.md §3). `mappings` come from compile_mappings."""
    source, payload, received = event["source_id"], event["payload"], event["received_at"]
    env = {
        "event_id": event["event_id"], "source": source, "type": "unmapped", "severity": "warning",
        "env": "unknown", "service": source, "ts": received, "dedup_hints": [],
        "mapped": False, "mapping_id": None, "received_at": received, "raw": payload,
    }
    pre = {"source": source, "raw": payload}  # what a mapping's match may see
    for m in mappings:
        if not m.match(pre):
            continue
        env["mapped"], env["mapping_id"] = True, m.id
        for name, keys, const, fmt in m.fields:
            v = const if keys is None else resolve(payload, keys)
            if name == "ts":
                v = _iso(v, fmt)
            else:
                v = _scalar(v)
                if name == "severity" and v is not None:
                    s = v.lower()
                    v = m.severity_map.get(s) or (s if s in SEV_RANK else None)
            if v is not None:
                env[name] = v
        env["dedup_hints"] = [None if (h := resolve(payload, k)) is MISSING else h for k in m.dedup]
        break
    return env


def fingerprint(source_id: str, payload: Any) -> str:
    """Stable hex id of an unmapped payload's shape, for schema_samples (storage.md)."""
    # shortcut: schemas that differ only in keys below depth 3 or in values of other fields still collide; add more discriminator keys when one bites
    paths, marks = set(), []

    def walk(d: dict, prefix: str, depth: int):
        for k, v in d.items():
            p = prefix + k
            paths.add(p)
            if depth <= 2 and k in _DISCRIMINATOR_KEYS and type(v) is str and len(v) <= 64:
                marks.append(f"{p}={v}")
            if depth < 3 and type(v) is dict:
                walk(v, p + ".", depth + 1)

    if type(payload) is dict:
        walk(payload, "", 1)
    blob = json.dumps([source_id, type(payload).__name__, sorted(paths), sorted(marks)])
    return hashlib.sha256(blob.encode()).hexdigest()


# --- rules.md §4: rule matching ---

@dataclass(slots=True, frozen=True)
class CompiledRule:
    id: str
    team_id: str
    when: Pred
    alert_key: tuple[Keys, ...] | None  # None: type, service + dedup_hints
    title: list
    body: list
    fields: tuple[tuple[str, Keys], ...]
    color: str | None
    rule: Rule  # the validated body, for what later stages need (window_s, route, runbook_id)


@dataclass(slots=True, frozen=True)
class RuleMatch:
    rule_id: str
    team_id: str
    alert_key: str
    title: str
    body: str
    fields: dict[str, Any]
    color: str | None
    severity: str | None  # the rule's fixed severity for its alerts; None: the event's


def compile_rules(bodies: Iterable[dict]) -> tuple[list[CompiledRule], list[tuple[Any, str]]]:
    """Return the valid enabled rules and (row id or index, message) per invalid row."""
    valid, errors = _validate_each(Rule, bodies)
    return [
        CompiledRule(
            r.id, r.team_id, _compile(r.when),
            None if r.alert_key is None else tuple(compile_path(p) for p in r.alert_key),
            # no title template: the rule's name, else "type · service", so no channel shows a blank title
            _compile_template(r.present.title) if r.present.title else [r.name] if r.name
            else _compile_template("{{type}} · {{service}}"),
            _compile_template(r.present.body),
            tuple((p, compile_path(p)) for p in r.present.fields), r.present.color, r,
        )
        for r in valid if r.enabled
    ], errors


def match_rules(env: dict, rules: list[CompiledRule]) -> list[RuleMatch]:
    """One RuleMatch per rule whose `when` holds on the envelope."""
    out = []
    for r in rules:
        if not r.when(env):
            continue
        if r.alert_key is None:
            values = [env["type"], env["service"], *env["dedup_hints"]]
        else:  # a missing value becomes null, so the key stays stable
            values = [None if (v := resolve(env, k)) is MISSING else v for k in r.alert_key]
        digest = hashlib.sha1(json.dumps(values, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
        out.append(RuleMatch(
            r.id, r.team_id, f"{r.id}:{digest}", _render(r.title, env), _render(r.body, env),
            {p: None if (v := resolve(env, k)) is MISSING else v for p, k in r.fields}, r.color, r.rule.severity,
        ))
    return out
