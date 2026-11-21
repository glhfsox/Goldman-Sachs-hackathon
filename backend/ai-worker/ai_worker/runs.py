"""Two Jev judges for runbook runs (issue #75, on the run model of #73), both on TypeSafe like the severity judge.

- `run_risk`: an `auto` run waiting in its cancel window gets a verdict: `clear` (starts at `start_at`) or `hold`
  (waits for a person). The judge can only hold; the api holds by itself when no verdict came by `start_at`.
- `run_start`: every 10 finished runs of a runbook on a rule, a `runbook_start` proposal when how they start should
  change. A person accepts or rejects it.

The api is polled (`GET /runs`); finished runs are copied into `run_history`.
"""

import json
from datetime import UTC, datetime, timedelta
from urllib.parse import quote

import psycopg
from psycopg.types.json import Jsonb

from .api import ApiError
from .judge import FACTOR_P, _answers, _cap, _ts

CLEAR_MIN = 0.6  # Jev must choose `clear` with at least this confidence, else the run is held
START_MIN = 0.6  # a `runbook_start` proposal needs at least this confidence
EVERY_RUNS = 10  # a start-mode job per this many finished runs of a (rule, runbook) pair; fewer recent ones: silent
MAX_RUNS, MAX_RUN_AGE_DAYS = 50, 30  # the stats window: most recent finished runs, and how old they may be
NODES_CAP = 16000  # characters of the run's nodes sent to Jev
POLL_TIMEOUT_S, FINISHED_LIMIT = 2, 500
OVERLAP = timedelta(minutes=5)  # re-read this much before the newest run we hold: one committed late is not missed
FINISHED = ("done", "failed", "cancelled")
FACTORS = {  # name -> (question to Jev, the reason shown to people when the answer is yes)
    "production": ("Do these actions act on a production system?", "it runs in production"),
    "customer_impact": ("Are customers or money affected right now?", "customers or money are affected right now"),
    "hard_to_undo": ("Would any of these actions be hard to undo?", "the action cannot be undone easily"),
    "other_team": ("Do these actions reach another team or another system?", "it reaches another team or system"),
}
RISK_QUESTION = {
    "type": "choice",
    "instructions": "This runbook run will start by itself when its cancel window ends. The state holds its actions "
                    "with their filled parameters, the alert it runs on and the alert's rule. Can it start without "
                    "a person looking at it first?",
    "criteria": {"clear": "Safe to start by itself: the actions are routine for this alert and easy to undo.",
                 "hold": "A person should approve it first: it could do real harm in this situation."}}
START_QUESTION = {
    "type": "choice",
    "instructions": "How should runs of this runbook on this rule start from now on? The state holds the actions, "
                    "the rule and how the most recent runs went. Decide in two steps. First: if a run could move "
                    "money or touch payments, settlements, payouts or trades in production, the answer is approval, "
                    "whatever the history says. Staging, UAT and dev are not production, and restarting a "
                    "service, retrying a report or a log job, or opening a ticket moves no money. Otherwise "
                    "judge by the history.",
    "criteria": {"auto": "Start by themselves (after the cancel window and the live risk check). Only when no money, "
                         "payment, settlement, payout or trade in production is touched, and people approved nearly "
                         "every run quickly, nearly every alert got a run, and runs rarely failed or got cancelled.",
                 "approval": "Each run waits for a person. Always when a run could move money or touch payments, "
                             "settlements, payouts or trades in production, however clean the history. Also when "
                             "runs get cancelled, fail, are held by the risk check or wait long for approval, or "
                             "most alerts get no run."}}

UPSERT = """
INSERT INTO run_history (run_id, alert_id, rule_id, runbook_id, team_id, trigger, status, risk_check, actions,
                         start_at, approved_by, approved_at, started_at, finished_at, cancelled_by)
VALUES (%(id)s, %(alert_id)s, %(rule_id)s, %(runbook_id)s, %(team_id)s, %(trigger)s, %(status)s, %(risk_check)s,
        %(actions)s, %(start_at)s, %(approved_by)s, %(approved_at)s, %(started_at)s, %(finished_at)s,
        %(cancelled_by)s)
ON CONFLICT (run_id) DO NOTHING"""

