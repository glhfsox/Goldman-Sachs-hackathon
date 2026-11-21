import psycopg
import pytest

from ai_worker import jobs, main
from ai_worker.agent import JobFailed
from ai_worker.api import ApiError


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
    job = jobs.claim(jobs_pg)
    assert job["id"] == "schema:a" and job["input"] == {"n": 1}
    with psycopg.connect(db) as other:  # a second replica skips the locked row
        assert jobs.claim(other) is None
    jobs.finish(jobs_pg, job, result={"mapping_id": "m"})
    assert rows(pg) == [("schema:a", "done", 1, {"mapping_id": "m"}, None)]
    assert jobs.claim(jobs_pg) is None


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
    w = main.Worker(pg, jobs_pg, None, None, None, api="api")
    w.mappings = {"m1": {"id": "m1"}}
    return w, pg, behaviour, calls


def boom(*_):
    raise ConnectionError("api down")


def test_retryable_failure_returns_to_pending_and_fails_at_three(worker):
    w, pg, behaviour, _ = worker
    jobs.enqueue(pg, "schema:a", "mapping", {})
    behaviour += [boom, boom, boom]
    assert w.run_one() == main.RETRY_S
    assert rows(pg)[0][1:3] == ("pending", 1)
    w.run_one()
    w.run_one()
    assert rows(pg)[0][1:3] == ("failed", 3) and "api down" in rows(pg)[0][4]
    assert w.run_one() == main.IDLE_S


@pytest.mark.parametrize("exc", [JobFailed("bad sample"), ApiError(422, {"error": "invalid_mapping"})])
def test_permanent_failure_fails_at_once(worker, exc):
    w, pg, behaviour, _ = worker
    jobs.enqueue(pg, "schema:a", "mapping", {})

    def fail(*_):
        raise exc
    behaviour.append(fail)
    w.run_one()
    assert rows(pg)[0][1:3] == ("failed", 1)


def test_success_stores_result_and_ctx_has_its_parts(worker):
    w, pg, behaviour, calls = worker
    jobs.enqueue(pg, "schema:a", "mapping", {"n": 1})
    jobs.enqueue(pg, "schema:b", "mapping", {"n": 2})
    behaviour += [lambda job_input, ctx: {"mapping_id": "m", "others": ctx.other_samples()}]
    assert w.run_one() == 0
    ctx = calls[0]
    assert ctx.api == "api" and ctx.mappings == [{"id": "m1"}] and callable(ctx.run_agent)
    assert rows(pg)[0] == ("schema:a", "done", 1, {"mapping_id": "m", "others": [{"n": 2}]}, None)
