"""Job kind `severity`: propose what severity a rule should fix, from how people reacted to its alerts.

The history is the ai-worker's own table `alert_history`, fed from every `alerts.changes` record (`record`). One
TypeSafe Jev call per job (a decision model: typed choices with probabilities, never text); a confident answer that
differs from the rule's `severity` (rules.md §4) is filed as a `rule_severity` proposal a person accepts or rejects.
"""

import json
import os
import urllib.error
import urllib.request
from datetime import UTC, datetime, timedelta
from urllib.parse import quote

from .agent import JobFailed
from .api import ApiError, file_proposal
from .runbooks import _capped

SEVERITIES = ("info", "warning", "error", "critical")
EVERY = 20  # one history job per this many resolved alerts of a rule; fewer recent ones: silent
MAX_ALERTS, MAX_AGE_DAYS = 200, 30  # the stats window: most recent resolved alerts, and how old they may be
MIN_CONFIDENCE = 0.5  # below it nothing is proposed
FACTOR_P = 0.7  # a factor with at least this probability of yes becomes a reason
PART_CAP = 2000  # characters of each value in the state; a longer one is sent as cut JSON text
TIMEOUT_S = 60

SEVERITY_QUESTION = {
    "type": "choice",
    "instructions": "Which severity should alerts of this rule have, judged by what the on-call person must do when "
                    "one fires? The state holds the rule, an example alert and, when there is any, how people reacted "
                    "to the rule's recent alerts.",
    "criteria": {"info": "Nobody needs to act; it is for the record.",
                 "warning": "Someone should look at it during working hours.",
                 "error": "Someone must act on it today.",
                 "critical": "Someone must act now, even if that means waking them up."}}
# name -> (question to Jev, the reason shown to people when the answer is yes)
FACTORS = {
    "production": ("Does this alert come from a production environment?", "it comes from production"),
    "customer_impact": ("Are customers or money affected right now when this alert fires?",
                        "customers or money are affected right now"),
}
HISTORY_FACTORS = {  # asked only when there is history to judge them by
    "fast_reaction": ("Do people usually react to this rule's alerts within minutes?",
                      "people usually react within minutes"),
    "self_resolving": ("Are most of this rule's alerts left to resolve on their own?",
                       "most alerts are left to resolve on their own"),
    "noisy": ("Does this rule fire so often that its alerts look like noise?",
              "the rule fires so often it looks like noise"),
}

UPSERT = """
INSERT INTO alert_history AS h (alert_id, rule_id, team_id, severity, count, opened_at, acked_at, acked_by,
                                resolved_at, resolved_by, reopened)
VALUES (%(alert_id)s, %(rule_id)s, %(team_id)s, %(severity)s, %(count)s, %(opened_at)s, %(acked_at)s, %(acked_by)s,
        %(resolved_at)s, %(resolved_by)s, %(reopened)s)
ON CONFLICT (alert_id) DO UPDATE SET
    team_id = COALESCE(EXCLUDED.team_id, h.team_id),
    severity = COALESCE(EXCLUDED.severity, h.severity),
    count = GREATEST(h.count, EXCLUDED.count),
    opened_at = COALESCE(h.opened_at, EXCLUDED.opened_at),
    acked_by = CASE WHEN h.acked_at IS NULL THEN EXCLUDED.acked_by ELSE h.acked_by END,
    acked_at = COALESCE(h.acked_at, EXCLUDED.acked_at),
    resolved_by = CASE WHEN EXCLUDED.resolved_at >= COALESCE(h.resolved_at, '-infinity') THEN EXCLUDED.resolved_by
                       ELSE h.resolved_by END,
    resolved_at = GREATEST(h.resolved_at, EXCLUDED.resolved_at),
    reopened = h.reopened OR EXCLUDED.reopened"""

STATS = """
WITH r AS (SELECT * FROM alert_history WHERE rule_id = %s AND resolved_at >= %s
           ORDER BY resolved_at DESC LIMIT %s)
SELECT count(*),
       count(*) FILTER (WHERE acked_at IS NOT NULL),
       percentile_cont(0.5) WITHIN GROUP (ORDER BY extract(epoch FROM acked_at - opened_at) / 60),
       count(*) FILTER (WHERE resolved_by = 'auto' AND acked_at IS NULL),
       count(*) FILTER (WHERE reopened),
       percentile_cont(0.5) WITHIN GROUP (ORDER BY count),
       (SELECT jsonb_object_agg(severity, n) FROM (SELECT severity, count(*) n FROM r
                                                   WHERE severity IS NOT NULL GROUP BY severity) s)
FROM r"""


