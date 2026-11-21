import json
import urllib.error

import anthropic
import httpx2
import psycopg
import pytest
from langgraph.checkpoint.memory import InMemorySaver

from ai_worker import agent, jobs, judge, main
from ai_worker.agent import JobFailed
from ai_worker.api import ApiError

KINDS = list(main.HANDLERS)


def rows(pg):
    return pg.execute("SELECT id, status, attempts, result, error FROM jobs ORDER BY id").fetchall()


def test_enqueue_twice_is_one_row(conns):
    pg, _ = conns
    assert jobs.enqueue(pg, "schema:a", "mapping", {"n": 1})
    assert not jobs.enqueue(pg, "schema:a", "mapping", {"n": 2})
    assert rows(pg) == [("schema:a", "pending", 0, None, None)]


def test_claim_locks_the_row_until_finish(conns, db):
    pg, jobs_pg = conns
    jobs.enqueue(pg, "schema:a", "mapping", {"n": 1})
    job = jobs.claim(jobs_pg, KINDS)
    assert job["id"] == "schema:a" and job["input"] == {"n": 1}
    with psycopg.connect(db) as other:  # a second replica skips the locked row
        assert jobs.claim(other, KINDS) is None
    jobs.finish(jobs_pg, job, result={"mapping_id": "m"})
    assert rows(pg) == [("schema:a", "done", 1, {"mapping_id": "m"}, None)]
    assert jobs.claim(jobs_pg, KINDS) is None


def test_finished_at_is_the_end_of_the_job_not_the_claim(conns):
    pg, jobs_pg = conns
    jobs.enqueue(pg, "schema:a", "mapping", {})
    job = jobs.claim(jobs_pg, KINDS)
    after_claim = pg.execute("SELECT clock_timestamp()").fetchone()[0]
    jobs.finish(jobs_pg, job, result={})
    assert pg.execute("SELECT finished_at FROM jobs").fetchone()[0] > after_claim


@pytest.mark.parametrize("bad", ["marek\x00", "\ud800"])
def test_a_record_postgres_cannot_store_is_skipped(worker, bad):
    w, pg, _, _ = worker
    alert = {"id": "A1", "rule_id": "r1", "title": "t"}
    w._change({"change_id": "c1", "change": "resolved", "at": "2026-11-20T10:00:00Z", "actor": bad, "alert": alert})
    w._change({"change_id": "c2", "change": "resolved", "at": "2026-11-20T10:00:00Z", "actor": "marek", "alert": alert})
    assert pg.execute("SELECT resolved_by FROM alert_history").fetchall() == [("marek",)]
    assert [r[0] for r in rows(pg)] == ["proposal:A1"]


def test_claim_takes_only_the_given_kinds(conns):
    pg, jobs_pg = conns
    jobs.enqueue(pg, "schema:a", "mapping", {})
    jobs.enqueue(pg, "severity:r1:first", "severity", {})
    assert jobs.claim(jobs_pg, ["severity"])["id"] == "severity:r1:first"
    jobs_pg.rollback()
    assert jobs.claim(jobs_pg, []) is None


def test_other_samples(conns):
    pg, jobs_pg = conns
    jobs.enqueue(pg, "schema:a", "mapping", {"n": 1})
    jobs.enqueue(pg, "schema:b", "mapping", {"n": 2})
    jobs.enqueue(pg, "proposal:x", "proposal", {"alert_id": "x"})
    assert jobs.other_samples(jobs_pg, "schema:a") == [{"n": 2}]


@pytest.fixture
def worker(conns, monkeypatch):
    pg, jobs_pg = conns
    calls = []

    def handler(job_input, ctx):
        calls.append(ctx)
        return behaviour.pop(0)(job_input, ctx)

    behaviour = []
    monkeypatch.setitem(main.HANDLERS, "mapping", handler)
    saver = InMemorySaver()
    monkeypatch.setattr(agent, "_saver", lambda: saver)
    w = main.Worker(pg, jobs_pg, None, None, None, api="api")
    w.mappings = {"m1": {"id": "m1"}}
    return w, pg, behaviour, calls


