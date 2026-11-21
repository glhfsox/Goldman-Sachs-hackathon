"""Run the start-mode judge (`runs.start`) on the labelled histories in evals/run_start_golden.jsonl against the
real Jev.

    set -a; . ./.env; set +a; uv run python scripts/eval_run_start.py

`expected` is the proposal the job should file (`auto`, `approval`) or null for none. An `auto` proposal on a case
that expects none or `approval` is the miss that matters: it pushes people toward more automation.
"""

import json
import sys
from pathlib import Path
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parent.parent
sys.path[:0] = [str(ROOT), str(ROOT.parent / "processor")]  # as pytest's pythonpath: ai_worker imports processor.core
from ai_worker import judge, runs  # noqa: E402


def main() -> None:
    cases = [json.loads(line) for line in (ROOT / "evals" / "run_start_golden.jsonl").read_text().splitlines() if line]
    right = wrong_auto = 0
    for c in cases:
        latest = {"alert_id": "A1", "team_id": "t", "trigger": "manual", "actions": c["actions"]}
        ctx = SimpleNamespace(rules={"r": c["rule"]}, ask_jev=judge.ask_jev, run_stats=lambda *_, c=c, la=latest: (c["stats"], la),
                              api=SimpleNamespace(post=lambda *_: {"id": "prop"}))
        out = runs.start({"rule_id": "r", "runbook_id": "rb"}, ctx)
        a, got = out["answer"], out["proposed"]
        right += got == c["expected"]
        wrong_auto += got == "auto" != c["expected"]
        print(f"{'ok  ' if got == c['expected'] else 'MISS'} {c['id']:<42} expected {c['expected'] or 'none':<8} got {got or 'none':<8} "
              f"Jev {a['choice']:<8} confidence {a['confidence']:.2f} p(auto) {a['probabilities']['auto']:.2f}")
    print(f"\n{right} of {len(cases)} right; `auto` proposed where it should not be: {wrong_auto}")


if __name__ == "__main__":
    main()
