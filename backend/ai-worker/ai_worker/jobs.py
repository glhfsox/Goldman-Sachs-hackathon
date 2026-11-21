"""The job queue in table `jobs` (contracts/schemas/ai-worker.md).

`claim` and `finish` run on a connection with autocommit off: the claimed row stays locked in one open transaction
for the whole job, so a crash releases it (still `pending`) and no `running` state or stale-job sweep is needed.
"""

from psycopg.rows import dict_row
from psycopg.types.json import Jsonb

MAX_ATTEMPTS = 3


def enqueue(conn, job_id: str, kind: str, input: dict) -> bool:
    """Insert a pending job; False when the id already exists (a redelivered record changes nothing)."""
    cur = conn.execute("INSERT INTO jobs (id, kind, input, status) VALUES (%s, %s, %s, 'pending')"
                       " ON CONFLICT (id) DO NOTHING", (job_id, kind, Jsonb(input)))
    return cur.rowcount == 1


def claim(conn) -> dict | None:
    """Lock one pending job and return it, leaving the transaction open until `finish`; None when there is none."""
    row = conn.cursor(row_factory=dict_row).execute(
        "SELECT id, kind, input, attempts FROM jobs WHERE status = 'pending'"
        " ORDER BY attempts, created_at LIMIT 1 FOR UPDATE SKIP LOCKED").fetchone()
    if row is None:
        conn.rollback()
    return row


def finish(conn, job: dict, result: dict | None = None, error: str | None = None, retry: bool = False) -> None:
    """Record the outcome of a claimed job and commit: done with `result`; or failed with `error`, unless `retry`
    and attempts are left, then back to pending."""
    attempts = job["attempts"] + 1
    status = "done" if error is None else "pending" if retry and attempts < MAX_ATTEMPTS else "failed"
    conn.execute("UPDATE jobs SET status = %s, attempts = %s, result = %s, error = %s,"
                 " finished_at = CASE WHEN %s = 'pending' THEN NULL ELSE clock_timestamp() END WHERE id = %s",
                 (status, attempts, None if result is None else Jsonb(result), error, status, job["id"]))
    conn.commit()


def other_samples(conn, job_id: str) -> list[dict]:
    """The `input` of every mapping job except this one."""
    return [r[0] for r in conn.execute("SELECT input FROM jobs WHERE kind = 'mapping' AND id <> %s ORDER BY created_at",
                                       (job_id,))]