def _ts(v) -> datetime | None:
    try:
        return datetime.fromisoformat(v) if type(v) is str else None
    except ValueError:
        return None


def _example(alert) -> dict | None:
    """What the judge shows Jev of one alert; None for an id-only record."""
    if type(alert) is not dict or "title" not in alert:
        return None
    ev = alert.get("sample_event") if type(alert.get("sample_event")) is dict else {}
    return {k: alert.get(k) for k in ("title", "body", "fields")} | {k: ev.get(k) for k in
                                                                       ("type", "service", "env", "severity")}


def record(conn, d: dict) -> tuple | None:
    """events.md §4 record -> its row in `alert_history`; a `severity` job (id, kind, input) when it is a trigger."""
    change, at, actor, alert = d.get("change"), _ts(d.get("at")), d.get("actor"), d.get("alert")
    alert_id = alert.get("id") if type(alert) is dict else None
    if type(alert_id) is not str or not alert_id or at is None:
        return None
    prev = conn.execute("SELECT rule_id FROM alert_history WHERE alert_id = %s", (alert_id,)).fetchone()
    rule_id = alert.get("rule_id") if type(alert.get("rule_id")) is str else prev and prev[0]
    if not rule_id:  # an id-only record (the api's acked / resolved) for an alert opened before we listened
        return None
    actor = actor if type(actor) is str else None
    conn.execute(UPSERT, {
        "alert_id": alert_id, "rule_id": rule_id,
        "team_id": alert.get("team_id") if type(alert.get("team_id")) is str else None,
        "severity": alert.get("severity") if alert.get("severity") in SEVERITIES else None,
        "count": alert.get("count") if type(alert.get("count")) is int else None,
        "opened_at": _ts(alert.get("first_received_at")) or (at if change == "opened" else None),
        "acked_at": at if change == "acked" else None, "acked_by": actor if change == "acked" else None,
        "resolved_at": at if change == "resolved" else None, "resolved_by": actor if change == "resolved" else None,
        "reopened": change == "reopened"})
    trigger = {"rule_id": rule_id, "alert_id": alert_id, "example": _example(alert)}
    if change == "opened" and not conn.execute(
            "SELECT 1 FROM alert_history WHERE rule_id = %s AND resolved_at IS NOT NULL LIMIT 1", (rule_id,)).fetchone():
        return f"severity:{rule_id}:first", "severity", trigger | {"mode": "first"}
    if change == "resolved":
        n = conn.execute("SELECT count(*) FROM alert_history WHERE rule_id = %s AND resolved_at IS NOT NULL",
                         (rule_id,)).fetchone()[0]
        if n >= EVERY:  # keyed by bucket: repeats are no-ops, and a bucket a crash or a race skipped comes next time
            return f"severity:{rule_id}:{n // EVERY * EVERY}", "severity", trigger | {"mode": "history"}
    return None


def stats(conn, rule_id: str, now: datetime) -> dict:
    """How people reacted to the rule's most recent resolved alerts (at most MAX_ALERTS, MAX_AGE_DAYS old)."""
    n, acked, ack_min, auto, reopened, med_count, sevs = conn.execute(
        STATS, (rule_id, now - timedelta(days=MAX_AGE_DAYS), MAX_ALERTS)).fetchone()
    share = (lambda k: round(k / n, 3)) if n else (lambda k: None)
    return {"window": {"max_alerts": MAX_ALERTS, "max_age_days": MAX_AGE_DAYS}, "resolved_alerts": n,
            "acked_share": share(acked), "median_minutes_to_ack": None if ack_min is None else round(ack_min, 1),
            "auto_resolved_unacked_share": share(auto), "reopened_share": share(reopened),
            "median_count": med_count, "severities": sevs or {}}


def _stat_reasons(s: dict) -> list[str]:
    n = s["resolved_alerts"]
    k, auto, re = (round(s[f] * n) for f in ("acked_share", "auto_resolved_unacked_share", "reopened_share"))
    out = [f"{k} of {n} alerts were acknowledged by a person"
           + (f", median {s['median_minutes_to_ack']:g} min" if k and s["median_minutes_to_ack"] is not None else "")]
    out.append(f"{auto} of {n} resolved on their own without anyone acknowledging them")
    if re:
        out.append(f"{re} of {n} were reopened")
    if s["median_count"] is not None:
        out.append(f"median {s['median_count']:g} event{'' if s['median_count'] == 1 else 's'} per alert")
    return out


