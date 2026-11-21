"""The job queue in table `jobs` (contracts/schemas/ai-worker.md).

`claim` and `finish` run on a connection with autocommit off: the claimed row stays locked in one open transaction
for the whole job, so a crash releases it (still `pending`) and no `running` state or stale-job sweep is needed.
A retried job waits until `run_after`: exponential backoff with jitter per job, so one failing job never pauses the rest.
"""

import random

from psycopg.pq import TransactionStatus
from psycopg.rows import dict_row
from psycopg.types.json import Jsonb

MAX_ATTEMPTS = 6
BACKOFF_S, BACKOFF_MAX_S = 10.0, 300.0  # 10, 20, 40, 80, 160 s (x0.5-1.5 jitter): about 5 minutes before `failed`
RUN_RISK_BACKOFF_S = 2.0  # flat: a run_risk job is a no-op after its run's start_at, a long wait would only waste it
CHECKPOINT_TABLES = ("checkpoint_writes", "checkpoint_blobs", "checkpoints")  # LangGraph's, thread_id = job id


def backoff(kind: str, attempts: int) -> float:
    """Seconds before the next try of a job that has failed `attempts` times."""
    if kind == "run_risk":
        return RUN_RISK_BACKOFF_S
    return min(BACKOFF_MAX_S, BACKOFF_S * 2 ** (attempts - 1)) * random.uniform(0.5, 1.5)


def enqueue(conn, job_id: str, kind: str, input: dict) -> bool:
    """Insert a pending job; False when the id already exists (a redelivered record changes nothing)."""
    cur = conn.execute("INSERT INTO jobs (id, kind, input, status) VALUES (%s, %s, %s, 'pending')"
                       " ON CONFLICT (id) DO NOTHING", (job_id, kind, Jsonb(input)))
    return cur.rowcount == 1


def claim(conn, kinds: list[str]) -> dict | None:
    """Lock one pending job of one of `kinds` and return it, leaving the transaction open until `finish`; None when
    there is none. `run_risk` jobs go first: they are useless after the run's `start_at`."""
    row = conn.cursor(row_factory=dict_row).execute(
        "SELECT id, kind, input, attempts FROM jobs WHERE status = 'pending' AND kind = ANY(%s)"
        " AND (run_after IS NULL OR run_after <= now())"
        " ORDER BY kind <> 'run_risk', attempts, created_at LIMIT 1 FOR UPDATE SKIP LOCKED", (kinds,)).fetchone()
    if row is None:
        conn.rollback()
    return row


def finish(conn, job: dict, result: dict | None = None, error: str | None = None,
           retry_in: float | None = None) -> str:
    """Record the outcome of a claimed job and commit: done with `result`; or failed with `error`, unless `retry_in`
    is given and attempts are left, then back to pending for `retry_in` seconds. A done or failed job's checkpoints
    are deleted in the same transaction; a pending one keeps them, so the retry resumes. Returns the new status."""
    if conn.info.transaction_status == TransactionStatus.INERROR:
        conn.rollback()  # the job's own statement failed on this connection; the row is still pending
    attempts = job["attempts"] + 1
    status = "done" if error is None else "pending" if retry_in is not None and attempts < MAX_ATTEMPTS else "failed"
    conn.execute("UPDATE jobs SET status = %s, attempts = %s, result = %s, error = %s,"
                 " run_after = CASE WHEN %s = 'pending' THEN clock_timestamp() + make_interval(secs => %s) END,"
                 " finished_at = CASE WHEN %s = 'pending' THEN NULL ELSE clock_timestamp() END WHERE id = %s",
                 (status, attempts, None if result is None else Jsonb(result), error, status, retry_in or 0, status,
                  job["id"]))
    if status != "pending":
        for t in CHECKPOINT_TABLES:
            conn.execute(f"DELETE FROM {t} WHERE thread_id = %s", (job["id"],))
    conn.commit()
    return status


def other_samples(conn, job_id: str) -> list[dict]:
    """The `input` of every mapping job except this one."""
    return [r[0] for r in conn.execute("SELECT input FROM jobs WHERE kind = 'mapping' AND id <> %s ORDER BY created_at",
                                       (job_id,))]