STATS = """
WITH r AS (SELECT run_history.*, a.opened_at FROM run_history LEFT JOIN alert_history a USING (alert_id)
           WHERE run_history.rule_id = %(rule)s AND runbook_id = %(runbook)s AND finished_at >= %(since)s
           ORDER BY finished_at DESC LIMIT %(max)s),
     al AS (SELECT alert_id FROM alert_history WHERE rule_id = %(rule)s AND opened_at >= %(since)s)
SELECT count(*), count(*) FILTER (WHERE trigger = 'auto'), count(*) FILTER (WHERE approved_by IS NOT NULL),
       percentile_cont(0.5) WITHIN GROUP (ORDER BY extract(epoch FROM approved_at - opened_at)),
       count(*) FILTER (WHERE status = 'cancelled' AND started_at IS NULL),
       count(*) FILTER (WHERE status = 'cancelled' AND started_at IS NOT NULL),
       count(*) FILTER (WHERE risk_check = 'hold'), count(*) FILTER (WHERE status = 'failed'),
       (SELECT count(*) FROM al),
       (SELECT count(*) FROM al WHERE NOT EXISTS (SELECT 1 FROM run_history h WHERE h.alert_id = al.alert_id))
FROM r"""


def _str(v) -> str | None:
    return v if type(v) is str and v else None


def _ids_ok(r) -> bool:
    return type(r) is dict and all(_str(r.get(k)) for k in ("id", "alert_id", "rule_id", "runbook_id"))


def _list(doc) -> list:
    if type(doc) is not list:
        raise ValueError(f"GET /runs did not return a list: {json.dumps(doc)[:200]}")
    return doc


def pending_jobs(api) -> list[tuple]:
    """`GET /runs?risk_check=pending` -> a `run_risk` job per valid run (a redelivered one enqueues nothing)."""
    return [(f"run_risk:{r['id']}", "run_risk", r) for r in _list(api.get("/runs?risk_check=pending", POLL_TIMEOUT_S))
            if _ids_ok(r) and type(r.get("nodes")) is list and _ts(r.get("start_at"))]


def sync_finished(conn, api, cursor: datetime | None, now: datetime) -> tuple[list[tuple], datetime | None]:
    """Copy finished runs since `cursor` (else the newest we hold) into `run_history`; a `run_start` job per pair that
    has reached a bucket of EVERY_RUNS (keyed by bucket, like the severity trigger). Returns the jobs and the new
    cursor: the newest `finished_at` of the page, skipped rows included, so a page of unusable runs still advances."""
    since = cursor or conn.execute("SELECT max(finished_at) FROM run_history").fetchone()[0]
    # shortcut: more than FINISHED_LIMIT runs inside OVERLAP would repeat forever; page by id if that happens
    q = (f"/runs?finished_since={quote((since - OVERLAP).astimezone(UTC).isoformat()) if since else '1970-01-01T00:00:00Z'}"
         f"&limit={FINISHED_LIMIT}")
    pairs = set()
    for r in _list(api.get(q, POLL_TIMEOUT_S)):
        finished_at = _ts(r.get("finished_at")) if type(r) is dict else None
        if finished_at is None or finished_at.tzinfo is None or finished_at > now + OVERLAP:
            continue  # a run dated in the future would hold the cursor there for good
        since = max(since, finished_at) if since else finished_at
        if not (_ids_ok(r) and r.get("status") in FINISHED):
            continue
        rc, nodes = r.get("risk_check"), r.get("nodes")
        try:
            conn.execute(UPSERT, {k: _str(r.get(k)) for k in ("id", "alert_id", "rule_id", "runbook_id", "team_id",
                                                               "trigger", "status", "approved_by", "cancelled_by")}
                         | {k: _ts(r.get(k)) for k in ("start_at", "approved_at", "started_at", "finished_at")}
                         | {"risk_check": _str(rc.get("verdict")) if type(rc) is dict else None,
                            "actions": Jsonb([{"action": n.get("action"), "risk": n.get("risk")} for n in nodes
                                              if type(n) is dict]) if type(nodes) is list else None})
        except (psycopg.DataError, UnicodeEncodeError):
            continue  # a run Postgres cannot store is left out of the history
        pairs.add((r["rule_id"], r["runbook_id"]))
    jobs = []
    for rule_id, runbook_id in sorted(pairs):
        n = conn.execute("SELECT count(*) FROM run_history WHERE rule_id = %s AND runbook_id = %s",
                         (rule_id, runbook_id)).fetchone()[0]
        if n >= EVERY_RUNS:
            jobs.append((f"run_start:{quote(rule_id, safe='')}:{quote(runbook_id, safe='')}:"
                         f"{n // EVERY_RUNS * EVERY_RUNS}", "run_start", {"rule_id": rule_id, "runbook_id": runbook_id}))
    return jobs, since