def boom(*_):
    raise ConnectionError("api down")


def due(pg):
    pg.execute("UPDATE jobs SET run_after = now()")


def test_retryable_failure_backs_off_and_fails_at_max_attempts(worker):
    w, pg, behaviour, _ = worker
    jobs.enqueue(pg, "schema:a", "mapping", {})
    behaviour += [boom] * jobs.MAX_ATTEMPTS
    assert w.run_one() == 0
    assert rows(pg)[0][1:3] == ("pending", 1)
    wait_s = pg.execute("SELECT extract(epoch FROM run_after - now()) FROM jobs").fetchone()[0]
    assert jobs.BACKOFF_S * 0.5 - 1 < wait_s <= jobs.BACKOFF_S * 1.5
    assert w.run_one() == main.IDLE_S  # not due yet: nothing is claimed
    for _ in range(jobs.MAX_ATTEMPTS - 1):
        due(pg)
        w.run_one()
    assert rows(pg)[0][1:3] == ("failed", jobs.MAX_ATTEMPTS) and "api down" in rows(pg)[0][4]
    due(pg)
    assert w.run_one() == main.IDLE_S


def test_backoff_grows_with_a_cap_and_run_risk_stays_short():
    assert all(jobs.BACKOFF_S * 0.5 <= jobs.backoff("mapping", 1) <= jobs.BACKOFF_S * 1.5 for _ in range(50))
    assert all(jobs.backoff("severity", 4) >= jobs.BACKOFF_S * 4 for _ in range(50))
    assert all(jobs.backoff("mapping", 30) <= jobs.BACKOFF_MAX_S * 1.5 for _ in range(50))
    assert jobs.backoff("run_risk", 5) == jobs.RUN_RISK_BACKOFF_S


def test_a_due_run_risk_job_goes_first_and_a_backed_off_one_waits(conns):
    pg, jobs_pg = conns
    jobs.enqueue(pg, "schema:a", "mapping", {})
    jobs.enqueue(pg, "run_risk:r1", "run_risk", {})
    assert jobs.claim(jobs_pg, KINDS)["id"] == "run_risk:r1"
    jobs_pg.rollback()
    pg.execute("UPDATE jobs SET run_after = now() + interval '1 minute' WHERE kind = 'run_risk'")
    assert jobs.claim(jobs_pg, KINDS)["id"] == "schema:a"
    jobs_pg.rollback()


def _http_error(cls, status):
    req = httpx2.Request("POST", "https://api.anthropic.com/v1/messages")
    return cls("x", response=httpx2.Response(status, request=req), body=None)


@pytest.mark.parametrize("exc, status", [
    (JobFailed("bad sample"), "failed"),
    (ApiError(422, {"error": "invalid_mapping"}), "failed"),
    (_http_error(anthropic.BadRequestError, 400), "failed"),
    (_http_error(anthropic.AuthenticationError, 401), "failed"),
    (_http_error(anthropic.RateLimitError, 429), "pending"),
    (_http_error(anthropic.APIStatusError, 529), "pending"),
    (_http_error(anthropic.InternalServerError, 500), "pending"),
    (anthropic.APIConnectionError(request=httpx2.Request("POST", "https://api.anthropic.com")), "pending"),
    (anthropic.APITimeoutError(request=httpx2.Request("POST", "https://api.anthropic.com")), "pending"),
    (urllib.error.HTTPError("http://api/x", 503, "down", None, None), "pending"),  # api 5xx
    (urllib.error.URLError("refused"), "pending"),
    (TimeoutError("timed out"), "pending"),
])
def test_retry_classification(worker, exc, status):
    w, pg, behaviour, _ = worker
    jobs.enqueue(pg, "schema:a", "mapping", {})

    def fail(*_):
        raise exc
    behaviour.append(fail)
    w.run_one()
    assert rows(pg)[0][1:3] == (status, 1)


