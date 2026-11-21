"""Command line: `sim run`, `sim sample`, `sim list`. Run `sim -h` for help."""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import signal
import sys

import yaml

from .generate import Context
from .model import build_event, list_schemas, load_schema, load_sources
from .run import list_scenarios, load_scenario, run_scenario, total_rate
from .sinks import HttpSink, KafkaSink, Router, Stats, StdoutSink

DEFAULT_INGEST_URL = "http://localhost:8001"
DEFAULT_BROKERS = "localhost:9094"


def _overrides(pairs: list[str]) -> dict:
    out = {}
    for pair in pairs or []:
        path, _, value = pair.partition("=")
        out[path] = yaml.safe_load(value)
    return out


def _short(text: str, width: int = 90) -> str:
    text = " ".join((text or "").split())
    return text if len(text) <= width else text[: width - 1] + "…"


def cmd_list(args) -> int:
    print("schemas:")
    for name in list_schemas():
        s = load_schema(name)
        print(f"  {name:16} -> {s.source:18} {_short(s.description)}")
    print("scenarios:")
    for name in list_scenarios():
        print(f"  {name:16} {_short(load_scenario(name).get('description', ''))}")
    return 0


def cmd_sample(args) -> int:
    ctx, sources, schema = Context(seed=args.seed), load_sources(), load_schema(args.schema)
    for _ in range(args.n):
        e = build_event(schema, ctx, sources, overrides=_overrides(args.set), text=args.text or None)
        where = f"kafka {e.topic} key={e.key}" if e.kind == "kafka" else f"POST /ingest/{e.source_id}"
        print(f"# {where}  ({e.content_type})")
        payload = e.payload()
        print(json.dumps(payload, indent=2, ensure_ascii=False) if isinstance(payload, dict) else payload)
    return 0


async def _run(args) -> int:
    scenario = load_scenario(args.scenario)
    sources = load_sources()
    stats = Stats()
    if args.dry_run:
        out = StdoutSink(stats)
        sinks = {"kafka": out, "http": out}
    else:
        sinks = {}
        if args.only in (None, "kafka"):
            sinks["kafka"] = KafkaSink(stats, args.brokers)
        if args.only in (None, "http"):
            sinks["http"] = HttpSink(stats, args.ingest_url, concurrency=args.concurrency)
    router = Router(sinks)

    rate_scale = args.rate_scale
    if args.rate:
        base = total_rate(scenario)
        if base <= 0:
            print("--rate needs a scenario whose first phase has rate streams", file=sys.stderr)
            return 2
        rate_scale = args.rate / base

    stop = asyncio.Event()
    loop = asyncio.get_running_loop()
    for sig in (signal.SIGINT, signal.SIGTERM):
        loop.add_signal_handler(sig, stop.set)

    target = "stdout" if args.dry_run else f"kafka {args.brokers}, http {args.ingest_url}"
    print(f"[sim] scenario {scenario['name']} -> {target}", file=sys.stderr)
    try:
        result = await run_scenario(scenario, sources, router, stats, seed=args.seed, rate_scale=rate_scale,
                                    max_duration_s=args.duration, only_kind=args.only, stop=stop,
                                    progress=not args.quiet)
    finally:
        if not args.quiet:
            print("[sim] stopping: delivering what is still queued...", file=sys.stderr)
        await router.close()

    t = stats.totals()
    rate = t.sent / result.elapsed_s if result.elapsed_s else 0
    print(f"[sim] done in {result.elapsed_s:.1f} s: generated {result.generated}, sent {t.sent}, "
          f"failed {t.failed}, retries {t.retries}, {rate:.0f} msg/s", file=sys.stderr)
    for sid, c in sorted(stats.by_source.items()):
        print(f"[sim]   {sid:20} sent {c.sent:7}  failed {c.failed:5}  retries {c.retries:5}", file=sys.stderr)
    return 1 if t.failed and not t.sent else 0


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(prog="sim", description="Simulated sources for the alerting service.")
    sub = p.add_subparsers(dest="cmd", required=True)

    r = sub.add_parser("run", help="play a scenario (demo, steady, burst, load, edge or a path)")
    r.add_argument("scenario")
    r.add_argument("--dry-run", action="store_true", help="print events as JSON lines instead of sending")
    r.add_argument("--duration", type=float, help="stop after this many seconds")
    r.add_argument("--rate", type=float, help="total events/s of the first phase (scales every stream)")
    r.add_argument("--rate-scale", type=float, default=1.0, help="multiply every stream's rate")
    r.add_argument("--only", choices=["kafka", "http"], help="send only through this transport")
    r.add_argument("--seed", type=int, help="override the scenario's seed")
    r.add_argument("--concurrency", type=int, default=64, help="parallel HTTP requests")
    r.add_argument("--ingest-url", default=os.environ.get("INGEST_URL", DEFAULT_INGEST_URL))
    r.add_argument("--brokers", default=os.environ.get("SOURCE_KAFKA_BROKERS", DEFAULT_BROKERS))
    r.add_argument("-q", "--quiet", action="store_true")
    r.set_defaults(func=lambda a: asyncio.run(_run(a)))

    s = sub.add_parser("sample", help="print example payloads of one schema")
    s.add_argument("schema")
    s.add_argument("-n", type=int, default=3)
    s.add_argument("--seed", type=int, default=0)
    s.add_argument("--set", action="append", metavar="PATH=VALUE", help="override a field, e.g. error.code=TIMEOUT")
    s.add_argument("--text", action="store_true", help="the plain-text form, if the schema has one")
    s.set_defaults(func=cmd_sample)

    lst = sub.add_parser("list", help="list schemas and scenarios")
    lst.set_defaults(func=cmd_list)

    args = p.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