def stats(conn, rule_id: str, runbook_id: str, now: datetime) -> tuple[dict, dict | None]:
    """How the pair's most recent finished runs went, and its latest run (alert_id, team_id, trigger, actions)."""
    n, auto, approved, approve_s, cancel_before, cancel_after, held, failed, alerts, no_run = conn.execute(STATS, {
        "rule": rule_id, "runbook": runbook_id, "since": now - timedelta(days=MAX_RUN_AGE_DAYS), "max": MAX_RUNS}).fetchone()
    share = (lambda k: round(k / n, 3)) if n else (lambda k: None)
    latest = conn.execute("SELECT alert_id, team_id, trigger, actions FROM run_history WHERE rule_id = %s AND"
                          " runbook_id = %s ORDER BY finished_at DESC LIMIT 1", (rule_id, runbook_id)).fetchone()
    return ({"window": {"max_runs": MAX_RUNS, "max_age_days": MAX_RUN_AGE_DAYS}, "runs": n,
             "auto_started_share": share(auto), "approved_share": share(approved),
             "median_seconds_from_alert_to_approve": None if approve_s is None else round(approve_s),
             "cancelled_before_start_share": share(cancel_before), "cancelled_after_start_share": share(cancel_after),
             "held_share": share(held), "failed_share": share(failed),
             # the rule's alerts opened in the window, and those that got no run at all; null when we hold none
             "rule_alerts": alerts or None, "alerts_without_run": no_run if alerts else None,
             "alerts_without_run_share": round(no_run / alerts, 3) if alerts else None},
            latest and dict(zip(("alert_id", "team_id", "trigger", "actions"), latest)))


def _stat_reasons(s: dict) -> list[str]:
    n = s["runs"]
    k, cb, ca, h, f = (round((s[x] or 0) * n) for x in ("approved_share", "cancelled_before_start_share",
                                                          "cancelled_after_start_share", "held_share", "failed_share"))
    d = s["median_seconds_from_alert_to_approve"]
    out = [f"{k} of {n} runs were approved by a person" + (f", median {d} s after the alert" if k and d is not None
                                                            else "")]
    out.append(f"{cb + ca} of {n} runs were cancelled ({cb} before they started)" if cb + ca
               else "no run was cancelled")
    out.append(f"{f} of {n} runs failed" if f else "no run failed")
    if h:
        out.append(f"{h} of {n} runs were held by the risk check")
    if s["rule_alerts"]:
        out.append(f"{s['alerts_without_run']} of {s['rule_alerts']} alerts of this rule got no run"
                   if s["alerts_without_run"] else f"all {s['rule_alerts']} alerts of this rule got a run")
    return out


def _questions(choice: str, q: dict) -> dict:
    return {choice: q} | {k: {"type": "noul", "instructions": text} for k, (text, _) in FACTORS.items()}


def _factor_reasons(a: dict) -> list[str]:
    return [phrase for k, (_, phrase) in FACTORS.items() if a[k]["noul"] >= FACTOR_P]


