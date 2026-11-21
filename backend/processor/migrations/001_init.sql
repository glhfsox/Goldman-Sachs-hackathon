-- The processor's own database; no other service reads it.
-- The schema it implements is described in contracts/schemas/processor.md: change both together.

CREATE TABLE IF NOT EXISTS alerts (
    id                text PRIMARY KEY,
    alert_key         text NOT NULL,          -- internal: RuleMatch.alert_key
    rule_id           text NOT NULL,
    team_id           text NOT NULL,
    incident_id       text,
    status            text NOT NULL,          -- open | acked | resolved
    severity          text NOT NULL,
    title             text NOT NULL,
    body              text NOT NULL,
    fields            jsonb NOT NULL,
    color             text,
    count             bigint NOT NULL,
    first_seen        timestamptz NOT NULL,
    last_seen         timestamptz NOT NULL,
    first_received_at timestamptz NOT NULL,
    last_received_at  timestamptz NOT NULL,   -- internal: window arithmetic uses ingest time
    runbook_id        text,
    acked_by          text,
    resolved_by       text,
    sample_event      jsonb NOT NULL,
    opened_by_event   text NOT NULL,          -- internal: event that last opened or reopened the alert
    opened_partition  int NOT NULL,           -- internal: Kafka position of opened_by_event, seeds its watermark
    opened_offset     bigint NOT NULL,
    window_s          int NOT NULL            -- internal: the rule's window_s when the alert last opened, for auto-resolve
);

-- Postgres decides alert existence: at most one non-resolved alert per key.
CREATE UNIQUE INDEX IF NOT EXISTS alerts_live_key ON alerts (alert_key) WHERE status <> 'resolved';

-- Transactional outbox: written in the same transaction as the alert or sample, published by a relay.
CREATE TABLE IF NOT EXISTS outbox (
    id      bigserial PRIMARY KEY,            -- the relay publishes in id order
    topic   text NOT NULL,
    key     text NOT NULL,
    payload jsonb NOT NULL
);

-- Outbox rows Kafka rejected for good (too large, invalid); moved here so they do not block the rows behind them.
CREATE TABLE IF NOT EXISTS outbox_dead (
    id        bigint PRIMARY KEY,             -- the outbox id
    topic     text NOT NULL,
    key       text NOT NULL,
    payload   jsonb NOT NULL,
    error     text NOT NULL,
    failed_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS schema_samples (
    fingerprint text PRIMARY KEY,
    source_id   text NOT NULL,
    created_at  timestamptz NOT NULL
);
