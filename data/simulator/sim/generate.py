"""Field generators: turn a schema's `fields` spec into one payload.

A spec node is one of:
- a literal (string, number, bool, null, list): copied as is;
- an object: a dict without a generator key; each key is generated in order;
- a generator: a dict with one generator key (`$choice`, `$faker`, ...), see GENERATORS.

Any node may also carry modifiers:
- `$p: 0.3`                           present with this probability, otherwise the field is left out;
- `$if: {path: type, in: [A, B]}`     present only if an already generated field matches
                                      (`in`, `not_in`, `eq`, `ne`).

Paths are dot paths into the payload generated so far (`error.code`). Fields are generated in file order,
so `$if`, `$ref` and `{placeholders}` only see fields above them.
"""

from __future__ import annotations

import copy
import math
import random
import re
from collections.abc import Callable
from dataclasses import dataclass, field
from datetime import UTC, datetime, timedelta
from typing import Any

from faker import Faker

MISSING = object()  # a field that is left out of the payload


@dataclass
class Context:
    """Randomness and counters shared by every generated event of one run (seeded = reproducible)."""

    seed: int = 0
    now: Callable[[], datetime] = lambda: datetime.now(UTC)
    rng: random.Random = field(init=False)
    faker: Faker = field(init=False)
    counters: dict[str, int] = field(default_factory=dict)

    def __post_init__(self) -> None:
        self.rng = random.Random(self.seed)
        self.faker = Faker()
        self.faker.seed_instance(self.seed)

    def next_seq(self, name: str) -> int:
        self.counters[name] = self.counters.get(name, 0) + 1
        return self.counters[name]


def get_path(doc: Any, path: str) -> Any:
    cur = doc
    for part in path.split("."):
        if isinstance(cur, dict) and part in cur:
            cur = cur[part]
        else:
            return MISSING
    return cur


# --- generators -------------------------------------------------------------------------------------------

def _choice(spec, ctx, doc):
    values = spec["$choice"]
    weights = spec.get("$weights")
    return copy.deepcopy(ctx.rng.choices(values, weights=weights, k=1)[0])


def _faker(spec, ctx, doc):
    method = getattr(ctx.faker, spec["$faker"])
    return method(*spec.get("$args", []), **spec.get("$kwargs", {}))


def _int(spec, ctx, doc):
    lo, hi = spec["$int"]
    return ctx.rng.randint(lo, hi)


def _float(spec, ctx, doc):
    lo, hi = spec["$float"]
    return round(ctx.rng.uniform(lo, hi), spec.get("$round", 2))


def _lognormal(spec, ctx, doc):
    """Skewed positive numbers (amounts, latencies): most near `median`, a long tail above it."""
    median = spec["$lognormal"]
    value = ctx.rng.lognormvariate(math.log(median), spec.get("$sigma", 1.0))
    value = max(spec.get("$min", value), min(spec.get("$max", value), value))
    if spec.get("$round_to"):
        value = round(value / spec["$round_to"]) * spec["$round_to"]
    return int(value) if spec.get("$as", "int") == "int" else round(value, 2)


_NOW_FORMATS = {
    "iso_s": lambda t: t.strftime("%Y-%m-%dT%H:%M:%SZ"),
    "iso_ms": lambda t: t.strftime("%Y-%m-%dT%H:%M:%S.") + f"{t.microsecond // 1000:03d}Z",
    "epoch_ms": lambda t: int(t.timestamp() * 1000),
    "epoch_s": lambda t: int(t.timestamp()),
}


def _now(spec, ctx, doc):
    """Current time in one of _NOW_FORMATS, optionally shifted by `$offset_s` (negative = in the past)."""
    t = ctx.now() + timedelta(seconds=spec.get("$offset_s", 0))
    return _NOW_FORMATS[spec["$now"]](t)


_PLACEHOLDER = re.compile(r"\{([A-Za-z_][\w.]*)(?::([^}]*))?\}")


def render_text(template: str, ctx: Context, doc: Any, seq_name: str) -> str:
    """Fill `{path}` / `{path:fmt}` from the payload; `{seq}`, `{date}`, `{time}` are built in.
    A missing path renders as an empty string."""

    def value_of(name: str):
        if name == "seq":
            return ctx.next_seq(seq_name)
        if name == "date":
            return ctx.now().strftime("%Y%m%d")
        if name == "time":
            return ctx.now().strftime("%H%M%S")
        v = get_path(doc, name)
        return "" if v is MISSING else v

    def repl(m: re.Match) -> str:
        v = value_of(m.group(1))
        return format(v, m.group(2)) if m.group(2) else str(v)

    return _PLACEHOLDER.sub(repl, template)


