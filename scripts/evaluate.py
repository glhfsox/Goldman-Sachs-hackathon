# /// script
# requires-python = ">=3.11"
# dependencies = ["websockets>=12"]
# ///
"""Measure the running alerting stack: throughput, end-to-end latency per channel, Jev judges.

Run from the repo root while the whole stack is up (root docker-compose.yml); the Jev judges need the key:
    set -a; . backend/ai-worker/.env; set +a
    uv run scripts/evaluate.py --rate 1000 --duration 30 --probes 20
Writes docs/evaluation/<timestamp>.json and .md and prints the markdown.
EVAL_COMPOSE_DIR: the directory whose `docker compose` runs the stack (default: repo root).
"""
import argparse
import asyncio
import json
import os
import re
import subprocess
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

import websockets

ROOT = Path(__file__).resolve().parent.parent
COMPOSE_DIR = os.environ.get("EVAL_COMPOSE_DIR", str(ROOT))
API = "http://localhost:8000/api"
WS = "ws://localhost:8000/ws"
INGEST = "http://localhost:8001/ingest/alertmanager-http"  # any http source; mapping `workflow-failed` matches raw.type
RULE_ID = "eval-probe"
RULE = {
    "id": RULE_ID, "team_id": "settlements", "name": "Evaluation probe", "enabled": True,
    "when": {"all": [{"field": "type", "op": "eq", "value": "WORKFLOW_FAILED"},
                     {"field": "raw.workflowName", "op": "eq", "value": "eval-probe"}]},
    "alert_key": ["raw.workflowId"], "window_s": 30,
    # elena has no preferences, so no quiet hours or per-hour push limit suppress the probes
    "route": {"recipients": [{"user": "elena"}], "channels": ["ui", "email", "sms"]},
    "present": {"title": "eval probe {{workflowId}}"},
}
JEV_SCRIPTS = ["eval_run_risk.py", "eval_run_start.py", "eval_severity.py"]


