-- Finished runbook runs, copied from the api's GET /runs?finished_since= (contracts/schemas/ai-worker.md).
-- One row per run id; a finished run does not change, so a repeat copy changes nothing.

CREATE TABLE IF NOT EXISTS run_history (
    run_id       text PRIMARY KEY,
    alert_id     text NOT NULL,
    rule_id      text NOT NULL,
    runbook_id   text NOT NULL,
    team_id      text,
    trigger      text,                      -- auto | manual
    status       text NOT NULL,             -- done | failed | cancelled
    risk_check   text,                      -- the verdict it got: clear | hold, or null
    actions      jsonb,                     -- [{action, risk}] of its nodes, no params
    start_at     timestamptz,
    approved_by  text,
    approved_at  timestamptz,
    started_at   timestamptz,
    finished_at  timestamptz NOT NULL,
    cancelled_by text
);

CREATE INDEX IF NOT EXISTS run_history_pair ON run_history (rule_id, runbook_id, finished_at DESC);