def _pattern(spec, ctx, doc):
    """`WF-{date}-{seq:04d}`, `T-#####`: placeholders first, then Faker's `#` (digit) and `?` (letter)."""
    text = render_text(spec["$pattern"], ctx, doc, seq_name=spec["$pattern"])
    return ctx.faker.bothify(text, letters=spec.get("$letters", "abcdefghijklmnopqrstuvwxyz"))


def _map(spec, ctx, doc):
    """Pick by an earlier field: {$map: error.code, $values: {TIMEOUT: "...", ...}, $default: ...}.
    Each value is itself a spec, so it can be a generator."""
    key = get_path(doc, spec["$map"])
    values = spec.get("$values", {})
    if key in values:
        return generate(values[key], ctx, doc)
    if "$default" in spec:
        return generate(spec["$default"], ctx, doc)
    return MISSING


def _ref(spec, ctx, doc):
    v = get_path(doc, spec["$ref"])
    return copy.deepcopy(v)


def _const(spec, ctx, doc):
    return copy.deepcopy(spec["$const"])


GENERATORS: dict[str, Callable[[dict, Context, Any], Any]] = {
    "$const": _const,
    "$choice": _choice,
    "$faker": _faker,
    "$int": _int,
    "$float": _float,
    "$lognormal": _lognormal,
    "$now": _now,
    "$pattern": _pattern,
    "$ref": _ref,
    "$map": _map,
}


# --- tree walking -----------------------------------------------------------------------------------------

def _condition_holds(cond: dict, doc: Any) -> bool:
    v = get_path(doc, cond["path"])
    if "in" in cond:
        return v in cond["in"]
    if "not_in" in cond:
        return v not in cond["not_in"]
    if "eq" in cond:
        return v == cond["eq"]
    if "ne" in cond:
        return v != cond["ne"]
    raise ValueError(f"$if needs in, not_in, eq or ne: {cond}")


def generate(spec: Any, ctx: Context, root: dict | None = None) -> Any:
    """Generate one value from a spec node. `root` is the payload being built (for paths)."""
    if not isinstance(spec, dict):
        return copy.deepcopy(spec)

    if "$p" in spec and ctx.rng.random() >= spec["$p"]:
        return MISSING
    if "$if" in spec and not _condition_holds(spec["$if"], root if root is not None else {}):
        return MISSING

    gen_keys = [k for k in spec if k in GENERATORS]
    if len(gen_keys) > 1:
        raise ValueError(f"one generator per node, got {gen_keys}")
    if gen_keys:
        return GENERATORS[gen_keys[0]](spec, ctx, root if root is not None else {})

    obj: dict = {}
    _fill(spec, ctx, obj, root if root is not None else obj)
    return obj


def generate_payload(fields: dict, ctx: Context) -> dict:
    """Generate a whole payload; nested objects resolve paths from the payload root."""
    payload: dict = {}
    _fill(fields, ctx, payload, payload)
    return payload


def _fill(spec: dict, ctx: Context, target: dict, root: dict) -> None:
    unknown = [k for k in spec if k.startswith("$") and k not in ("$p", "$if")]
    if unknown:
        raise ValueError(f"unknown generator or modifier {unknown} (known: {sorted(GENERATORS)}, $p, $if)")
    for key, sub in spec.items():
        if key.startswith("$"):
            continue
        if isinstance(sub, dict) and not any(k in GENERATORS for k in sub):
            if "$p" in sub and ctx.rng.random() >= sub["$p"]:
                continue
            if "$if" in sub and not _condition_holds(sub["$if"], root):
                continue
            target[key] = {}
            _fill(sub, ctx, target[key], root)
            continue
        value = generate(sub, ctx, root)
        if value is not MISSING:
            target[key] = value


def apply_overrides(fields: dict, overrides: dict | None) -> dict:
    """Return a copy of `fields` with spec nodes replaced at dot paths, e.g. {"error.code": "TIMEOUT"}.
    A value may be a literal or a generator node. Nested dict overrides are flattened to dot paths."""
    result = copy.deepcopy(fields)
    for path, value in _flatten(overrides or {}).items():
        parts = path.split(".")
        node = result
        for part in parts[:-1]:
            nxt = node.get(part)
            if not isinstance(nxt, dict) or any(k in GENERATORS for k in nxt):
                nxt = {}
                node[part] = nxt
            node = nxt
        node[parts[-1]] = copy.deepcopy(value)
    return result


def _flatten(d: dict, prefix: str = "") -> dict:
    out: dict = {}
    for k, v in d.items():
        path = f"{prefix}{k}"
        if isinstance(v, dict) and v and not any(key.startswith("$") for key in v):
            out.update(_flatten(v, path + "."))
        else:
            out[path] = v
    return out
