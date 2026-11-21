-- How people reacted to each alert, fed from alerts.changes; the severity judge reads it (contracts/schemas/ai-worker.md).
-- Every column is set-once, a flag, or a latest value, so a redelivered record changes nothing.

CREATE TABLE IF NOT EXISTS alert_history (
    alert_id    text PRIMARY KEY,
    rule_id     text NOT NULL,
    team_id     text,
    severity    text,                       -- latest seen
    count       bigint,                     -- highest seen
    opened_at   timestamptz,                -- the alert's first_received_at, else the `at` of its `opened` change
    acked_at    timestamptz,                -- first `acked` change
    acked_by    text,
    resolved_at timestamptz,                -- latest `resolved` change
    resolved_by text,                       -- a user id, or `auto`
    reopened    boolean NOT NULL DEFAULT false
);

CREATE INDEX IF NOT EXISTS alert_history_resolved ON alert_history (rule_id, resolved_at DESC)
    WHERE resolved_at IS NOT NULL;