def risk(run: dict, ctx) -> dict:
    """Job `run_risk`: post `clear` or `hold` for a scheduled `auto` run before its `start_at`."""
    if ctx.now() >= _ts(run["start_at"]):
        return {"verdict": None, "why": "start_at has passed: the api holds the run for a person"}
    try:
        alert = ctx.api.get(f"/alerts/{quote(run['alert_id'], safe='')}")["alert"]
    except ApiError as e:
        if e.status != 404:
            raise
        alert = None
    ev = alert.get("sample_event") if type(alert) is dict and type(alert.get("sample_event")) is dict else {}
    rule = ctx.rules.get(run["rule_id"]) or {}
    nodes = [_cap({k: n.get(k) for k in ("action", "risk", "params", "text")}) for n in run["nodes"] if type(n) is dict]
    if len(json.dumps(nodes, ensure_ascii=False)) > NODES_CAP:  # Jev would not see every node: never clear it
        return _post(ctx, run, {"verdict": "hold", "confidence": 1.0, "reasons": ["the run is too large to check"]})
    state = {"run": {"risk": run.get("risk"), "trigger": run.get("trigger"), "nodes": nodes},
             "alert": alert and _cap({"title": alert.get("title"), "severity": alert.get("severity")}
                                     | {k: ev.get(k) for k in ("type", "service", "env")}),
             "rule": _cap({"name": rule.get("name"), "when": rule.get("when")})}
    a = _answers(ctx.ask_jev(state, _questions("decision", RISK_QUESTION)), FACTORS, "decision", ("clear", "hold"))
    d = a["decision"]
    verdict = "clear" if d["choice"] == "clear" and min(d["confidence"], d["probabilities"]["clear"]) >= CLEAR_MIN \
        else "hold"
    reasons = _factor_reasons(a)
    if verdict == "hold" and d["choice"] == "clear":
        reasons.append(f"the risk check was not sure enough to clear it ({d['confidence']:.2f})")
    elif verdict == "hold" and not reasons:
        reasons.append("the risk check judged it too risky to start by itself")
    return _post(ctx, run, {"verdict": verdict, "confidence": d["probabilities"][verdict], "reasons": reasons[:6]}) \
        | {"answer": d}


def _post(ctx, run: dict, body: dict) -> dict:
    try:
        ctx.api.post(f"/runs/{quote(run['id'], safe='')}/risk-check", body)
    except ApiError as e:
        if e.status != 409:
            raise
        return body | {"posted": False, "why": "the run is no longer scheduled"}
    return body | {"posted": True}


def start(job_input: dict, ctx) -> dict:
    """Job `run_start`: a `runbook_start` proposal when Jev confidently picks another start mode than today's."""
    rule_id, runbook_id = job_input["rule_id"], job_input["runbook_id"]
    rule = ctx.rules.get(rule_id)
    if rule is None or rule.get("enabled") is False:
        return {"proposed": None, "why": f"rule {rule_id} is unknown, deleted or disabled"}
    s, latest = ctx.run_stats(rule_id, runbook_id)
    if s["runs"] < EVERY_RUNS:
        return {"proposed": None, "why": f"not enough recent runs: {s['runs']} in the last {MAX_RUN_AGE_DAYS} days, "
                                         f"{EVERY_RUNS} needed"}
    current = "auto" if s["auto_started_share"] >= 0.5 else "approval"  # one manual re-run does not flip it
    state = {"rule": _cap({"id": rule_id, "name": rule.get("name"), "when": rule.get("when")}),
             "runbook_id": runbook_id, **_cap({"actions": latest["actions"]}), "starts_today": current, "history": s}
    a = _answers(ctx.ask_jev(state, _questions("start", START_QUESTION)), FACTORS, "start", ("auto", "approval"))
    d = a["start"]
    answer = {"choice": d["choice"], "confidence": d["confidence"], "probabilities": d["probabilities"]}
    if d["choice"] == current or d["confidence"] < START_MIN:
        why = "same as today" if d["choice"] == current else f"confidence below {START_MIN}"
        return {"proposed": None, "why": why, "answer": answer}
    p = ctx.api.post(f"/alerts/{quote(latest['alert_id'], safe='')}/proposals", {"kind": "runbook_start", "body": {
        "rule_id": rule_id, "team_id": latest["team_id"], "runbook_id": runbook_id, "start": d["choice"],
        "current_start": current, "confidence": d["confidence"],
        "probabilities": {k: d["probabilities"][k] for k in ("auto", "approval")}, "stats": s,
        "reasons": (_stat_reasons(s) + _factor_reasons(a))[:6]}})
    return {"proposal_id": p["id"], "proposed": d["choice"], "answer": answer}
