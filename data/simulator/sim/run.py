"""Scenario runner: plays phases of event streams at given rates through a Router.

Scenario file (scenarios/*.yaml):
  name, description, seed
  background: [stream, ...]        runs under every phase whose `background` is not false
  phases:
    - name, duration_s (null = until stopped), background (default true)
      streams:
        - schema: workflow_event   a file in schemas/
          rate: 8                  events per second, Poisson arrivals
          count: 1                 or: exactly this many, spread evenly over the phase
          overrides: {error.code: TIMEOUT}   fixed values or generators at dot paths
          source: some-id          send as another source id (unknown id = HTTP, expect 404)
          text: true               force the plain-text form (schemas with `text`)
          corrupt: true            cut the JSON short (invalid-JSON edge case)
          pool: 1000               pre-build this many events and cycle them (load tests)
"""

from __future__ import annotations

import asyncio
import itertools
import math
import random
import sys
import time
from dataclasses import dataclass
from pathlib import Path

import yaml

from .generate import Context
from .model import SCENARIOS_DIR, Event, Schema, Source, build_event, load_schema
from .sinks import Router, Stats

TICK_S = 0.05
PROGRESS_EVERY_S = 5.0


def load_scenario(name_or_path: str) -> dict:
    path = Path(name_or_path)
    if not path.suffix:
        path = SCENARIOS_DIR / f"{name_or_path}.yaml"
    scenario = yaml.safe_load(path.read_text())
    for phase in scenario.get("phases", []):
        for spec in phase.get("streams", []) + scenario.get("background", []):
            load_schema(spec["schema"])  # fail early on a typo
    return scenario


def list_scenarios() -> list[str]:
    return sorted(p.stem for p in SCENARIOS_DIR.glob("*.yaml"))


def phase_streams(scenario: dict, phase: dict) -> list[dict]:
    streams = list(phase.get("streams", []))
    if phase.get("background", True):
        streams += scenario.get("background", [])
    return streams


def total_rate(scenario: dict) -> float:
    """Events per second of the first phase (rate streams only): the base that `--rate` scales."""
    first = scenario["phases"][0]
    return sum(s.get("rate", 0) for s in phase_streams(scenario, first))


class Stream:
    def __init__(self, spec: dict, schema: Schema, ctx: Context, sources: dict[str, Source],
                 duration_s: float | None, rate_scale: float) -> None:
        self.spec, self.schema, self.ctx, self.sources = spec, schema, ctx, sources
        self.rate = spec.get("rate", 0) * rate_scale
        self.count = spec.get("count")
        self.duration_s = duration_s
        self.emitted = 0
        self.pool = None
        if spec.get("pool"):
            self.pool = itertools.cycle([self._build() for _ in range(spec["pool"])])

    def _build(self) -> Event:
        s = self.spec
        return build_event(self.schema, self.ctx, self.sources, overrides=s.get("overrides"),
                           source_id=s.get("source"), text=s.get("text"), corrupt=s.get("corrupt", False))

    def next(self) -> Event:
        self.emitted += 1
        return next(self.pool) if self.pool else self._build()

    def due(self, elapsed_s: float, dt: float, rng: random.Random) -> int:
        """How many events to send in the tick that ends at `elapsed_s` into the phase."""
        if self.count is not None:
            if self.emitted >= self.count:
                return 0
            span = self.duration_s or 0
            # event i goes out at i * span / count (the first one at the phase start)
            target = self.count if not span else min(self.count, math.floor(elapsed_s * self.count / span) + 1)
            return max(0, target - self.emitted)
        return poisson(self.rate * dt, rng)


def poisson(lam: float, rng: random.Random) -> int:
    if lam <= 0:
        return 0
    if lam > 30:  # normal approximation, plenty for pacing
        return max(0, round(rng.gauss(lam, math.sqrt(lam))))
    limit, k, p = math.exp(-lam), 0, 1.0
    while True:
        p *= rng.random()
        if p <= limit:
            return k
        k += 1


@dataclass
class RunResult:
    stats: Stats
    elapsed_s: float
    generated: int
    stopped: bool


async def run_scenario(scenario: dict, sources: dict[str, Source], router: Router, stats: Stats, *,
                       seed: int | None = None, rate_scale: float = 1.0, max_duration_s: float | None = None,
                       only_kind: str | None = None, stop: asyncio.Event | None = None,
                       progress: bool = True, clock=time.monotonic, sleep=asyncio.sleep) -> RunResult:
    seed = scenario.get("seed", 0) if seed is None else seed
    ctx = Context(seed=seed)
    arrivals = random.Random(seed + 1)  # timing randomness kept apart from payload randomness
    stop = stop or asyncio.Event()
    schemas: dict[str, Schema] = {}
    run_start = clock()
    generated = 0
    last_report, last_sent = run_start, 0

    for phase in scenario["phases"]:
        duration = phase.get("duration_s")
        if max_duration_s is not None:
            left = max_duration_s - (clock() - run_start)
            if left <= 0:
                break
            duration = left if duration is None else min(duration, left)
        streams = []
        for spec in phase_streams(scenario, phase):
            schema = schemas.setdefault(spec["schema"], load_schema(spec["schema"]))
            source = sources.get(spec.get("source") or schema.source)
            kind = source.kind if source else "http"
            if only_kind and kind != only_kind:
                continue
            streams.append(Stream(spec, schema, ctx, sources, duration, rate_scale))
        if progress:
            length = "until stopped" if duration is None else f"{duration:.0f} s"
            print(f"[sim] phase: {phase.get('name', '?')} ({length})", file=sys.stderr)

        phase_start = last_tick = clock()
        while not stop.is_set():
            now = clock()
            elapsed = now - phase_start
            if duration is not None and elapsed >= duration:
                break
            dt, last_tick = now - last_tick, now
            for stream in streams:
                for _ in range(stream.due(elapsed, dt, arrivals)):
                    await router.send(stream.next())
                    generated += 1
            if progress and now - last_report >= PROGRESS_EVERY_S:
                t = stats.totals()
                rate = (t.sent - last_sent) / (now - last_report)
                print(f"[sim] {now - run_start:6.1f} s  sent {t.sent}  failed {t.failed}  "
                      f"retries {t.retries}  {rate:.0f} msg/s", file=sys.stderr)
                last_report, last_sent = now, t.sent
            await sleep(max(0.0, TICK_S - (clock() - now)))
        if stop.is_set():
            break

    return RunResult(stats, clock() - run_start, generated, stop.is_set())
