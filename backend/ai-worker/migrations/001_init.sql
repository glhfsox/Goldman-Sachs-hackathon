-- The ai-worker's own database; no other service reads it.
-- The schema it implements is described in contracts/schemas/ai-worker.md: change both together.

CREATE TABLE IF NOT EXISTS jobs (
    id          text PRIMARY KEY,           -- schema:<fingerprint> | runbook:<id>:<version> | proposal:<alert id>
    kind        text NOT NULL,              -- mapping | runbook | proposal
    input       jsonb NOT NULL,
    status      text NOT NULL,              -- pending | done | failed (a running job is a row lock, not a status)
    attempts    int NOT NULL DEFAULT 0,
    result      jsonb,
    error       text,
    created_at  timestamptz NOT NULL DEFAULT now(),
    finished_at timestamptz
);

CREATE INDEX IF NOT EXISTS jobs_pending ON jobs (created_at) WHERE status = 'pending';