def ask_jev(state, questions: dict) -> dict:
    """One `POST /v1/systemone`. A 4xx other than 429 is permanent (JobFailed); the rest is retried by the queue."""
    body = {"model": os.environ.get("TYPESAFE_MODEL") or "jev-latest", "state": state, "questions": questions}
    base = (os.environ.get("TYPESAFE_BASE_URL") or "https://api.typesafe.ai").rstrip("/")
    req = urllib.request.Request(base + "/v1/systemone", data=json.dumps(body).encode(), method="POST",
                                 headers={"Authorization": f"Bearer {os.environ['TYPESAFE_API_KEY']}",
                                          "Content-Type": "application/json", "Accept": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=TIMEOUT_S) as resp:
            return json.loads(resp.read())
    except urllib.error.HTTPError as e:
        raw = e.read()
        try:
            detail = json.loads(raw).get("detail")
        except (ValueError, AttributeError):
            detail = raw.decode(errors="replace")
        if 400 <= e.code < 500 and e.code != 429:
            raise JobFailed(f"jev {e.code}: {str(detail)[:500]}") from None
        raise RuntimeError(f"jev {e.code}: {str(detail)[:500]}") from None


def _prob(v) -> bool:
    return type(v) in (int, float) and 0 <= v <= 1


def _answers(resp, factors: dict, choice: str = "severity", options: tuple = SEVERITIES) -> dict:
    """The Jev response's answers, checked (a trust boundary); a malformed one raises ValueError, which is retried."""
    a = resp.get("answers") if type(resp) is dict else None
    sev = a.get(choice) if type(a) is dict else None
    if not (type(sev) is dict and sev.get("choice") in options and _prob(sev.get("confidence"))
            and type(sev.get("probabilities")) is dict and all(_prob(sev["probabilities"].get(s)) for s in options)
            and all(type(a.get(f)) is dict and _prob(a[f].get("noul")) for f in factors)):
        raise ValueError(f"malformed Jev response: {json.dumps(resp)[:500]}")
    return a


def _cap(d: dict) -> dict:
    return {k: v if len(json.dumps(v, ensure_ascii=False)) <= PART_CAP else _capped(v, PART_CAP)
            for k, v in d.items()}


def handle(job_input: dict, ctx) -> dict:
    rule_id, alert_id, mode = job_input["rule_id"], job_input["alert_id"], job_input["mode"]
    rule = ctx.rules.get(rule_id)
    if rule is None or rule.get("enabled") is False:
        return {"proposed": None, "why": f"rule {rule_id} is unknown, deleted or disabled"}
    s = None
    if mode == "history":
        s = ctx.rule_stats(rule_id)
        if s["resolved_alerts"] < EVERY:
            return {"proposed": None, "why": f"not enough recent history: {s['resolved_alerts']} resolved alerts in "
                                             f"the last {MAX_AGE_DAYS} days, {EVERY} needed"}
    example = job_input.get("example")
    if example is None:  # the trigger was an id-only record: ask the api for the alert
        try:
            example = _example(ctx.api.get(f"/alerts/{quote(alert_id, safe='')}")["alert"])
        except ApiError as e:
            if e.status != 404:
                raise
    current = rule.get("severity")
    present = rule.get("present") if type(rule.get("present")) is dict else {}
    state = {"team": rule.get("team_id"),
             "rule": _cap({"id": rule_id, "name": rule.get("name"), "when": rule.get("when"),
                           "route": rule.get("route"), "title_template": present.get("title"), "severity": current}),
             "example_alert": example and _cap(example), "history": s}
    factors = FACTORS | (HISTORY_FACTORS if s else {})
    questions = {"severity": SEVERITY_QUESTION} | {k: {"type": "noul", "instructions": q}
                                                   for k, (q, _) in factors.items()}
    a = _answers(ctx.ask_jev(state, questions), factors)
    sev = a["severity"]
    answer = {"choice": sev["choice"], "confidence": sev["confidence"],
              "probabilities": {k: sev["probabilities"][k] for k in SEVERITIES},
              "factors": {k: a[k]["noul"] for k in factors}}
    if sev["choice"] == current or sev["confidence"] < MIN_CONFIDENCE:
        why = "same as the rule's severity" if sev["choice"] == current else f"confidence below {MIN_CONFIDENCE}"
        return {"proposed": None, "why": why, "answer": answer}
    reasons = [phrase for k, (_, phrase) in factors.items() if a[k]["noul"] >= FACTOR_P] + (_stat_reasons(s) if s else [])
    p = file_proposal(ctx.api, alert_id, "rule_severity", {
        "rule_id": rule_id, "team_id": rule.get("team_id"), "severity": sev["choice"], "current_severity": current,
        "confidence": sev["confidence"], "probabilities": answer["probabilities"], "mode": mode, "stats": s,
        "reasons": reasons}, ctx.job_id)
    return {"proposal_id": p["id"], "proposed": sev["choice"], "answer": answer}