def http(method, url, body=None):
    req = urllib.request.Request(url, method=method, data=None if body is None else json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json", "X-User": "elena"})
    try:
        with urllib.request.urlopen(req, timeout=10) as r:
            return r.status, json.loads(r.read() or b"null")
    except urllib.error.HTTPError as e:
        return e.code, None


def ts(iso):
    return datetime.fromisoformat(iso.replace("Z", "+00:00")).timestamp()


def pct(values):
    if not values:
        return {"count": 0}
    s = sorted(values)
    q = lambda p: s[min(len(s) - 1, int(round(p * (len(s) - 1))))]
    return {"count": len(s), "p50": q(0.5), "p95": q(0.95), "p99": q(0.99), "max": s[-1]}


def offsets():
    """(committed, log_end) of group `processor` on events.raw, summed over partitions."""
    out = subprocess.run(["docker", "compose", "exec", "-T", "kafka", "/opt/kafka/bin/kafka-consumer-groups.sh",
                          "--bootstrap-server", "localhost:9092", "--describe", "--group", "processor"],
                         cwd=COMPOSE_DIR, capture_output=True, text=True, timeout=30).stdout
    cur = end = 0
    for line in out.splitlines():
        f = line.split()
        if len(f) >= 5 and f[1] == "events.raw" and f[3].isdigit() and f[4].isdigit():
            cur, end = cur + int(f[3]), end + int(f[4])
    if not end:
        raise RuntimeError(f"no events.raw offsets for group processor:\n{out}")
    return cur, end


class Run:
    def __init__(self, args):
        self.args = args
        self.tag = datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
        self.samples = []  # (wall time, committed, log_end)
        self.opened = {}  # workflowId -> {alert_id, ws_at, start}
        self.other_opened = 0  # background alerts opened while measuring
        self.probes_sent = {}  # workflowId -> send time
        self.sim = None

    async def ws_listen(self):
        async with websockets.connect(WS, max_size=2**22) as ws:
            self.ws_ready.set()
            async for raw in ws:
                at = time.time()
                try:
                    m = json.loads(raw)
                    if not (isinstance(m, dict) and m.get("type") == "alert" and m.get("change") == "opened"):
                        continue
                    a = m["alert"]
                    if a.get("rule_id") != RULE_ID:
                        self.other_opened += 1
                        continue
                    wf = a["sample_event"]["raw"]["workflowId"]
                    self.opened.setdefault(wf, {"alert_id": str(a["id"]), "ws_at": at,
                                                "start": ts(a["first_received_at"])})
                except (ValueError, KeyError, TypeError, AttributeError):
                    continue  # malformed or another shape: not ours

    async def sample(self, stop):
        while not stop.is_set():
            cur, end = await asyncio.to_thread(offsets)
            self.samples.append((time.time(), cur, end))
            await asyncio.sleep(0.5)

    async def load(self):
        sim = ROOT / "data/simulator/.venv/bin/sim"
        if not sim.exists():
            raise SystemExit("run `bash check.sh` in data/simulator first")
        p = await asyncio.create_subprocess_exec(str(sim), "run", "load", "--rate", str(self.args.rate),
                                                 "--duration", str(self.args.duration),
                                                 stdout=asyncio.subprocess.DEVNULL, stderr=asyncio.subprocess.PIPE)
        err = (await p.communicate())[1].decode()
        done = next((l for l in err.splitlines() if "[sim] done" in l), "")
        sent, failed = (re.search(rf"{k} (\d+)", done) for k in ("sent", "failed"))
        self.sim = {"sent": int(sent[1]) if sent else None, "failed": int(failed[1]) if failed else None,
                    "summary": done.removeprefix("[sim] ").strip() or err[-500:]}

    async def probes(self):
        gap = self.args.duration / (self.args.probes + 1)
        for i in range(self.args.probes):
            await asyncio.sleep(gap)
            wf = f"EVAL-{self.tag}-{i:04d}"
            body = {"type": "WORKFLOW_FAILED", "workflowId": wf, "workflowName": "eval-probe", "step": "settle",
                    "severity": "critical", "environment": "prod",
                    "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
                    "error": {"code": "TIMEOUT", "message": "evaluation probe"}}
            status, _ = await asyncio.to_thread(http, "POST", INGEST, body)
            if status == 202:
                self.probes_sent[wf] = time.time()

    async def main(self):
        a = self.args
        status, _ = http("PUT", f"{API}/rules/{RULE_ID}", RULE)
        if status == 404:
            status, _ = http("POST", f"{API}/rules", RULE)
        if status not in (200, 201):
            raise SystemExit(f"could not save rule {RULE_ID}: HTTP {status}")
        try:
            print("rule saved; waiting 6 s for the processor and notifier to load it")
            await asyncio.sleep(6)
            self.ws_ready = asyncio.Event()
            ws_task = asyncio.create_task(self.ws_listen())
            await asyncio.wait_for(self.ws_ready.wait(), 10)
            stop = asyncio.Event()
            sampler = asyncio.create_task(self.sample(stop))
            while not self.samples:
                await asyncio.sleep(0.1)
            t0 = time.time()
            print(f"load {a.rate} msg/s for {a.duration} s, {a.probes} probes")
            await asyncio.gather(self.load(), self.probes())
            t_load_end = time.time()
            while self.samples[-1][0] < t_load_end or self.samples[-1][1] < self.samples[-1][2]:
                if time.time() - t_load_end > 120:
                    break
                await asyncio.sleep(0.5)
            t_drained = self.samples[-1][0] if self.samples[-1][1] >= self.samples[-1][2] else None
            stop.set()
            await sampler
            # probes: wait for the alert, then for every channel to report sent_at
            deadline = time.time() + 30
            while len(self.opened) < len(self.probes_sent) and time.time() < deadline:
                await asyncio.sleep(0.5)
            ws_task.cancel()
            chan = await asyncio.to_thread(self.channels)
        finally:
            RULE_OFF = dict(RULE, enabled=False)
            http("PUT", f"{API}/rules/{RULE_ID}", RULE_OFF)
        jev = {} if a.skip_jev else await asyncio.to_thread(run_jev)
        return self.report(t0, t_load_end, t_drained, chan, jev)

    def channels(self):
        lat = {}
        failed = {}
        deadline = time.time() + 60
        pending = {wf: o for wf, o in self.opened.items() if wf in self.probes_sent}
        while pending and time.time() < deadline:
            for wf, o in list(pending.items()):
                status, d = http("GET", f"{API}/alerts/{o['alert_id']}")
                notes = d.get("notifications") if isinstance(d, dict) else None
                if status != 200 or not isinstance(notes, list):
                    continue
                notes = [n for n in notes if isinstance(n, dict)]
                if {n.get("channel") for n in notes} >= set(RULE["route"]["channels"]) and \
                        all(n.get("sent_at") or n.get("status") in ("dead", "suppressed", "cancelled") for n in notes):
                    for n in notes:
                        if n.get("sent_at"):
                            lat.setdefault(n["channel"], []).append((ts(n["sent_at"]) - o["start"]) * 1000)
                        else:
                            failed[n.get("channel")] = failed.get(n.get("channel"), 0) + 1
                    del pending[wf]
            time.sleep(1)
        for wf in pending:
            failed["unfinished_alerts"] = failed.get("unfinished_alerts", 0) + 1
        return lat, failed

    def report(self, t0, t_load_end, t_drained, chan, jev):
        a, s = self.args, self.samples
        first = min(s, key=lambda x: abs(x[0] - t0))
        last = min(s, key=lambda x: abs(x[0] - t_load_end))
        span = last[0] - first[0]
        ingested, processed = last[2] - first[2], last[1] - first[1]
        rates = [((b[2] - p[2]) / (b[0] - p[0]), (b[1] - p[1]) / (b[0] - p[0])) for p, b in zip(s, s[1:]) if b[0] > p[0]]
        probe_lat = [(o["ws_at"] - o["start"]) * 1000 for wf, o in self.opened.items() if wf in self.probes_sent]
        lat_ms, chan_failed = chan
        bg_events = ingested - len(self.probes_sent)
        r = {
            "config": {"rate": a.rate, "duration_s": a.duration, "probes": a.probes, "started": self.tag,
                       "machine": f"one machine ({os.uname().sysname} {os.uname().machine}, Docker); "
                                  "all services share the host clock, so cross-service latencies are comparable"},
            "throughput": {
                "window_s": round(span, 1),
                "ingested_per_s": round(ingested / span, 1) if span else None,
                "processed_per_s": round(processed / span, 1) if span else None,
                "peak_ingested_per_s": round(max((x[0] for x in rates), default=0), 1),
                "peak_processed_per_s": round(max((x[1] for x in rates), default=0), 1),
                "lag_at_load_end": last[2] - last[1],
                "drain_s": round(t_drained - t_load_end, 1) if t_drained else None,
                "simulator": self.sim,
            },
            "latency_ms": {"ingest_to_ui": pct(probe_lat),
                           **{f"ingest_to_{c}": pct(v) for c, v in sorted(lat_ms.items())}},
            "probes": {"sent": len(self.probes_sent),
                       "lost": len([wf for wf in self.probes_sent if wf not in self.opened]),
                       "channel_not_sent": chan_failed},
            "dedup": {"background_events": bg_events, "background_alerts_opened": self.other_opened,
                      "duplicate_ratio": round(1 - self.other_opened / bg_events, 4) if bg_events > 0 else None},
            "jev": jev,
        }
        return r


def run_jev():
    if not os.environ.get("TYPESAFE_API_KEY"):
        return {"skipped": "TYPESAFE_API_KEY not set: `set -a; . backend/ai-worker/.env; set +a` first"}
    cwd = ROOT / "backend/ai-worker"
    results = {}
    for name in JEV_SCRIPTS:
        script = cwd / "scripts" / name
        if not script.exists():
            results[name] = {"skipped": "script not found"}
            continue
        out = Path(f"/tmp/evaluate-{name}.json")
        out.unlink(missing_ok=True)
        cmd = ["uv", "run", "python", f"scripts/{name}"] + (["--json", str(out)] if "--json" in script.read_text() else [])
        p = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, timeout=900)
        if out.exists():
            results[name] = json.loads(out.read_text())
        else:
            lines = [l for l in p.stdout.splitlines() if l.strip()]
            results[name] = {"summary": lines[-1] if lines else None, "exit": p.returncode,
                             **({"stderr_tail": p.stderr[-300:]} if p.returncode else {})}
    return results


