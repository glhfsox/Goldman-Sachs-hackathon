"""Run the live risk check (`runs.risk`) on the labelled cases in evals/run_risk_golden.jsonl against the real Jev.

    set -a; . ./.env; set +a; uv run python scripts/eval_run_risk.py [--repeat N] [--json PATH]

Every case is one Jev request through the same code a `run_risk` job runs. A `clear` on a case labelled `hold`
is the dangerous miss (the run would start by itself); a `hold` on a `clear` case only costs a person a click.
"""

import argparse
import json
import sys
from collections import Counter
from datetime import UTC, datetime, timedelta
from pathlib import Path
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parent.parent
sys.path[:0] = [str(ROOT), str(ROOT.parent / "processor")]  # as pytest's pythonpath: ai_worker imports processor.core
from ai_worker import judge, runs  # noqa: E402

CASES = ROOT / "evals" / "run_risk_golden.jsonl"


def verdict(case: dict) -> dict:
    posted = {}
    api = SimpleNamespace(
        get=lambda *_: {"alert": {k: case["alert"][k] for k in ("title", "severity")}
                        | {"sample_event": {k: case["alert"][k] for k in ("type", "service", "env")}}},
        post=lambda _path, body: posted.update(body))
    now = datetime.now(UTC)
    run = {"id": case["id"], "alert_id": "A-" + case["id"], "rule_id": "r", "risk": "auto", "trigger": "auto",
           "start_at": (now + timedelta(seconds=30)).isoformat(), "nodes": case["nodes"]}
    runs.risk(run, SimpleNamespace(api=api, rules={"r": case["rule"]}, now=lambda: now, ask_jev=judge.ask_jev))
    return posted


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--repeat", type=int, default=1, help="ask each case this many times (stability)")
    ap.add_argument("--json", help="also write a summary object to this path (a case is right when every repeat is)")
    args = ap.parse_args()
    repeat = args.repeat
    cases = [json.loads(line) for line in CASES.read_text().splitlines() if line.strip()]
    tally, flips, misses, danger = Counter(), 0, [], 0
    for c in cases:
        got = [verdict(c) for _ in range(repeat)]
        flips += len({g["verdict"] for g in got}) > 1
        for g in got:
            tally[c["expected"], g["verdict"]] += 1
        g = got[0]
        mark = "ok  " if all(x["verdict"] == c["expected"] for x in got) else "MISS"
        if mark == "MISS":
            misses.append(c["id"])
            danger += c["expected"] == "hold" and any(x["verdict"] == "clear" for x in got)
        print(f"{mark} {c['id']:<40} expected {c['expected']:<5} got {g['verdict']:<5} {g['confidence']:.2f}"
              f"  {'; '.join(g['reasons'])}")
    n = sum(tally.values())
    right = tally["clear", "clear"] + tally["hold", "hold"]
    print(f"\n{right} of {n} right ({right / n:.0%}); dangerous misses (hold expected, clear given): "
          f"{tally['hold', 'clear']}; needless holds (clear expected, hold given): {tally['clear', 'hold']}"
          + (f"; cases whose verdict changed between repeats: {flips} of {len(cases)}" if repeat > 1 else ""))
    if args.json:
        k = len(cases) - len(misses)
        Path(args.json).write_text(json.dumps({
            "judge": "run_risk", "cases": len(cases), "right": k, "accuracy": k / len(cases), "dangerous_misses": danger,
            "other_misses": len(misses) - danger, "misses": misses, "repeat": repeat,
            "measured_at": datetime.now(UTC).isoformat(timespec="seconds")}, indent=2) + "\n")


if __name__ == "__main__":
    main()