def checkpoint(pg, thread_id):
    pg.execute("INSERT INTO checkpoints (thread_id, checkpoint_ns, checkpoint_id, checkpoint, metadata)"
               " VALUES (%s, '', 'c1', '{}', '{}')", (thread_id,))
    return lambda: pg.execute("SELECT count(*) FROM checkpoints WHERE thread_id = %s", (thread_id,)).fetchone()[0]


def test_finish_deletes_checkpoints_of_a_finished_job_and_keeps_them_for_a_retry(conns):
    pg, jobs_pg = conns
    for jid in ("schema:a", "schema:b"):
        jobs.enqueue(pg, jid, "mapping", {})
    left_a, left_b = checkpoint(pg, "schema:a"), checkpoint(pg, "schema:b")
    jobs.finish(jobs_pg, jobs.claim(jobs_pg, KINDS), error="boom", retry_in=1)
    assert left_a() == 1
    jobs.finish(jobs_pg, jobs.claim(jobs_pg, KINDS), result={})  # schema:b: a's retry is not due yet
    assert left_b() == 0
    due(pg)
    jobs.finish(jobs_pg, jobs.claim(jobs_pg, KINDS), error="bad")
    assert left_a() == 0 and rows(pg)[0][1] == "failed"


def test_a_dropped_connection_leaves_the_job_pending_and_reconnect_finishes_it(worker, db, monkeypatch, metrics_file):
    w, pg, behaviour, _ = worker
    monkeypatch.setattr(agent, "reset", lambda: None)
    jobs.enqueue(pg, "schema:a", "mapping", {})

    def drop(*_):  # the database restarts while the job runs
        pg.execute("SELECT pg_terminate_backend(%s)", (w.jobs_pg.info.backend_pid,))
        return {"mapping_id": "m"}
    behaviour += [drop, lambda *_: {"mapping_id": "m"}]
    with pytest.raises(psycopg.OperationalError):
        w.run_one()
    assert rows(pg)[0][1:3] == ("pending", 0)  # the lock went with the connection; no attempt counted
    w.reconnect(db)
    assert w.run_one() == 0
    assert rows(w.pg)[0][1:3] == ("done", 1)
    lines = [json.loads(x) for x in metrics_file.read_text().splitlines()]
    assert [(x["outcome"], x["error_class"], x["attempt"]) for x in lines] == [
        ("retry", "AdminShutdown", 1), ("done", None, 1)]  # an OperationalError


def test_a_statement_that_always_fails_counts_an_attempt_instead_of_reconnecting(worker, metrics_file):
    w, pg, behaviour, _ = worker
    jobs.enqueue(pg, "schema:a", "mapping", {})

    def deadlock(*_):  # an OperationalError of the job's own statement, on the claim connection
        w.jobs_pg.execute("DO $$ BEGIN RAISE EXCEPTION USING ERRCODE = '40P01'; END $$")
    behaviour.append(deadlock)
    w.run_one()
    status, attempts = rows(pg)[0][1:3]
    assert (status, attempts) == ("pending", 1)


class FakeMsg:
    def __init__(self, offset, value):
        self._offset, self._value = offset, value

    def error(self):
        return None

    def topic(self):
        return "schema.samples"

    def partition(self):
        return 0

    def offset(self):
        return self._offset

    def value(self):
        return self._value


class FakeConsumer:
    def __init__(self, msgs):
        self.msgs, self.seeks, self.commits = msgs, [], 0

    def consume(self, num_messages, timeout):
        msgs, self.msgs = self.msgs, []
        return msgs

    def seek(self, tp):
        self.seeks.append((tp.topic, tp.partition, tp.offset))

    def commit(self, asynchronous):
        self.commits += 1