def markdown(r):
    t, c = r["throughput"], r["config"]
    md = [f"# Evaluation {c['started']}", "",
          f"Load {c['rate']} msg/s for {c['duration_s']} s, {c['probes']} probes. {c['machine']}.", "",
          "| Throughput | Value |", "|---|---|",
          f"| Ingested (events.raw), sustained | {t['ingested_per_s']} events/s over {t['window_s']} s |",
          f"| Processed (group processor), sustained | {t['processed_per_s']} events/s |",
          f"| Peak ingested / processed (0.5-2 s samples) | {t['peak_ingested_per_s']} / {t['peak_processed_per_s']} events/s |",
          f"| Processor lag at load end | {t['lag_at_load_end']} events |",
          f"| Time to drain the lag | {t['drain_s']} s |",
          f"| Simulator | {(t['simulator'] or {}).get('summary')} |", "",
          "| Latency from ingest `received_at` (ms) | count | p50 | p95 | p99 | max |", "|---|---|---|---|---|---|"]
    for k, v in r["latency_ms"].items():
        md.append(f"| {k.replace('_', ' ')} | {v['count']} | " +
                  " | ".join(f"{v[q]:.0f}" if q in v else "-" for q in ("p50", "p95", "p99", "max")) + " |")
    p, d = r["probes"], r["dedup"]
    md += ["", f"Probes sent {p['sent']}, lost {p['lost']}, channel rows not sent {p['channel_not_sent'] or 'none'}. "
           f"Background: {d['background_events']} events, {d['background_alerts_opened']} alerts opened, "
           f"duplicate ratio {d['duplicate_ratio']}."]
    if r["jev"]:
        md += ["", "| Jev judge | Result |", "|---|---|"]
        md += [f"| {k} | {v['right']} of {v['cases']} right ({v['accuracy']:.0%}), dangerous misses {v['dangerous_misses']} |"
               if isinstance(v, dict) and "cases" in v else f"| {k} | {json.dumps(v)[:300]} |" for k, v in r["jev"].items()]
    return "\n".join(md) + "\n"


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--rate", type=int, default=1000)
    ap.add_argument("--duration", type=int, default=30)
    ap.add_argument("--probes", type=int, default=20)
    ap.add_argument("--skip-jev", action="store_true")
    run = Run(ap.parse_args())
    result = asyncio.run(run.main())
    out = ROOT / "docs/evaluation"
    out.mkdir(parents=True, exist_ok=True)
    (out / f"{run.tag}.json").write_text(json.dumps(result, indent=2) + "\n")
    md = markdown(result)
    (out / f"{run.tag}.md").write_text(md)
    print(md + f"\nwritten: {out / run.tag}.json, .md")
