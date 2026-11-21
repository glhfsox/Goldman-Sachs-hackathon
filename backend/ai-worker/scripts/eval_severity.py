"""Run the severity judge (`judge.handle`) on the labelled cases in evals/severity_golden.jsonl against the real Jev.

    set -a; . ./.env; set +a; uv run python scripts/eval_severity.py [--repeat N] [--json PATH]

Every case is one Jev request through the same code a `severity` job runs. `expected` is the proposal the job should
file (a severity) or null for none. The dangerous miss is a proposal lower than the case's right severity (`expected`,
or the rule's current one when none is expected) when that is `error` or `critical`: the alert would get quieter.
"""

import argparse
import json
import sys
from datetime import UTC, datetime
from pathlib import Path
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parent.parent
sys.path[:0] = [str(ROOT), str(ROOT.parent / "processor")]  # as pytest's pythonpath: ai_worker imports processor.core
from ai_worker import judge  # noqa: E402

RANK = {s: i for i, s in enumerate(judge.SEVERITIES)}


def run(c: dict) -> dict:
    ctx = SimpleNamespace(rules={c["rule"]["id"]: c["rule"]}, rule_stats=lambda _: c["stats"], ask_jev=judge.ask_jev,
                          api=SimpleNamespace(post=lambda *_: {"id": "prop"}))
    return judge.handle({"rule_id": c["rule"]["id"], "alert_id": "A-" + c["id"], "mode": c["mode"],
                         "example": c["alert"]}, ctx)


def dangerous(c: dict, got) -> bool:
    right = c["expected"] or c["rule"].get("severity")
    return got is not None and right in ("error", "critical") and RANK[got] < RANK[right]


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--repeat", type=int, default=1, help="ask each case this many times (stability)")
    ap.add_argument("--json", help="also write a summary object to this path")
    args = ap.parse_args()
    cases = [json.loads(line) for line in (ROOT / "evals" / "severity_golden.jsonl").read_text().splitlines() if line]
    right, danger, misses, flips = 0, 0, [], 0
    for c in cases:
        outs = [run(c) for _ in range(args.repeat)]
        gots = [o["proposed"] for o in outs]
        flips += len(set(gots)) > 1
        ok = all(g == c["expected"] for g in gots)
        right += ok
        if not ok:
            misses.append(c["id"])
            danger += any(dangerous(c, g) for g in gots)
        a = outs[0]["answer"]
        print(f"{'ok  ' if ok else 'MISS'} {c['id']:<48} {c['mode']:<7} current {c['rule'].get('severity') or 'none':<8} "
              f"expected {c['expected'] or 'none':<8} got {gots[0] or 'none':<8} Jev {a['choice']:<8} "
              f"confidence {a['confidence']:.2f}")
    print(f"\n{right} of {len(cases)} right; dangerous misses (lower than an error/critical label): {danger}; "
          f"other misses: {len(misses) - danger}"
          + (f"; cases whose answer changed between repeats: {flips} of {len(cases)}" if args.repeat > 1 else ""))
    if args.json:
        Path(args.json).write_text(json.dumps({
            "judge": "severity", "cases": len(cases), "right": right, "accuracy": right / len(cases),
            "dangerous_misses": danger, "other_misses": len(misses) - danger, "misses": misses,
            "repeat": args.repeat, "measured_at": datetime.now(UTC).isoformat(timespec="seconds")}, indent=2) + "\n")


if __name__ == "__main__":
    main()