def test_a_database_error_while_enqueuing_rewinds_the_batch(worker, monkeypatch):
    w, *_ = worker
    sample = json.dumps({"fingerprint": "f", "source_id": "s", "sample": {}}).encode()
    w.samples, w.changes, w.config = FakeConsumer([FakeMsg(7, sample), FakeMsg(8, sample)]), FakeConsumer([]), \
        FakeConsumer([])

    def down(*_):
        raise psycopg.OperationalError("server closed the connection")
    monkeypatch.setattr(jobs, "enqueue", down)
    with pytest.raises(psycopg.OperationalError):
        w.poll(0)
    assert w.samples.seeks == [("schema.samples", 0, 7)] and w.samples.commits == 0


def test_metrics_lines_for_claude_and_jev_jobs(worker, monkeypatch, metrics_file):
    w, pg, behaviour, _ = worker
    monkeypatch.setitem(main.HANDLERS, "severity", lambda i, ctx: {"proposal_id": "p1", **ctx.ask_jev({}, {})})
    monkeypatch.setattr(judge, "ask_jev", lambda s, q: {"answer": {"choice": "critical", "confidence": 0.8}})
    jobs.enqueue(pg, "schema:a", "mapping", {})
    jobs.enqueue(pg, "severity:r1:first", "severity", {})
    behaviour.append(lambda i, ctx: {"mapping_id": "m"})
    w.run_one()
    w.run_one()
    lines = [json.loads(x) for x in metrics_file.read_text().splitlines()]
    claude, jev = sorted(lines, key=lambda x: x["kind"])
    assert claude["job_id"] == "schema:a" and claude["outcome"] == "done" and claude["attempt"] == 1
    assert {"ts", "duration_ms", "model", "model_calls", "model_calls_total", "resumed_from_checkpoint", "tool_calls",
            "tool_errors", "input_tokens", "output_tokens", "cache_read_input_tokens", "cache_creation_input_tokens",
            "cost_usd", "sdk_retries", "stop_reason", "error_class"} <= claude.keys()
    assert jev["kind"] == "severity" and jev["jev_calls"] == 1 and jev["jev_latency_ms"] >= 0
    assert (jev["choice"], jev["confidence"], jev["posted"]) == ("critical", 0.8, True) and "model" not in jev


def test_a_metrics_write_failure_never_breaks_the_job(worker, monkeypatch, tmp_path):
    w, pg, behaviour, _ = worker
    monkeypatch.setenv("AGENT_METRICS_FILE", str(tmp_path))  # a directory: open() fails
    jobs.enqueue(pg, "schema:a", "mapping", {})
    behaviour.append(lambda i, ctx: {"mapping_id": "m"})
    assert w.run_one() == 0 and rows(pg)[0][1] == "done"


def test_success_stores_result_and_ctx_has_its_parts(worker):
    w, pg, behaviour, calls = worker
    jobs.enqueue(pg, "schema:a", "mapping", {"n": 1})
    jobs.enqueue(pg, "schema:b", "mapping", {"n": 2})
    behaviour += [lambda job_input, ctx: {"mapping_id": "m", "others": ctx.other_samples()}]
    assert w.run_one() == 0
    ctx = calls[0]
    assert ctx.api == "api" and ctx.mappings == [{"id": "m1"}] and callable(ctx.run_agent)
    assert rows(pg)[0] == ("schema:a", "done", 1, {"mapping_id": "m", "others": [{"n": 2}]}, None)


@pytest.mark.parametrize("exc, down", [
    (psycopg.OperationalError("server closed the connection unexpectedly"), True),
    (psycopg.InterfaceError("the connection is closed"), True),
    (psycopg.errors.AdminShutdown("terminating connection"), True),
    (psycopg.errors.ProgramLimitExceeded("index row size exceeds"), False),
    (psycopg.errors.DeadlockDetected("deadlock detected"), False),
    (psycopg.errors.QueryCanceled("canceling statement"), False)])
def test_only_a_lost_database_means_reconnect(exc, down):
    assert main._db_down(exc) is down
